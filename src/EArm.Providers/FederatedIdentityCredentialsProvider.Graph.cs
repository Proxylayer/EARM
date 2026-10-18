using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Threading;
using EArm.Graph;
using EArm.Core;
using EArm.Engine;
using EArm.Engine.Providers;
using GraphApi = EArm.Graph.GraphApi;

namespace EArm.Providers;

/// <summary>The Graph calls of <see cref="FederatedIdentityCredentialsProvider"/>: the parent application lookups, the serialized upsert per parent and the sibling listing.</summary>
public sealed partial class FederatedIdentityCredentialsProvider
{
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

    // ---- Graph helpers ----

    /// <summary>
    /// <c>PATCH {parent}/federatedIdentityCredentials(name='…')</c> + <c>Prefer: create-if-missing</c>; the created
    /// object (201), or the re-read credential after a merge (204).
    /// </summary>
    private Task<JsonObject> UpsertAsync(IGraphClient graph, string parentPath, string name, JsonObject body, ResourceInstance desired, CancellationToken cancellationToken)
    {
        return OneWriteAtATimeAsync(parentPath, () => UpsertCoreAsync(graph, parentPath, name, body, desired, cancellationToken), cancellationToken);
    }

    /// <summary>Runs a write to the credentials of the application at <paramref name="parentPath"/> while no other write to them is in flight.</summary>
    private async Task<T> OneWriteAtATimeAsync<T>(string parentPath, Func<Task<T>> write, CancellationToken cancellationToken)
    {
        var gate = _parentWriteGates.GetOrAdd(parentPath, static _ => new SemaphoreSlim(1, 1));
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

    private async Task<JsonObject> UpsertCoreAsync(IGraphClient graph, string parentPath, string name, JsonObject body, ResourceInstance desired, CancellationToken cancellationToken)
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
}
