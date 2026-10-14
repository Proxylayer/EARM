using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EntraArm.Az;
using EntraArm.Core.Secrets;
using EntraArm.Core.Types;
using EntraArm.Engine;
using EntraArm.Engine.Providers;
using EntraArm.Engine.State;

namespace EntraArm.TestSupport;

/// <summary>
/// A generic, catalog-driven provider for tests: reads by the overlay's key kind (alternate key, UPN, id,
/// displayName filter, composite filter, parent/child path), creates with POST (or an alternate-key PATCH with
/// <c>Prefer: create-if-missing</c> for <c>uniqueName</c> types), updates with PATCH, deletes with DELETE, all
/// against the overlay's Graph path. It has none of the per-type pitfalls the real providers (WI-07+) handle; it
/// exists so planner/executor tests and harnesses can run end-to-end against <see cref="FakeGraphClient"/>.
/// </summary>
public sealed class EchoProvider : ProviderBase
{
    public EchoProvider(ResourceTypeDefinition definition, ResourceTypeCatalog? catalog = null)
        : base(definition, catalog)
    {
    }

    /// <summary>A registry with one echo provider per catalog type.</summary>
    public static ProviderRegistry RegistryForCatalog(ResourceTypeCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var registry = new ProviderRegistry();
        foreach (var definition in catalog.Types)
        {
            registry.Register(new EchoProvider(definition, catalog));
        }

        return registry;
    }

    private bool IsPasswordCredential => Definition.GraphPath.EndsWith("/addPassword", StringComparison.OrdinalIgnoreCase);

    // ---- read ----

    public override async Task<ExistingResource?> ReadAsync(ResourceKey key, string? stateId, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(graph);

        if (IsPasswordCredential)
        {
            return await ReadPasswordCredentialAsync(key, graph, cancellationToken).ConfigureAwait(false);
        }

        if (stateId is not null)
        {
            // The pinned Graph id from the state goes first (authoritative for displayName/GUID-named types); a 404
            // means drift and falls back to the key lookup below.
            string byId = EntityPath(key, stateId);
            if (await graph.GetAsync(new GraphRequest(byId, key.Api), cancellationToken).ConfigureAwait(false) is JsonObject pinned)
            {
                return ToExisting(pinned);
            }
        }

        switch (Definition.NameKind)
        {
            case ResourceNameKind.UniqueName:
            case ResourceNameKind.AppId:
            case ResourceNameKind.ChildName:
                {
                    string path = GraphPath.AlternateKey(CollectionPathFor(key), key.SingleName, key.Single);
                    return await graph.GetAsync(new GraphRequest(path, key.Api), cancellationToken).ConfigureAwait(false) is JsonObject found ? ToExisting(found) : null;
                }

            case ResourceNameKind.UserPrincipalName:
            case ResourceNameKind.Id:
                {
                    string path = CollectionPathFor(key) + "/" + GraphPath.Segment(key.Single);
                    return await graph.GetAsync(new GraphRequest(path, key.Api), cancellationToken).ConfigureAwait(false) is JsonObject found ? ToExisting(found) : null;
                }

            case ResourceNameKind.DisplayName:
                {
                    string filter = $"{key.SingleName} eq '{key.Single.Replace("'", "''", StringComparison.Ordinal)}'";
                    var request = new GraphRequest(CollectionPathFor(key), key.Api).WithFilter(filter);
                    var matches = new List<JsonObject>();
                    await foreach (var item in graph.GetAllPagesAsync(request, cancellationToken).ConfigureAwait(false))
                    {
                        if (item is JsonObject obj)
                        {
                            matches.Add(obj);
                        }
                    }

                    return matches.Count switch
                    {
                        0 => null,
                        1 => ToExisting(matches[0]),
                        _ => throw new ProviderException(ProviderException.AmbiguousMatch, $"{TypeName}: {matches.Count} objects have {key.Describe()}; pin one with 'entraarm adopt'."),
                    };
                }

            case ResourceNameKind.Guid:
                {
                    var request = new GraphRequest(CollectionPathFor(key), key.Api);
                    await foreach (var item in graph.GetAllPagesAsync(request, cancellationToken).ConfigureAwait(false))
                    {
                        if (item is JsonObject obj && key.Values.All(pair => JsonObjects.KeyEquals(JsonObjects.GetString(obj, pair.Key), pair.Value)))
                        {
                            return ToExisting(obj);
                        }
                    }

                    return null;
                }

            default:
                throw new ProviderException(EngineDiagnosticCodes.ResourceKeyUnresolved, $"{TypeName}: unsupported name kind {Definition.NameKind}.");
        }
    }

