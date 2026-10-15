using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Threading;
using EArm.Graph;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Secrets;
using EArm.Core.Types;
using EArm.Engine;
using EArm.Engine.Providers;
using EArm.Expressions.Values;
using GraphApi = EArm.Graph.GraphApi;

namespace EArm.Providers;

/// <summary>
/// <c>Microsoft.Entra/applications/federatedIdentityCredentials</c>.
/// The resource name is <c>&lt;application uniqueName&gt;/&lt;credential name&gt;</c> (nested children are flattened to
/// that form by the loader); the credential name is the Graph <c>name</c>, an alternate key under the application.
/// <para>
/// Every path starts with the parent segment: <c>/applications/{parentId}</c> when the parent's Graph id is known
/// (applied in this run, or read during planning), otherwise the alternate key
/// <c>/applications(uniqueName='…')</c>. Requests per operation:
/// <list type="bullet">
/// <item><b>Read</b>: <c>GET {parent}/federatedIdentityCredentials(name='…')</c> → <c>GET {parent}/federatedIdentityCredentials</c> (the siblings, for the uniqueness and limit checks of <see cref="Plan"/>); a 404 is "does not exist".</item>
/// <item><b>Create</b> and <b>Update</b>: the upsert <c>PATCH {parent}/federatedIdentityCredentials(name='…')</c> + <c>Prefer: create-if-missing</c> (201 = created, body is the object; 204 = merged into the existing one, re-read); when the parent's Graph id was not known, <c>GET /applications(uniqueName='…')?$select=id</c> so the applied key carries <c>parentId</c>.</item>
/// <item><b>Delete</b>: <c>DELETE {parent}/federatedIdentityCredentials(name='…')</c>; a 404 is "already gone".</item>
/// </list>
/// </para>
/// <para>
/// <see cref="Plan"/> refuses (unsupported row) a name outside the overlay pattern, a non-https <c>issuer</c>, an empty
/// <c>subject</c>, <c>claimsMatchingExpression</c> on the v1.0 apiVersion, an <c>issuer + subject</c> pair already used
/// by another credential of the application (live, or declared elsewhere in the same deployment), and a 21st
/// credential. The live siblings come from the list read of <see cref="ReadAsync"/> (kept per parent); the declared
/// siblings are registered per <see cref="PlanOptions"/> instance, which the planner and the executor each create
/// per pass. A rename is a new resource (the id changes): the old credential stays (Complete mode reports it as an orphan; it is
/// never deleted), so a rename that keeps issuer and subject is refused until the old credential is deleted.
/// </para>
/// </summary>
[EntraProvider(ResourceType)]
public sealed class FederatedIdentityCredentialsProvider : ProviderBase, IOrphanCandidateSource
{
    public const string ResourceType = "Microsoft.Entra/applications/federatedIdentityCredentials";

    /// <summary>The navigation segment under the application.</summary>
    public const string ChildSegment = "federatedIdentityCredentials";

    private const string ParentKeyProperty = "uniqueName";

    private readonly ConcurrentDictionary<string, IReadOnlyList<FederatedCredentialSummary>> _siblings = new(StringComparer.Ordinal);

    private readonly ConditionalWeakTable<PlanOptions, DeclaredCredentials> _declared = new();

    public FederatedIdentityCredentialsProvider(ResourceTypeDefinition definition, ResourceTypeCatalog? catalog = null)
        : base(definition, catalog)
    {
        if (!ResourceIdentifier.TypeEquals(definition.Type, ResourceType))
        {
            throw new ArgumentException($"{nameof(FederatedIdentityCredentialsProvider)} implements '{ResourceType}', not '{definition.Type}'.", nameof(definition));
        }
    }

    public FederatedIdentityCredentialsProvider(ResourceTypeCatalog catalog)
        : this(RequireDefinition(catalog), catalog)
    {
    }

    /// <summary>
    /// Same rule as the applications provider, applied to the parent:
    /// <c>Application.ReadWrite.OwnedBy</c> when the parent application is declared in the template (this
    /// deployment creates and therefore owns it), otherwise <c>Application.ReadWrite.All</c>.
    /// </summary>
    public override IReadOnlyList<string> RequiredPermissions(ResourceInstance? desired)
    {
        return desired?.Parent is not null ? [ApplicationsProvider.OwnedByPermission] : [ApplicationsProvider.ReadWriteAllPermission];
    }

