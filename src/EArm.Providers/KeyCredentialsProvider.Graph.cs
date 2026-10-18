using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Engine;
using EArm.Engine.Providers;
using GraphApi = EArm.Graph.GraphApi;

namespace EArm.Providers;

/// <summary>The Graph calls of <see cref="KeyCredentialsProvider"/>: the serialized read-modify-write of the application's <c>keyCredentials</c> and the parent lookups.</summary>
public sealed partial class KeyCredentialsProvider
{
    /// <summary>Writes the declared credential (create or replacement) and returns the applied credential as Graph now holds it.</summary>
    private async Task<AppliedResource> WriteAsync(ResourceInstance desired, IGraphClient graph, CancellationToken cancellationToken)
    {
        var parsed = ParseDesired(desired);
        string name = desired.Id.NameLeaf;
        string parentId = await ResolveParentIdAsync(desired, graph, cancellationToken).ConfigureAwait(false);
        string parentPath = ParentPathById(parentId);

        var applied = await OneWriteAtATimeAsync(parentId, async () =>
        {
            var parent = await ReadParentAsync(graph, parentPath, desired.Api, desired.RetryHint, cancellationToken).ConfigureAwait(false)
                ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: application {parentId} does not exist, so no certificate credential can be added to it.", desired.Id);
            var liveCollection = parent[KeyCredentialsRules.KeyCredentialsProperty];
            var all = KeyCredentialsRules.Summarize(liveCollection);
            var sameName = KeyCredentialsRules.SameName(all, name);
            if (sameName.Count > 1)
            {
                throw new ProviderException(EngineDiagnosticCodes.AmbiguousMatch, AmbiguityMessage(desired.Describe(), name, sameName.Select(c => c.KeyId)), desired.Id);
            }

            if (sameName.Count == 1 && KeyCredentialsRules.Compare(parsed, sameName[0]).Count == 0)
            {
                // Already as declared (written by an earlier attempt or outside EARM since the plan): nothing to write.
                return sameName[0];
            }

            RefuseWhenKeysAreMissing(desired, parentId, all, name);
            var collection = KeyCredentialsRules.ReplaceCollection(liveCollection, name, KeyCredentialsRules.DesiredEntry(name, parsed));
            await PatchCollectionAsync(graph, parentPath, collection, desired.Api, desired.RetryHint, cancellationToken).ConfigureAwait(false);

            // The application was just written: the read-back may hit replication lag.
            var after = await ReadParentAsync(graph, parentPath, desired.Api, RetryHint.ExpectReplicationLag, cancellationToken).ConfigureAwait(false);
            return KeyCredentialsRules.SameName(KeyCredentialsRules.Summarize(after?[KeyCredentialsRules.KeyCredentialsProperty]), name)
                .FirstOrDefault(c => string.Equals(c.Thumbprint, parsed.Thumbprint, StringComparison.OrdinalIgnoreCase))
                ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: the keyCredentials PATCH succeeded but the certificate '{name}' could not be read back from application {parentId}.", desired.Id);
        }, cancellationToken).ConfigureAwait(false);

        return new AppliedResource(desired.Id, applied.KeyId, KeyCredentialsRules.LiveView(applied), ResolveKey(desired) with { ParentGraphId = parentId });
    }

    /// <summary>Removes every same-name credential with one PATCH of the remaining collection; nothing to do when there is none.</summary>
    private Task RemoveAsync(IGraphClient graph, string parentId, string name, GraphApi api, CancellationToken cancellationToken)
    {
        string parentPath = ParentPathById(parentId);
        return OneWriteAtATimeAsync(parentId, async () =>
        {
            var parent = await ReadParentAsync(graph, parentPath, api, RetryHint.Default, cancellationToken).ConfigureAwait(false);
            var liveCollection = parent?[KeyCredentialsRules.KeyCredentialsProperty];
            var all = KeyCredentialsRules.Summarize(liveCollection);
            if (parent is null || KeyCredentialsRules.SameName(all, name).Count == 0)
            {
                return true;
            }

            RefuseWhenKeysAreMissing(null, parentId, all, name);
            await PatchCollectionAsync(graph, parentPath, KeyCredentialsRules.ReplaceCollection(liveCollection, name, null), api, RetryHint.Default, cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken);
    }

    /// <summary>
    /// An unmanaged credential Graph returned without its <c>key</c> would be lost by the collection PATCH (Graph replaces the whole collection): the write
    /// is refused instead. The single-object <c>$select=keyCredentials</c> read always returns the bytes (Learn: keyCredential), so this guards against drift.
    /// </summary>
    private void RefuseWhenKeysAreMissing(ResourceInstance? desired, string parentId, System.Collections.Generic.IReadOnlyList<KeyCredentialSummary> all, string name)
    {
        var missing = KeyCredentialsRules.WithoutKey(all.Where(c => !string.Equals(c.DisplayName, name, StringComparison.Ordinal))).ToList();
        if (missing.Count == 0)
        {
            return;
        }

        string who = desired?.Describe() ?? $"{TypeName} '{name}'";
        throw new ProviderException(
            EngineDiagnosticCodes.ResourceApplyFailed,
            $"{who}: Graph returned credential(s) {string.Join(", ", missing.Select(m => "keyId " + m.KeyId))} of application {parentId} without their certificate bytes; writing the keyCredentials collection back would remove them, so nothing was written.",
            desired?.Id);
    }

    /// <summary>The ambiguous-key message: the template names one credential, the application holds several with that displayName.</summary>
    private static string AmbiguityMessage(string who, string name, System.Collections.Generic.IEnumerable<string> keyIds)
    {
        return $"{who}: the application holds more than one certificate credential named '{name}' ({string.Join(", ", keyIds.Select(k => "keyId " + k))}); EARM cannot tell which one the template manages. Rename or remove the extra ones (EARM never removes them on its own), then run the deployment again ({EngineDiagnosticCodes.AmbiguousMatch}).";
    }

    /// <summary><c>PATCH /applications/{id} { "keyCredentials": [...] }</c>.</summary>
    private static async Task PatchCollectionAsync(IGraphClient graph, string parentPath, JsonArray collection, GraphApi api, RetryHint hint, CancellationToken cancellationToken)
    {
        var body = new JsonObject { [KeyCredentialsRules.KeyCredentialsProperty] = collection };
        await graph.PatchAsync(new GraphRequest(parentPath, api, hint: hint), body, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs a write to the certificate credentials of one application while no other write to them is in flight.</summary>
    private async Task<T> OneWriteAtATimeAsync<T>(string parentId, Func<Task<T>> write, CancellationToken cancellationToken)
    {
        var gate = _parentWriteGates.GetOrAdd(parentId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await write().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary><c>GET {parent}?$select=id,keyCredentials</c>; <c>null</c> when the application does not exist.</summary>
    private static async Task<JsonObject?> ReadParentAsync(IGraphClient graph, string parentPath, GraphApi api, RetryHint hint, CancellationToken cancellationToken)
    {
        var request = new GraphRequest(parentPath, api, hint: hint).WithSelect(KeyCredentialsRules.ParentSelect);
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
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: application '{parentId.NameLeaf}' does not exist, so no certificate credential can be added to it.", desired.Id);
    }
}
