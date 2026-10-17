using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Threading;
using EArm.Graph;
using EArm.Core;
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
/// <para>
/// Writes (upsert, delete) to the credentials of ONE application are serialized: Graph does not accept concurrent writes to an application's
/// credential collection (Learn: create the federated credentials of one parent sequentially; a real-tenant run that upserted <c>github-0</c> and
/// <c>github-1</c> of a copy loop in parallel got <c>400 Request_BadRequest: Request contains a property with duplicate values</c> for the second,
/// although their subjects differed). Credentials of different applications are still written in parallel.
/// </para>
/// </summary>
[EntraProvider(ResourceType)]
public sealed partial class FederatedIdentityCredentialsProvider : ProviderBase, IOrphanCandidateSource
{
    public const string ResourceType = "Microsoft.Entra/applications/federatedIdentityCredentials";

    /// <summary>The navigation segment under the application.</summary>
    public const string ChildSegment = "federatedIdentityCredentials";

    private const string ParentKeyProperty = "uniqueName";

    private readonly ConcurrentDictionary<string, IReadOnlyList<FederatedCredentialSummary>> _siblings = new(StringComparer.Ordinal);

    private readonly ConditionalWeakTable<PlanOptions, DeclaredCredentials> _declared = new();

    /// <summary>One gate per parent application path: the writes to one application's credentials go one at a time (see the type).</summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _parentWriteGates = new(StringComparer.OrdinalIgnoreCase);

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

        await OneWriteAtATimeAsync(parentPath, async () =>
        {
            try
            {
                await graph.DeleteAsync(new GraphRequest(CredentialByNamePath(parentPath, name), key.Api), cancellationToken).ConfigureAwait(false);
            }
            catch (GraphException ex) when (ex.IsNotFound)
            {
                // Already gone (deleted outside EARM, or with its application): nothing to do.
            }

            return true;
        }, cancellationToken).ConfigureAwait(false);
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

    private string RequireId(JsonObject live, ResourceInstance desired)
    {
        return JsonObjects.GetId(live)
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{TypeName}: Graph returned an object without 'id' for '{desired.Name}'.", desired.Id);
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