    /// <summary>The top-level properties each template declares, selected next to the managed set on its reads (G-08).</summary>
    private readonly DeclaredSelects _selects = new();

    /// <summary>Application object ids found by <c>uniqueName</c>, so a parent is looked up once per run.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _parentIds = new(StringComparer.OrdinalIgnoreCase);

    public override ResourceKey ResolveKey(ResourceInstance desired)
    {
        var key = base.ResolveKey(desired);
        _selects.Register(key, Definition, desired);
        return key;
    }

    // ---- read ----

    public override async Task<ExistingResource?> ReadAsync(ResourceKey key, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(graph);

        if (!key.TryGet("name", out var name))
        {
            throw new ProviderException(EngineDiagnosticCodes.ResourceKeyUnresolved, $"{TypeName}: the key {key.Describe()} carries no credential name.");
        }

        string? parentUniqueName = key.ParentKey is { } parentKey && parentKey.TryGet(ParentKeyProperty, out var unique) ? unique : null;
        string? parentPath = await TryResolveParentPathAsync(graph, key.ParentGraphId, parentUniqueName, key.Api, cancellationToken).ConfigureAwait(false);
        if (parentPath is null)
        {
            // The parent application does not exist (yet): neither does the credential, and there are no siblings.
            _siblings[SnapshotKey(key.ParentGraphId, parentUniqueName)] = [];
            return null;
        }

        var managed = _selects.For(key, ManagedPropertiesFor(key.Api));
        return await ReadCoreAsync(graph, key, name, parentPath, parentUniqueName, managed, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ExistingResource?> ReadCoreAsync(IGraphClient graph, ResourceKey key, string name, string parentPath, string? parentUniqueName, string[] managed, CancellationToken cancellationToken)
    {
        var live = await graph.GetAsync(new GraphRequest(CredentialByNamePath(parentPath, name), key.Api).WithSelect(managed), cancellationToken).ConfigureAwait(false) as JsonObject;

        // The siblings decide the issuer+subject uniqueness and the 20-per-application limit in Plan (sync); a 404
        // here means the parent application does not exist yet.
        _siblings[SnapshotKey(key.ParentGraphId, parentUniqueName)] = await ListSiblingsAsync(graph, parentPath, key.Api, cancellationToken).ConfigureAwait(false);

        return live is null ? null : ToExisting(live);
    }

    /// <summary>The object id of the application with this <c>uniqueName</c>, found with an advanced <c>$filter</c>; <c>null</c> when there is none.</summary>
    private async Task<string?> FindParentIdByFilterAsync(IGraphClient graph, string uniqueName, GraphApi api, CancellationToken cancellationToken)
    {
        var request = new GraphRequest(CollectionPath, api)
            .WithFilter(ParentKeyProperty + " eq '" + ProviderSupport.EscapeLiteral(uniqueName) + "'")
            .WithCount()
            .WithConsistencyLevelEventual()
            .WithSelect("id", ParentKeyProperty);
        await foreach (var item in graph.GetAllPagesAsync(request, cancellationToken).ConfigureAwait(false))
        {
            if (item is JsonObject app && string.Equals(JsonObjects.GetString(app, ParentKeyProperty), uniqueName, StringComparison.OrdinalIgnoreCase))
            {
                return JsonObjects.GetId(app);
            }
        }

        return null;
    }

    /// <summary>
    /// The path of the parent application in a form Learn documents for the credential endpoints: <c>/applications/{id}</c> (federatedidentitycredential-upsert,
    /// application-list-federatedidentitycredentials). The <c>applications(uniqueName='…')</c> form appears only in an example, so a parent known by its
    /// <c>uniqueName</c> alone is resolved to its object id first (alternate-key read, then an advanced <c>$filter</c> when the tenant rejects the key form).
    /// Null when the application does not exist.
    /// </summary>
    private async Task<string?> TryResolveParentPathAsync(IGraphClient graph, string? parentGraphId, string? parentUniqueName, GraphApi api, CancellationToken cancellationToken)
    {
        if (parentGraphId is not null)
        {
            return CollectionPath + "/" + GraphPath.Segment(parentGraphId);
        }

        if (parentUniqueName is null)
        {
            throw new ProviderException(EngineDiagnosticCodes.ResourceKeyUnresolved, $"{TypeName}: the parent application is not known (neither its Graph id nor its uniqueName).");
        }

        if (_parentIds.TryGetValue(parentUniqueName, out var cached))
        {
            return CollectionPath + "/" + GraphPath.Segment(cached);
        }

        string? parentId;
        try
        {
            var parent = await graph.GetAsync(new GraphRequest(GraphPath.AlternateKey(CollectionPath, ParentKeyProperty, parentUniqueName), api).WithSelect("id"), cancellationToken).ConfigureAwait(false) as JsonObject;
            parentId = JsonObjects.GetId(parent);
        }
        catch (GraphException ex) when (ex.Status == System.Net.HttpStatusCode.BadRequest)
        {
            parentId = await FindParentIdByFilterAsync(graph, parentUniqueName, api, cancellationToken).ConfigureAwait(false);
        }

        if (parentId is null)
        {
            return null;
        }

        _parentIds[parentUniqueName] = parentId;
        return CollectionPath + "/" + GraphPath.Segment(parentId);
    }

    /// <summary><see cref="TryResolveParentPathAsync"/> for a write: a missing parent is an error.</summary>
    private async Task<string> ResolveParentPathAsync(IGraphClient graph, string? parentGraphId, string? parentUniqueName, GraphApi api, ResourceIdentifier id, CancellationToken cancellationToken)
    {
        return await TryResolveParentPathAsync(graph, parentGraphId, parentUniqueName, api, cancellationToken).ConfigureAwait(false)
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{TypeName}: the parent application '{parentUniqueName}' of '{id.NameLeaf}' does not exist.", id);
    }
    // ---- plan ----

    public override ResourceChange Plan(ResourceInstance desired, ExistingResource? existing, PlanOptions options)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(options);

        if (desired.IsIgnored || desired.IsSymbolic || desired.Properties is not { } properties)
        {
            return base.Plan(desired, existing, options);
        }

        string name = desired.Id.NameLeaf;
        if (desired.Id.Parent is not { } parentId)
        {
            return Unsupported(desired, existing, $"{desired.Describe()}: the name must be '<application uniqueName>/<credential name>' (or the credential must be nested under its application).");
        }

        string parentName = parentId.NameLeaf;
        if (DeclaredNameConflict(properties, name, desired) is { } conflict)
        {
            return Unsupported(desired, existing, conflict);
        }

        if (FederatedIdentityCredentialsRules.NameProblem(name, Definition.NamePattern) is { } nameProblem)
        {
            return Unsupported(desired, existing, $"{desired.Describe()}: {nameProblem}");
        }

        JsonObjects.TryGetIgnoreCase(properties, "issuer", out var issuer);
        if (FederatedIdentityCredentialsRules.IssuerProblem(issuer) is { } issuerProblem)
        {
            return Unsupported(desired, existing, $"{desired.Describe()}: {issuerProblem}");
        }

        JsonObjects.TryGetIgnoreCase(properties, "subject", out var subject);
        if (FederatedIdentityCredentialsRules.SubjectProblem(subject) is { } subjectProblem)
        {
            return Unsupported(desired, existing, $"{desired.Describe()}: {subjectProblem}");
        }

        if (desired.Api != GraphApi.Beta && JsonObjects.TryGetIgnoreCase(properties, FederatedIdentityCredentialsRules.PreviewOnlyProperty, out var preview) && !JsonObjects.IsAbsent(preview))
        {
            return Unsupported(desired, existing, $"{desired.Describe()}: properties.{FederatedIdentityCredentialsRules.PreviewOnlyProperty} is only available on the beta endpoint; use apiVersion '2026-10-01-preview.beta' or remove the property.");
        }

        var pair = FederatedIdentityCredentialsRules.PairOf(properties);
        string snapshotKey = SnapshotKey(desired.ParentGraphId, parentName);
        var siblings = _siblings.TryGetValue(snapshotKey, out var snapshot) ? snapshot : [];
        var declared = _declared.GetValue(options, _ => new DeclaredCredentials()).Register(snapshotKey, name, pair);

        if (pair is { } mine)
        {
            if (FederatedIdentityCredentialsRules.FindPairHolder(siblings, mine, name) is { } holder)
            {
                return Unsupported(desired, existing, $"{desired.Describe()}: issuer '{mine.Issuer}' and subject '{mine.Subject}' are already used by federated identity credential '{holder.Name}' of application '{parentName}'; Graph requires the pair to be unique per application. Remove or change that credential first (a renamed credential keeping issuer and subject needs the old one deleted before the new one is created).");
            }

            // A credential that already holds its pair live is never the duplicate: the declared sibling that is not
            // live yet is (it sees this one in its sibling list), whatever the order the two were planned in.
            bool holdsPairLive = existing is not null && FederatedIdentityCredentialsRules.PairOf(existing.Raw) is { } livePair && FederatedIdentityCredentialsRules.SamePair(livePair, mine);
            if (!holdsPairLive && declared.DuplicateOf is { } other)
            {
                return Unsupported(desired, existing, $"{desired.Describe()}: issuer '{mine.Issuer}' and subject '{mine.Subject}' are also declared by federated identity credential '{other}' of application '{parentName}' in this template; Graph requires the pair to be unique per application.");
            }
        }

        if (existing is null)
        {
            int others = FederatedIdentityCredentialsRules.CountOthers(siblings, declared.OtherNames, name);
            if (others >= FederatedIdentityCredentialsRules.MaxPerApplication)
            {
                return Unsupported(desired, null, $"{desired.Describe()}: application '{parentName}' already has {others} federated identity credentials (existing plus those declared in this template); Graph allows at most {FederatedIdentityCredentialsRules.MaxPerApplication} per application.");
            }
        }

        return base.Plan(desired, existing, options);
    }

    // ---- create ----

    public override async Task<AppliedResource> CreateAsync(ResourceInstance desired, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(graph);
        if (desired.Properties is null)
        {
            throw new InvalidOperationException($"{desired.Describe()}: properties must be evaluated before creating.");
        }

        string name = desired.Id.NameLeaf;
        string parentPath = await ResolveParentPathAsync(graph, desired.ParentGraphId, desired.Id.Parent?.NameLeaf, desired.Api, desired.Id, cancellationToken).ConfigureAwait(false);
        var body = BuildCreateBody(desired);
        RemoveServerAssigned(body);

        var live = await UpsertAsync(graph, parentPath, name, body, desired, cancellationToken).ConfigureAwait(false);
        string id = RequireId(live, desired);
        var key = await KeyWithParentAsync(desired, graph, cancellationToken).ConfigureAwait(false);
        return new AppliedResource(desired.Id, id, live, key);
    }

    // ---- update ----

    public override async Task<AppliedResource> UpdateAsync(ResourceInstance desired, ExistingResource existing, ResourceChange change, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(graph);
        if (desired.Properties is null)
        {
            throw new InvalidOperationException($"{desired.Describe()}: properties must be evaluated before updating.");
        }

        string name = desired.Id.NameLeaf;
        string parentPath = await ResolveParentPathAsync(graph, desired.ParentGraphId, desired.Id.Parent?.NameLeaf, desired.Api, desired.Id, cancellationToken).ConfigureAwait(false);
        var body = BuildPatchBody(desired, existing, change);
        RemoveServerAssigned(body);

        JsonObject live;
        if (body.Count == 0)
        {
            live = (JsonObject)JsonValues.Clone(existing.Raw)!;
        }
        else
        {
            // The same upsert as a create: the body is merged into the credential (204), or, should it have vanished
            // since the read (drift), it is created again (201) and the new id is adopted.
            live = await UpsertAsync(graph, parentPath, name, body, desired, cancellationToken).ConfigureAwait(false);
        }

        string id = RequireId(live, desired);
        var key = await KeyWithParentAsync(desired, graph, cancellationToken).ConfigureAwait(false);
        return new AppliedResource(desired.Id, id, live, key);
    }

    // ---- delete ----

    public override async Task DeleteAsync(ResourceKey key, ExistingResource live, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(graph);
        string name = key.TryGet("name", out var keyed) ? keyed
            : JsonObjects.GetString(live.Raw, "name")
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceKeyUnresolved, $"{TypeName}: neither the key {key.Describe()} nor the live credential {live.GraphId} carries a name.");
        string? parentUniqueName = key.ParentKey is { } parentKey && parentKey.TryGet(ParentKeyProperty, out var unique) ? unique : null;
        string? parentPath = await TryResolveParentPathAsync(graph, key.ParentGraphId, parentUniqueName, key.Api, cancellationToken).ConfigureAwait(false);
        if (parentPath is null)
        {
            // The parent application is gone (and its credentials with it): nothing to do.
            return;
        }

        try
        {
            await graph.DeleteAsync(new GraphRequest(CredentialByNamePath(parentPath, name), key.Api), cancellationToken).ConfigureAwait(false);
        }
        catch (GraphException ex) when (ex.IsNotFound)
        {
            // Already gone (deleted outside EARM, or with its application): nothing to do.
        }
    }

