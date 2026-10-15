using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Json;
using EArm.Core.Secrets;
using EArm.Core.Types;
using EArm.Engine;
using EArm.Engine.Providers;
using EArm.Expressions.Values;
using GraphApi = EArm.Graph.GraphApi;

namespace EArm.Providers;

/// <summary>
/// <c>Microsoft.Entra/applications/passwordCredentials</c>. The
/// resource name is <c>&lt;application uniqueName&gt;/&lt;credential name&gt;</c>. The live <c>displayName</c> is
/// <c>&lt;name&gt;#&lt;rotationTag&gt;</c> when the template declares a <c>rotationTag</c>, else the plain name
/// (<see cref="PasswordCredentialName"/>): the tag lives in Graph, so rotation needs no local state. The credential is matched
/// among the application's <c>passwordCredentials</c> by base name (with or without a tag suffix); among several, the newest whose
/// tag equals the declared one, else the newest. Graph never edits a credential: every change is a replacement, and the
/// <c>secretText</c> is returned exactly once, by <c>addPassword</c>.
/// <para>
/// Action URLs always address the application by its object id (the parent applied or read in this run, or
/// an id lookup); only reads use the alternate key <c>/applications(uniqueName='…')</c>. Requests per operation:
/// <list type="bullet">
/// <item><b>Read</b>: <c>GET /applications/{parentId}?$select=id,passwordCredentials</c> (or <c>GET /applications(uniqueName='…')</c>); a 404 is "does not exist" (parent and credential alike).</item>
/// <item><b>Create</b>: <c>[GET /applications(uniqueName='…')?$select=id]</c> when the parent's id is not known → <c>POST /applications/{id}/addPassword { "passwordCredential": { "displayName": "&lt;name&gt;[#&lt;rotationTag&gt;]", "endDateTime"[, "startDateTime"] } }</c>; the response's <c>secretText</c> goes into the published live object as a secret leaf (so <c>reference(...).secretText</c> works in this run) and nowhere else.</item>
/// <item><b>Rotate</b> (a <c>Modify</c> row): the same <c>addPassword</c> first (named with the new tag), then <c>POST /applications/{id}/removePassword { "keyId" }</c> for every live same-base-name credential except the new one, so a consumer never sees a moment without a valid secret.</item>
/// <item><b>Delete</b>: <c>GET /applications/{parentId}?$select=id,passwordCredentials</c> (a 404 means the application and its credentials are gone) → <c>removePassword</c> for the live credential's <c>keyId</c> and every same-name leftover; an unknown <c>keyId</c> counts as removed.</item>
/// </list>
/// </para>
/// <para>
/// <see cref="Plan"/>: <c>Create</c> when no same-name credential exists; <c>NoChange</c> when one exists and nothing
/// below applies; otherwise <c>Modify</c> (mode <see cref="ChangeMode.Patch"/>, applied by <see cref="UpdateAsync"/>
/// as add-then-remove rather than the executor's delete-then-create <see cref="ChangeMode.Recreate"/>) when the
/// <c>rotationTag</c> differs from the tag in the live <c>displayName</c> (a live credential without a tag, created by an
/// older EARM, matches any declared tag and is reported with <see cref="ProviderWarnings.RotationTagNotStamped"/>), a
/// declared <c>endDateTime</c> / <c>startDateTime</c> differs from the live value, the credential expired, or it
/// expires within <c>rotateBeforeDays</c> (default 30). A credential created without <c>endDateTime</c> gets
/// "now + 180 days" once; reruns compare against the live expiry, never against a fresh default. The secret is
/// never kept outside the run (only the secure outputs file receives it); on reruns the published
/// live object carries <c>secretText: null</c> plus the <see cref="LiveSecrets.AvailabilityMarker"/> so an output
/// reading it is reported as <c>null</c> with <see cref="EngineDiagnosticCodes.SecretNotAvailableOnRerun"/>.
/// </para>
/// </summary>
[EntraProvider(ResourceType)]
public sealed class PasswordCredentialsProvider : ProviderBase, IOrphanCandidateSource
{
    public const string ResourceType = "Microsoft.Entra/applications/passwordCredentials";

    public const string AddPasswordAction = "addPassword";

    public const string RemovePasswordAction = "removePassword";

    private const string ParentKeyProperty = "uniqueName";