    private async Task<ExistingResource?> ReadPasswordCredentialAsync(ResourceKey key, IGraphClient graph, CancellationToken cancellationToken)
    {
        var parent = await graph.GetAsync(new GraphRequest(ParentEntityPath(key), key.Api).WithSelect("id", "passwordCredentials"), cancellationToken).ConfigureAwait(false) as JsonObject;
        if (parent?["passwordCredentials"] is not JsonArray credentials)
        {
            return null;
        }

        foreach (var item in credentials)
        {
            if (item is JsonObject credential && JsonObjects.KeyEquals(JsonObjects.GetString(credential, "displayName"), key.Single))
            {
                var raw = (JsonObject)credential.DeepClone();
                raw["id"] = JsonObjects.GetString(credential, "keyId");
                return ToExisting(raw);
            }
        }

        return null;
    }

    // ---- create / update / delete ----

    public override async Task<AppliedResource> CreateAsync(ResourceInstance desired, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(graph);

        var key = ResolveKey(desired);
        var body = BuildCreateBody(desired);

        if (IsPasswordCredential)
        {
            var request = new GraphRequest(ParentEntityPath(key) + "/addPassword", desired.Api, hint: desired.RetryHint);
            var response = await graph.PostAsync(request, new JsonObject { ["passwordCredential"] = body }, cancellationToken).ConfigureAwait(false);
            var credential = response.Body as JsonObject ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{TypeName}: addPassword returned no body.", desired.Id);
            var live = (JsonObject)credential.DeepClone();
            live["id"] = JsonObjects.GetString(credential, "keyId");
            if (JsonObjects.GetString(credential, "secretText") is { } secret)
            {
                live["secretText"] = SecretJson.CreateString(secret);
            }

            return new AppliedResource(desired.Id, JsonObjects.GetString(credential, "keyId") ?? string.Empty, live, key, new JsonObject { ["keyId"] = JsonObjects.GetString(credential, "keyId"), ["endDateTime"] = credential["endDateTime"]?.DeepClone() });
        }

        if (Definition.NameKind is ResourceNameKind.UniqueName or ResourceNameKind.ChildName)
        {
            // Upsert through the alternate key, as the real providers do for uniqueName / FIC name.
            string path = GraphPath.AlternateKey(CollectionPathFor(key), key.SingleName, key.Single);
            var request = new GraphRequest(path, desired.Api, hint: desired.RetryHint).WithPrefer("create-if-missing");
            var response = await graph.PatchAsync(request, body, cancellationToken).ConfigureAwait(false);
            var live = response.Body as JsonObject
                ?? await graph.GetAsync(new GraphRequest(path, desired.Api, hint: RetryHint.ExpectReplicationLag), cancellationToken).ConfigureAwait(false) as JsonObject
                ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{TypeName}: the upsert of '{desired.Name}' returned no object.", desired.Id);
            return new AppliedResource(desired.Id, RequireId(live, desired), live, key);
        }

        if (Definition.NameKind == ResourceNameKind.Id)
        {
            // Graph upsert semantics: PATCH /{id} creates when missing (authenticationContextClassReferences).
            string path = CollectionPathFor(key) + "/" + GraphPath.Segment(key.Single);
            var request = new GraphRequest(path, desired.Api, hint: desired.RetryHint).WithPrefer("create-if-missing");
            var response = await graph.PatchAsync(request, body, cancellationToken).ConfigureAwait(false);
            var live = response.Body as JsonObject
                ?? await graph.GetAsync(new GraphRequest(path, desired.Api), cancellationToken).ConfigureAwait(false) as JsonObject
                ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{TypeName}: the upsert of '{desired.Name}' returned no object.", desired.Id);
            return new AppliedResource(desired.Id, RequireId(live, desired), live, key);
        }

        var created = await graph.PostAsync(new GraphRequest(CollectionPathFor(key), desired.Api, hint: desired.RetryHint), body, cancellationToken).ConfigureAwait(false);
        var createdObject = created.Body as JsonObject
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{TypeName}: the create of '{desired.Name}' returned no object.", desired.Id);
        return new AppliedResource(desired.Id, RequireId(createdObject, desired), createdObject, key);
    }