    // ---- Graph helpers ----

    /// <summary>
    /// <c>PATCH {parent}/federatedIdentityCredentials(name='…')</c> + <c>Prefer: create-if-missing</c>; the created
    /// object (201), or the re-read credential after a merge (204).
    /// </summary>
    private async Task<JsonObject> UpsertAsync(IGraphClient graph, string parentPath, string name, JsonObject body, ResourceInstance desired, CancellationToken cancellationToken)
    {
        string path = CredentialByNamePath(parentPath, name);
        var upsert = new GraphRequest(path, desired.Api, hint: desired.RetryHint).WithPrefer("create-if-missing");
        var response = await graph.PatchAsync(upsert, body, cancellationToken).ConfigureAwait(false);
        if (response.IsCreated && response.Body is JsonObject created)
        {
            return created;
        }

        // 204: the credential existed and the body was merged into it. It was just written, so the re-read may hit
        // replication lag.
        var reread = new GraphRequest(path, desired.Api, hint: RetryHint.ExpectReplicationLag).WithSelect(_selects.For(ResolveKey(desired), ManagedPropertiesFor(desired.Api)));
        return await graph.GetAsync(reread, cancellationToken).ConfigureAwait(false) as JsonObject
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: the upsert returned no object and the credential '{name}' could not be read back.", desired.Id);
    }