    /// <summary>
    /// The key hint (<see cref="ResourceKey.Hints"/>) that carries the declared <c>rotationTag</c> (when it is known), so <see cref="ReadAsync"/>
    /// can prefer the credential whose live tag matches. It only steers the choice among same-name credentials; it never excludes one.
    /// </summary>
    public const string RotationTagHint = PasswordCredentialsRules.RotationTagProperty;

    /// <summary>The live same-name credentials seen by the last read, per (parent, name): what a rotation removes besides the credential it replaces.</summary>
    private readonly ConcurrentDictionary<string, IReadOnlyList<PasswordCredentialSummary>> _sameName = new(StringComparer.Ordinal);

    public PasswordCredentialsProvider(ResourceTypeDefinition definition, ResourceTypeCatalog? catalog = null)
        : base(definition, catalog)
    {
        if (!ResourceIdentifier.TypeEquals(definition.Type, ResourceType))
        {
            throw new ArgumentException($"{nameof(PasswordCredentialsProvider)} implements '{ResourceType}', not '{definition.Type}'.", nameof(definition));
        }
    }

    public PasswordCredentialsProvider(ResourceTypeCatalog catalog)
        : this(RequireDefinition(catalog), catalog)
    {
    }

    /// <summary>The clock expiry checks and the default <c>endDateTime</c> use; settable for tests.</summary>
    public TimeProvider Clock { get; set; } = TimeProvider.System;

    /// <summary>
    /// Same rule as the applications provider, applied to the parent:
    /// <c>Application.ReadWrite.OwnedBy</c> when the parent application is declared in the template (this
    /// deployment creates and therefore owns it), otherwise <c>Application.ReadWrite.All</c>.
    /// </summary>
    public override IReadOnlyList<string> RequiredPermissions(ResourceInstance? desired)
    {
        return desired?.Parent is not null ? [ApplicationsProvider.OwnedByPermission] : [ApplicationsProvider.ReadWriteAllPermission];
    }

    // ---- key ----