    public override async Task<AppliedResource> UpdateAsync(ResourceInstance desired, ExistingResource existing, ResourceChange change, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(graph);

        var key = ResolveKey(desired);
        if (IsPasswordCredential)
        {
            throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{TypeName}: password credentials are never edited; the planner should have asked for a recreate.", desired.Id);
        }

        var body = BuildPatchBody(desired, existing, change);
        string path = EntityPath(key, existing.GraphId);
        await graph.PatchAsync(new GraphRequest(path, desired.Api, hint: desired.RetryHint), body, cancellationToken).ConfigureAwait(false);
        var live = await graph.GetAsync(new GraphRequest(path, desired.Api), cancellationToken).ConfigureAwait(false) as JsonObject
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{TypeName}: '{desired.Name}' vanished after the update.", desired.Id);
        return new AppliedResource(desired.Id, existing.GraphId, live, key);
    }

    public override async Task DeleteAsync(StateEntry entry, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(graph);

        var key = new ResourceKey { Type = TypeName, Values = entry.Key, ParentGraphId = entry.ParentGraphId };
        if (IsPasswordCredential)
        {
            string parentPath = entry.ParentGraphId is null
                ? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{TypeName}: deleting a password credential needs the parent's Graph id.", entry.Id)
                : CollectionPath + "/" + GraphPath.Segment(entry.ParentGraphId);
            await graph.PostAsync(new GraphRequest(parentPath + "/removePassword"), new JsonObject { ["keyId"] = entry.GraphId }, cancellationToken).ConfigureAwait(false);
            return;
        }

        await graph.DeleteAsync(new GraphRequest(EntityPath(key, entry.GraphId)), cancellationToken).ConfigureAwait(false);
    }

    // ---- paths ----

    /// <summary>The collection the object lives in: the overlay path with <c>{parentId}</c> / composite placeholders substituted.</summary>
    private string CollectionPathFor(ResourceKey key)
    {
        if (!Definition.IsChildType)
        {
            return GraphPathFor(key);
        }

        string pattern = Definition.GraphPath;
        if (IsPasswordCredential)
        {
            pattern = pattern[..pattern.LastIndexOf('/')];
        }

        if (key.ParentGraphId is not null)
        {
            return SubstitutePath(pattern, name => name.Equals("parentId", StringComparison.OrdinalIgnoreCase) ? key.ParentGraphId : (key.TryGet(name, out var v) ? v : null));
        }

        var parentKey = key.ParentKey ?? throw new ProviderException(EngineDiagnosticCodes.ResourceKeyUnresolved, $"{TypeName}: the parent of {key.Describe()} is unknown.");
        string parentSegment = ParentAlternateKeySegment(parentKey);
        return pattern.Replace("/{parentId}", parentSegment, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The parent entity path: by Graph id when known, otherwise by its alternate key.</summary>
    private string ParentEntityPath(ResourceKey key)
    {
        string collection = CollectionPathFor(key);
        if (IsPasswordCredential)
        {
            // CollectionPathFor already removed the action segment; the remainder addresses the parent entity.
            return collection;
        }

        return collection[..collection.LastIndexOf('/')];
    }

    private string ParentAlternateKeySegment(ResourceKey parentKey)
    {
        var parentDefinition = Catalog.Find(parentKey.Type)
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceKeyUnresolved, $"{TypeName}: parent type '{parentKey.Type}' is not in the catalog.");
        return parentDefinition.NameKind switch
        {
            ResourceNameKind.UserPrincipalName or ResourceNameKind.Id => "/" + GraphPath.Segment(parentKey.Single),
            _ => "(" + parentKey.SingleName + "='" + GraphPath.KeyValue(parentKey.Single) + "')",
        };
    }

    private string EntityPath(ResourceKey key, string graphId)
    {
        return CollectionPathFor(key) + "/" + GraphPath.Segment(graphId);
    }

    private string RequireId(JsonObject live, ResourceInstance desired)
    {
        return JsonObjects.GetId(live)
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{TypeName}: Graph returned an object without 'id' for '{desired.Name}'.", desired.Id);
    }
}