    /// <summary>The live siblings of a credential; empty when the parent application does not exist (404).</summary>
    private static async Task<IReadOnlyList<FederatedCredentialSummary>> ListSiblingsAsync(IGraphClient graph, string parentPath, GraphApi api, CancellationToken cancellationToken)
    {
        var request = new GraphRequest(parentPath + "/" + ChildSegment, api).WithSelect(FederatedIdentityCredentialsRules.SummaryProperties);
        var live = new List<JsonObject>();
        try
        {
            await foreach (var item in graph.GetAllPagesAsync(request, cancellationToken).ConfigureAwait(false))
            {
                if (item is JsonObject credential)
                {
                    live.Add(credential);
                }
            }
        }
        catch (GraphException ex) when (ex.IsNotFound)
        {
            return [];
        }

        return FederatedIdentityCredentialsRules.Summarize(live);
    }

    /// <summary>
    /// The key to record: the resolved key plus the parent's Graph id, looked up by <c>uniqueName</c> when the
    /// engine did not know it (a parent that is not declared in the template), so the applied key carries
    /// <c>parentId</c>.
    /// </summary>
    private async Task<ResourceKey> KeyWithParentAsync(ResourceInstance desired, IGraphClient graph, CancellationToken cancellationToken)
    {
        var key = ResolveKey(desired);
        if (key.ParentGraphId is not null || desired.Id.Parent is not { } parentId)
        {
            return key;
        }

        string? parentPath = await TryResolveParentPathAsync(graph, null, parentId.NameLeaf, desired.Api, cancellationToken).ConfigureAwait(false);
        return parentPath is not null ? key with { ParentGraphId = parentPath[(CollectionPath.Length + 1)..] } : key;
    }