    /// <summary>The base key (application, credential name), with the declared <c>rotationTag</c> as a read hint when it is a known scalar.</summary>
    public override ResourceKey ResolveKey(ResourceInstance desired)
    {
        var key = base.ResolveKey(desired);
        if (desired.Properties is not { } properties
            || !JsonObjects.TryGetIgnoreCase(properties, PasswordCredentialsRules.RotationTagProperty, out var tag)
            || JsonObjects.IsAbsent(tag)
            || PasswordCredentialsRules.DescribeDesired(properties, out var parsed) is not null
            || parsed?.RotationTag is not { } rotationTag)
        {
            return key;
        }

        return key with { Hints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [RotationTagHint] = rotationTag } };
    }

    // ---- read ----

    public override async Task<ExistingResource?> ReadAsync(ResourceKey key, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(graph);

        if (!key.TryGet(PasswordCredentialsRules.DisplayNameProperty, out var name))
        {
            throw new ProviderException(EngineDiagnosticCodes.ResourceKeyUnresolved, $"{TypeName}: the key {key.Describe()} carries no credential displayName.");
        }

        string? parentUniqueName = key.ParentKey is { } parentKey && parentKey.TryGet(ParentKeyProperty, out var unique) ? unique : null;
        string parentPath = ParentPath(key.ParentGraphId, parentUniqueName, null);
        string snapshotKey = SnapshotKey(key.ParentGraphId, parentUniqueName, name);

        var parent = await ReadParentAsync(graph, parentPath, key.Api, RetryHint.Default, cancellationToken).ConfigureAwait(false);
        if (parent is null)
        {
            _sameName.TryRemove(snapshotKey, out _);
            return null;
        }

        var sameName = PasswordCredentialsRules.SameName(PasswordCredentialsRules.Summarize(parent[PasswordCredentialsRules.PasswordCredentialsProperty]), name);
        _sameName[snapshotKey] = sameName;

        string? preferredTag = key.Hints.TryGetValue(RotationTagHint, out var tag) ? tag : null;
        var match = PasswordCredentialsRules.Select(sameName, preferredTag);
        return match is null ? null : Existing(match);
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
        if (desired.Id.Parent is null)
        {
            return Unsupported(desired, existing, $"{desired.Describe()}: the name must be '<application uniqueName>/<credential displayName>' (or the credential must be nested under its application).");
        }

        if (DeclaredNameConflict(properties, name, desired) is { } conflict)
        {
            return Unsupported(desired, existing, conflict);
        }

        if (desired.HasUnknownProperties)
        {
            if (existing is null)
            {
                return base.Plan(desired, null, options);
            }

            // Whether the secret is rotated depends on values that are only known at deployment time.
            return new ResourceChange
            {
                Id = desired.Id,
                ResourceType = TypeName,
                Type = ChangeType.Modify,
                Mode = ChangeMode.Patch,
                GraphId = existing.GraphId,
                Before = JsonDiff.RenderObject(existing.Properties),
                After = JsonDiff.RenderObject(properties),
                IsUnknown = true,
                Reasons = ["some property values are only known at deployment time; whether the secret is rotated is decided when the credential is applied"],
            };
        }

        string? problem = PasswordCredentialsRules.DescribeDesired(properties, out var parsed);
        if (problem is not null || parsed is null)
        {
            return Unsupported(desired, existing, $"{desired.Describe()}: {problem ?? "the properties could not be interpreted."}");
        }

        if (PasswordCredentialsRules.NameProblem(name, parsed.RotationTag) is { } nameProblem)
        {
            return Unsupported(desired, existing, $"{desired.Describe()}: {nameProblem} ({DiagnosticCodes.InvalidResourceName})");
        }

        if (existing is null)
        {
            // The shared Create row (deltas, required-on-create check), with the name shown like the other views.
            return base.Plan(desired, null, options) with { After = AfterView(properties, name, parsed) };
        }

        var live = PasswordCredentialsRules.SummaryOf(existing.Raw)
            ?? throw new ProviderException(EngineDiagnosticCodes.ReadFailed, $"{desired.Describe()}: the live credential carries no keyId.", desired.Id);
        var assessment = PasswordCredentialsRules.AssessRotation(parsed, live, Clock.GetUtcNow());
        var triggers = assessment.Triggers;
        var warningReasons = assessment.Warnings.Select(w => ProviderWarnings.Format(w.Code, $"{desired.Describe()}: {w.Message}")).ToList();

        var before = PasswordCredentialsRules.DiffView(live);
        var after = AfterView(properties, name, parsed);
        if (triggers.Count == 0)
        {
            return new ResourceChange
            {
                Id = desired.Id,
                ResourceType = TypeName,
                Type = ChangeType.NoChange,
                Mode = ChangeMode.None,
                Before = before,
                After = after,
                GraphId = existing.GraphId,
                Reasons = warningReasons,
            };
        }

        var deltas = new List<PropertyDelta>();
        var reasons = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var trigger in triggers)
        {
            reasons.Add(trigger.Reason);
            if (!seen.Add(trigger.Property))
            {
                continue;
            }

            var kind = trigger.Before is null ? PropertyChangeType.Create : trigger.After is null ? PropertyChangeType.Delete : PropertyChangeType.Modify;
            deltas.Add(new PropertyDelta(JsonPath.Of("properties", trigger.Property), kind, trigger.Before, trigger.After));
        }

        return new ResourceChange
        {
            Id = desired.Id,
            ResourceType = TypeName,
            Type = ChangeType.Modify,
            Mode = ChangeMode.Patch,
            Deltas = deltas,
            Before = before,
            After = after,
            GraphId = existing.GraphId,
            Reasons = ["the secret is rotated (a new credential is added, then the previous one removed): " + string.Join("; ", reasons), .. warningReasons],
        };
    }

    // ---- create ----

    public override async Task<AppliedResource> CreateAsync(ResourceInstance desired, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(graph);
        var parsed = ParseDesired(desired);
        string parentId = await ResolveParentIdAsync(desired, graph, cancellationToken).ConfigureAwait(false);
        var (created, live) = await AddAsync(desired, parsed, parentId, graph, cancellationToken).ConfigureAwait(false);
        return new AppliedResource(desired.Id, created.KeyId, live, KeyWithParent(desired, parentId));
    }

    // ---- update (rotation) ----

    /// <summary>Adds the replacement (named with the declared tag) first, then removes every live same-base-name credential the read saw (and the one being replaced).</summary>
    public override async Task<AppliedResource> UpdateAsync(ResourceInstance desired, ExistingResource existing, ResourceChange change, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(graph);
        var parsed = ParseDesired(desired);
        string name = desired.Id.NameLeaf;
        string parentId = await ResolveParentIdAsync(desired, graph, cancellationToken).ConfigureAwait(false);
        var (created, live) = await AddAsync(desired, parsed, parentId, graph, cancellationToken).ConfigureAwait(false);

        var stale = new List<string> { existing.GraphId };
        if (_sameName.TryGetValue(SnapshotKey(desired.ParentGraphId, desired.Id.Parent?.NameLeaf, name), out var snapshot))
        {
            foreach (var credential in snapshot)
            {
                if (!stale.Exists(id => PasswordCredentialsRules.KeyIdEquals(id, credential.KeyId)))
                {
                    stale.Add(credential.KeyId);
                }
            }
        }

        string parentPath = ParentPathById(parentId);
        foreach (var keyId in stale)
        {
            if (!PasswordCredentialsRules.KeyIdEquals(keyId, created.KeyId))
            {
                await RemoveAsync(graph, parentPath, keyId, desired.Api, desired.RetryHint, cancellationToken).ConfigureAwait(false);
            }
        }

        return new AppliedResource(desired.Id, created.KeyId, live, KeyWithParent(desired, parentId));
    }

    // ---- delete ----

    public override async Task DeleteAsync(ResourceKey key, ExistingResource live, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(graph);
        string name = key.TryGet(PasswordCredentialsRules.DisplayNameProperty, out var keyed) ? keyed
            : JsonObjects.GetString(live.Raw, PasswordCredentialsRules.DisplayNameProperty) is { } liveName ? PasswordCredentialName.Parse(liveName).Base
            : throw new ProviderException(EngineDiagnosticCodes.ResourceKeyUnresolved, $"{TypeName}: neither the key {key.Describe()} nor the live credential {live.GraphId} carries a displayName.");
        string? parentUniqueName = key.ParentKey is { } parentKey && parentKey.TryGet(ParentKeyProperty, out var unique) ? unique : null;
        string lookupPath = ParentPath(key.ParentGraphId, parentUniqueName, null);

        var parent = await ReadParentAsync(graph, lookupPath, key.Api, RetryHint.Default, cancellationToken).ConfigureAwait(false);
        if (parent is null)
        {
            // The application is gone (deleted outside EARM, or by this pass): its credentials went with it.
            return;
        }

        string parentId = JsonObjects.GetId(parent) ?? key.ParentGraphId
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceKeyUnresolved, $"{TypeName}: the parent application of {key.Describe()} has no id.");
        var keyIds = new List<string> { live.GraphId };
        foreach (var credential in PasswordCredentialsRules.SameName(PasswordCredentialsRules.Summarize(parent[PasswordCredentialsRules.PasswordCredentialsProperty]), name))
        {
            if (!keyIds.Exists(id => PasswordCredentialsRules.KeyIdEquals(id, credential.KeyId)))
            {
                keyIds.Add(credential.KeyId);
            }
        }

        string parentPath = ParentPathById(parentId);
        foreach (var keyId in keyIds)
        {
            await RemoveAsync(graph, parentPath, keyId, key.Api, RetryHint.Default, cancellationToken).ConfigureAwait(false);
        }
    }

    // ---- Graph helpers ----

    /// <summary><c>POST {parent}/addPassword</c> named <c>&lt;name&gt;[#&lt;rotationTag&gt;]</c>; the created credential and its live view (secret leaf inside).</summary>
    private async Task<(PasswordCredentialSummary Created, JsonObject Live)> AddAsync(ResourceInstance desired, DesiredPasswordCredential parsed, string parentId, IGraphClient graph, CancellationToken cancellationToken)
    {
        string name = desired.Id.NameLeaf;
        string liveName = PasswordCredentialsRules.LiveDisplayName(name, parsed.RotationTag);
        var endDateTime = parsed.EndDateTime ?? PasswordCredentialsRules.DefaultEndDateTime(Clock.GetUtcNow());
        var body = PasswordCredentialsRules.AddPasswordBody(liveName, endDateTime, parsed.StartDateTime);
        var request = new GraphRequest(ParentPathById(parentId) + "/" + AddPasswordAction, desired.Api, hint: desired.RetryHint);

        GraphResponse response;
        try
        {
            response = await graph.PostAsync(request, body, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsAmbiguousWrite(ex))
        {
            // The credential may exist although no answer came back, and the secret was lost with the answer: the call is never repeated
            // blindly. Report any same-name credential EARM did not record so the user can remove it; nothing is deleted automatically.
            throw await DescribeAmbiguousAddAsync(desired, parentId, name, ex, graph, cancellationToken).ConfigureAwait(false);
        }

        if (response.Body is not JsonObject result)
        {
            throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: addPassword returned no credential object.", desired.Id);
        }

        // The response is the only copy of the secret: it is lifted into a secret leaf and the plain text is not kept.
        string secretText = JsonObjects.GetString(result, LiveSecrets.SecretTextProperty)
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: addPassword returned no secretText.", desired.Id);
        result.Remove(JsonObjects.FindKey(result, LiveSecrets.SecretTextProperty)!);

        var created = PasswordCredentialsRules.SummaryOf(result)
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: addPassword returned no keyId.", desired.Id);
        if (created.EndDateTime is null)
        {
            created = created with { EndDateTime = endDateTime };
        }

        if (!string.Equals(created.DisplayName, liveName, StringComparison.Ordinal))
        {
            created = created with { DisplayName = liveName };
        }

        var live = PasswordCredentialsRules.LiveView(created, new SecretValue(secretText));
        return (created, live);
    }

    private static bool IsAmbiguousWrite(Exception exception)
    {
        return exception is GraphException { IsAmbiguous: true } or GraphTransportException { Ambiguous: true };
    }

    /// <summary>
    /// After an ambiguous <c>addPassword</c> failure: lists the application's same-name credentials and reports the ones this run did not
    /// know about (not in the last read's snapshot) by <c>keyId</c>. The Graph answer that carried the secret is lost, so such a credential
    /// is an orphan (its secret is unrecoverable): it is reported, never removed.
    /// </summary>
    private async Task<ProviderException> DescribeAmbiguousAddAsync(ResourceInstance desired, string parentId, string name, Exception cause, IGraphClient graph, CancellationToken cancellationToken)
    {
        string text = $"{desired.Describe()}: addPassword failed without a usable answer ({cause.Message}); the request is not repeated because the credential may have been created and its secret is only returned once.";
        try
        {
            var parent = await ReadParentAsync(graph, ParentPathById(parentId), desired.Api, RetryHint.Default, cancellationToken).ConfigureAwait(false);
            var known = _sameName.TryGetValue(SnapshotKey(desired.ParentGraphId, desired.Id.Parent?.NameLeaf, name), out var snapshot) ? snapshot : [];
            var orphans = PasswordCredentialsRules.SameName(PasswordCredentialsRules.Summarize(parent?[PasswordCredentialsRules.PasswordCredentialsProperty]), name)
                .Where(c => !known.Any(k => PasswordCredentialsRules.KeyIdEquals(k.KeyId, c.KeyId)))
                .ToList();
            if (orphans.Count > 0)
            {
                string ids = string.Join(", ", orphans.Select(o => $"keyId {o.KeyId} (hint '{o.Hint}', expires {(o.EndDateTime is { } end ? PasswordCredentialsRules.Format(end) : "?")})"));
                text += $" {ProviderWarnings.PasswordCredentialOrphaned}: the application now holds {orphans.Count} credential(s) named '{name}' that EARM did not record: {ids}. Their secrets are lost; remove them with POST /applications/{parentId}/removePassword {{ \"keyId\": \"…\" }} (EARM never deletes them automatically), then run the deployment again.";
            }
            else
            {
                text += " No unrecorded credential named '" + name + "' was found, so the deployment can simply be run again.";
            }
        }
        catch (Exception reconcile) when (reconcile is GraphException or GraphTransportException)
        {
            text += $" The credentials could not be listed to check for a duplicate ({reconcile.Message}); before running again, look for an extra credential named '{name}' on application {parentId}.";
        }

        return new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, text, desired.Id, cause);
    }

    /// <summary><c>POST {parent}/removePassword { keyId }</c>; an unknown key id (404, or Graph's 400) counts as removed.</summary>
    private static async Task RemoveAsync(IGraphClient graph, string parentPath, string keyId, GraphApi api, RetryHint hint, CancellationToken cancellationToken)
    {
        var request = new GraphRequest(parentPath + "/" + RemovePasswordAction, api, hint: hint);
        try
        {
            await graph.PostAsync(request, PasswordCredentialsRules.RemovePasswordBody(keyId), cancellationToken).ConfigureAwait(false);
        }
        catch (GraphException ex) when (PasswordCredentialsRules.IsUnknownKeyId(ex))
        {
            // Already gone (removed outside EARM, or never existed under this application).
        }
    }

    /// <summary><c>GET {parent}?$select=id,passwordCredentials</c>; <c>null</c> when the application does not exist.</summary>
    private static async Task<JsonObject?> ReadParentAsync(IGraphClient graph, string parentPath, GraphApi api, RetryHint hint, CancellationToken cancellationToken)
    {
        var request = new GraphRequest(parentPath, api, hint: hint).WithSelect(PasswordCredentialsRules.ParentSelect);
        return await graph.GetAsync(request, cancellationToken).ConfigureAwait(false) as JsonObject;
    }

    /// <summary>The parent's object id: known from the engine (applied in this run, or read while planning) or looked up by <c>uniqueName</c>.</summary>
    private async Task<string> ResolveParentIdAsync(ResourceInstance desired, IGraphClient graph, CancellationToken cancellationToken)
    {
        if (desired.ParentGraphId is not null)
        {
            return desired.ParentGraphId;
        }

        if (desired.Id.Parent is not { } parentId)
        {
            throw new ProviderException(EngineDiagnosticCodes.ResourceKeyUnresolved, $"{desired.Describe()}: the name must be '<application uniqueName>/<credential displayName>'.", desired.Id);
        }

        var request = new GraphRequest(GraphPath.AlternateKey(CollectionPath, ParentKeyProperty, parentId.NameLeaf), desired.Api, hint: desired.RetryHint).WithSelect("id");
        var parent = await graph.GetAsync(request, cancellationToken).ConfigureAwait(false) as JsonObject;
        return JsonObjects.GetId(parent)
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: application '{parentId.NameLeaf}' does not exist, so no password credential can be added to it.", desired.Id);
    }

    private ResourceKey KeyWithParent(ResourceInstance desired, string parentId)
    {
        return ResolveKey(desired) with { ParentGraphId = parentId };
    }

    // ---- paths ----

    /// <summary><c>/applications/{id}</c> when the parent's Graph id is known, otherwise <c>/applications(uniqueName='…')</c> (reads only).</summary>
    private string ParentPath(string? parentGraphId, string? parentUniqueName, ResourceIdentifier? id)
    {
        if (parentGraphId is not null)
        {
            return ParentPathById(parentGraphId);
        }

        if (parentUniqueName is not null)
        {
            return GraphPath.AlternateKey(CollectionPath, ParentKeyProperty, parentUniqueName);
        }

        throw new ProviderException(EngineDiagnosticCodes.ResourceKeyUnresolved, $"{TypeName}: the parent application is not known (neither its Graph id nor its uniqueName).", id);
    }

    private string ParentPathById(string parentGraphId)
    {
        return CollectionPath + "/" + GraphPath.Segment(parentGraphId);
    }

    /// <summary>The per-(parent, name) snapshot key shared by <see cref="ReadAsync"/> and <see cref="UpdateAsync"/> (the resolved key copies the instance's parent Graph id).</summary>
    private static string SnapshotKey(string? parentGraphId, string? parentUniqueName, string name)
    {
        string parent = parentGraphId is not null ? "id=" + parentGraphId : ParentKeyProperty + "=" + (parentUniqueName ?? string.Empty);
        return parent + "|" + name;
    }

    // ---- helpers ----

    private static ResourceTypeDefinition RequireDefinition(ResourceTypeCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.Find(ResourceType)
            ?? throw new InvalidOperationException($"The catalog does not define '{ResourceType}'.");
    }

    /// <summary>A live credential wrapped for the engine: Graph id = <c>keyId</c>, the diff view, and the reference view with no secret available.</summary>
    private static ExistingResource Existing(PasswordCredentialSummary credential)
    {
        return new ExistingResource(credential.KeyId, PasswordCredentialsRules.DiffView(credential), PasswordCredentialsRules.LiveView(credential, null));
    }

    private static DesiredPasswordCredential ParseDesired(ResourceInstance desired)
    {
        if (desired.Properties is not { } properties)
        {
            throw new InvalidOperationException($"{desired.Describe()}: properties must be evaluated before applying.");
        }

        string? problem = PasswordCredentialsRules.DescribeDesired(properties, out var parsed);
        if (problem is not null || parsed is null)
        {
            throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: {problem ?? "the properties could not be interpreted."}", desired.Id);
        }

        if (PasswordCredentialsRules.NameProblem(desired.Id.NameLeaf, parsed.RotationTag) is { } nameProblem)
        {
            throw new ProviderException(DiagnosticCodes.InvalidResourceName, $"{desired.Describe()}: {nameProblem}", desired.Id);
        }

        return parsed;
    }

    /// <summary>
    /// The desired side of the what-if row: the declared properties with the live <c>displayName</c> (<c>name#rotationTag</c>) injected and
    /// the dates in canonical form, so it lines up with the live side (<see cref="PasswordCredentialsRules.DiffView"/>) tag for tag.
    /// </summary>
    private static JsonObject AfterView(JsonObject properties, string name, DesiredPasswordCredential parsed)
    {
        var after = (JsonObject)JsonValues.Clone(properties)!;
        string liveName = PasswordCredentialsRules.NameProblem(name, parsed.RotationTag) is null ? PasswordCredentialsRules.LiveDisplayName(name, parsed.RotationTag) : name;
        after[JsonObjects.FindKey(after, PasswordCredentialsRules.DisplayNameProperty) ?? PasswordCredentialsRules.DisplayNameProperty] = liveName;
        if (parsed.EndDateTime is { } end)
        {
            after[JsonObjects.FindKey(after, PasswordCredentialsRules.EndDateTimeProperty) ?? PasswordCredentialsRules.EndDateTimeProperty] = PasswordCredentialsRules.Format(end);
        }

        if (parsed.StartDateTime is { } start)
        {
            after[JsonObjects.FindKey(after, PasswordCredentialsRules.StartDateTimeProperty) ?? PasswordCredentialsRules.StartDateTimeProperty] = PasswordCredentialsRules.Format(start);
        }

        return JsonDiff.RenderObject(after)!;
    }

    /// <summary>Mirrors <see cref="ProviderBase.InjectName"/> at plan time: a declared <c>displayName</c> must equal the credential name.</summary>
    private static string? DeclaredNameConflict(JsonObject properties, string name, ResourceInstance desired)
    {
        if (!JsonObjects.TryGetIgnoreCase(properties, PasswordCredentialsRules.DisplayNameProperty, out var declared) || JsonObjects.IsAbsent(declared) || SecretJson.IsSecret(declared) || UnknownJson.IsUnknown(declared))
        {
            return null;
        }

        if (JsonValues.TryGetString(declared, out var text) && string.Equals(text, name, StringComparison.Ordinal))
        {
            return null;
        }

        return $"{desired.Describe()}: properties.displayName ({JsonValues.Render(declared)}) conflicts with the credential name '{name}'; the name is written to the Graph 'displayName', so either omit the property or make it equal ({DiagnosticCodes.NameMappedPropertyConflict}).";
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

    // ---- orphan report ----

    /// <summary>The orphan report lists the password credentials of the applications the template declares (Graph id = <c>keyId</c>).</summary>
    public OrphanScope OrphanScope => OrphanScope.ChildrenOfDeclaredParents;

    public string? OrphanParentType => Definition.ParentType;

    public async Task<IReadOnlyList<OrphanCandidate>> ListOrphanCandidatesAsync(OrphanQuery query, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(graph);
        var candidates = new List<OrphanCandidate>();
        foreach (var parent in query.Parents)
        {
            var live = await ReadParentAsync(graph, ParentPathById(parent.GraphId), query.Api, RetryHint.Default, cancellationToken).ConfigureAwait(false);
            foreach (var credential in PasswordCredentialsRules.Summarize(live?[PasswordCredentialsRules.PasswordCredentialsProperty]))
            {
                string name = credential.DisplayName is { Length: > 0 } displayName ? PasswordCredentialName.Parse(displayName).Base : credential.KeyId;
                candidates.Add(new OrphanCandidate(credential.KeyId, string.IsNullOrEmpty(name) ? credential.KeyId : name, parent.Name, credential.DisplayName is null ? null : $"displayName '{credential.DisplayName}'"));
            }
        }

        return candidates;
    }
}