    // ---- paths ----

    private static string CredentialByNamePath(string parentPath, string name)
    {
        return GraphPath.AlternateKey(parentPath + "/" + ChildSegment, "name", name);
    }

    /// <summary>The per-parent snapshot key shared by <see cref="ReadAsync"/> and <see cref="Plan"/> (the resolved key copies the instance's parent Graph id).</summary>
    private static string SnapshotKey(string? parentGraphId, string? parentUniqueName)
    {
        return parentGraphId is not null ? "id=" + parentGraphId : ParentKeyProperty + "=" + (parentUniqueName ?? string.Empty);
    }

    private static string[] ManagedPropertiesFor(GraphApi api)
    {
        return api == GraphApi.Beta ? FederatedIdentityCredentialsRules.PreviewManagedProperties : FederatedIdentityCredentialsRules.ManagedProperties;
    }

    // ---- helpers ----

    private static ResourceTypeDefinition RequireDefinition(ResourceTypeCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.Find(ResourceType)
            ?? throw new InvalidOperationException($"The catalog does not define '{ResourceType}'.");
    }

    /// <summary><c>id</c> is assigned by Graph and rejected in bodies.</summary>
    private static void RemoveServerAssigned(JsonObject body)
    {
        if (JsonObjects.FindKey(body, "id") is { } key)
        {
            body.Remove(key);
        }
    }

    /// <summary>Mirrors <see cref="ProviderBase.InjectName"/> at plan time: a declared <c>name</c> must equal the credential name.</summary>
    private static string? DeclaredNameConflict(JsonObject properties, string name, ResourceInstance desired)
    {
        if (!JsonObjects.TryGetIgnoreCase(properties, "name", out var declared) || JsonObjects.IsAbsent(declared) || SecretJson.IsSecret(declared) || UnknownJson.IsUnknown(declared))
        {
            return null;
        }

        if (JsonValues.TryGetString(declared, out var text) && string.Equals(text, name, StringComparison.Ordinal))
        {
            return null;
        }

        return $"{desired.Describe()}: properties.name ({JsonValues.Render(declared)}) conflicts with the credential name '{name}'; the name is written to the Graph 'name', so either omit the property or make it equal ({DiagnosticCodes.NameMappedPropertyConflict}).";
    }

    private ResourceChange Unsupported(ResourceInstance desired, ExistingResource? existing, string message)
    {
        return new ResourceChange
        {
            Id = desired.Id,
            ResourceType = TypeName,
            Type = existing is null ? ChangeType.Create : ChangeType.Modify,
            Mode = existing is null ? ChangeMode.Create : ChangeMode.Patch,
            GraphId = existing?.GraphId,
            Before = existing is null ? null : RenderBefore(existing.Properties, desired.Properties),
            After = JsonDiff.RenderObject(desired.Properties),
            IsUnknown = desired.HasUnknownProperties || desired.IsSymbolic,
            IsSymbolicId = desired.IsSymbolic,
            Reasons = [message],
            UnsupportedReason = message,
        };
    }

    private string RequireId(JsonObject live, ResourceInstance desired)
    {
        return JsonObjects.GetId(live)
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{TypeName}: Graph returned an object without 'id' for '{desired.Name}'.", desired.Id);
    }

    /// <summary>
    /// The credentials declared in one planning pass, per parent: the <c>issuer + subject</c> pair of every name seen
    /// so far, so siblings planned in parallel (and not live yet) are checked against each other.
    /// </summary>
    private sealed class DeclaredCredentials
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, Dictionary<string, (string Issuer, string Subject)?>> _byParent = new(StringComparer.Ordinal);

        /// <summary>Registers (or re-registers) a name and returns the other declared names of the parent plus the first one sharing the pair.</summary>
        public (string? DuplicateOf, IReadOnlyList<string> OtherNames) Register(string parentKey, string name, (string Issuer, string Subject)? pair)
        {
            lock (_gate)
            {
                if (!_byParent.TryGetValue(parentKey, out var names))
                {
                    names = new Dictionary<string, (string Issuer, string Subject)?>(StringComparer.OrdinalIgnoreCase);
                    _byParent[parentKey] = names;
                }

                names[name] = pair;
                string? duplicateOf = null;
                var others = new List<string>();
                foreach (var other in names)
                {
                    if (FederatedIdentityCredentialsRules.NamesEqual(other.Key, name))
                    {
                        continue;
                    }

                    others.Add(other.Key);
                    if (duplicateOf is null && pair is { } mine && other.Value is { } theirs && FederatedIdentityCredentialsRules.SamePair(mine, theirs))
                    {
                        duplicateOf = other.Key;
                    }
                }

                return (duplicateOf, others);
            }
        }
    }

    // ---- orphan report ----

    /// <summary>The orphan report lists the federated identity credentials of the applications the template declares.</summary>
    public OrphanScope OrphanScope => OrphanScope.ChildrenOfDeclaredParents;

    public string? OrphanParentType => Definition.ParentType;

    public async Task<IReadOnlyList<OrphanCandidate>> ListOrphanCandidatesAsync(OrphanQuery query, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(graph);
        var candidates = new List<OrphanCandidate>();
        foreach (var parent in query.Parents)
        {
            foreach (var credential in await ListSiblingsAsync(graph, CollectionPath + "/" + GraphPath.Segment(parent.GraphId), query.Api, cancellationToken).ConfigureAwait(false))
            {
                if (credential.Id is { Length: > 0 } id)
                {
                    candidates.Add(new OrphanCandidate(id, credential.Name, parent.Name, credential.Subject is null ? null : $"subject '{credential.Subject}'"));
                }
            }
        }

        return candidates;
    }
}
