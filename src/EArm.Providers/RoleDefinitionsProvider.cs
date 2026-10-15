using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Az;
using EArm.Core;
using EArm.Core.Types;
using EArm.Engine;
using EArm.Engine.Providers;
using EArm.Engine.State;
using EArm.Expressions.Values;
using GraphApi = EArm.Az.GraphApi;

namespace EArm.Providers;

/// <summary>
/// <c>Microsoft.Entra/roleDefinitions</c>, custom directory roles (plan sections 1.3, 3.2; work item WI-15). The
/// resource name is the <c>displayName</c>; the Graph id is pinned in the state after the first run. Built-in roles
/// are never managed: a name that matches one is an unsupported row (<see cref="BuiltInRole"/>) pointing at
/// <c>roleTemplate()</c>.
/// <para>
/// Requests per operation (every read selects <see cref="RoleDefinitionsRules.ManagedProperties"/>):
/// <list type="bullet">
/// <item><b>Read</b>: <c>GET /roleManagement/directory/roleDefinitions/{pinnedId}</c> (state id, when known; a 404 is drift) → <c>GET /roleManagement/directory/roleDefinitions?$filter=displayName eq '…'</c> filtered client-side to <c>isBuiltIn: false</c>: no custom hit is "create" (unless a built-in carries the name: <see cref="ProviderException"/> <see cref="BuiltInRole"/>), one hit is the object, several hits are <c>ProviderException(AmbiguousMatch)</c> listing the ids.</item>
/// <item><b>Create</b>: <c>POST /roleManagement/directory/roleDefinitions { displayName, description, isEnabled, resourceScopes: ["/"], rolePermissions }</c> (the response body is the object).</item>
/// <item><b>Update</b>: <c>PATCH …/{id}</c> (merge body; <c>rolePermissions</c> is replaced whole) → <c>GET …/{id}</c>.</item>
/// <item><b>Delete</b>: <c>DELETE …/{id}</c>; a 404 is "already gone"; a 400 (assignments still reference the role) is <see cref="RoleDefinitionInUse"/> with the fix.</item>
/// </list>
/// </para>
/// <para>
/// Type-specific rules (<see cref="RoleDefinitionsRules"/>, pure): <c>rolePermissions</c> is compared as a set of
/// <c>{ allowedResourceActions (set), excludedResourceActions (set), condition }</c>, so reordered actions are not
/// drift; <c>isEnabled</c> defaults to <c>true</c> (overlay); <c>resourceScopes</c> is the constant <c>["/"]</c>
/// (written on create, never diffed); <c>id</c>, <c>templateId</c>, <c>version</c>, <c>isBuiltIn</c> and
/// <c>inheritsPermissionsFrom</c> are server-managed and rejected in a template. <c>reference(resourceId(...),
/// apiVersion)</c> resolves custom roles only. Complete mode deletes role assignments before role definitions
/// (<c>ApplyPhases.DeleteOrderOf</c>).
/// </para>
/// </summary>
[EntraProvider(ResourceType)]
public sealed class RoleDefinitionsProvider : ProviderBase
{
    public const string ResourceType = "Microsoft.Entra/roleDefinitions";

    /// <summary><see cref="ProviderException.Code"/> when the name matches a built-in role, which cannot be managed.</summary>
    public const string BuiltInRole = "RoleDefinitionBuiltIn";

    /// <summary><see cref="ProviderException.Code"/> when Graph refuses to delete a role that assignments still reference.</summary>
    public const string RoleDefinitionInUse = "RoleDefinitionInUse";

    private const string KeyProperty = "displayName";

    public RoleDefinitionsProvider(ResourceTypeDefinition definition, ResourceTypeCatalog? catalog = null)
        : base(definition, catalog)
    {
        if (!ResourceIdentifier.TypeEquals(definition.Type, ResourceType))
        {
            throw new ArgumentException($"{nameof(RoleDefinitionsProvider)} implements '{ResourceType}', not '{definition.Type}'.", nameof(definition));
        }
    }

    public RoleDefinitionsProvider(ResourceTypeCatalog catalog)
        : this(RequireDefinition(catalog), catalog)
    {
    }

    // ---- read ----

    public override async Task<ExistingResource?> ReadAsync(ResourceKey key, string? stateId, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(graph);

        if (stateId is not null && await graph.GetAsync(Select(new GraphRequest(EntityPath(stateId), key.Api)), cancellationToken).ConfigureAwait(false) is JsonObject pinned)
        {
            return Existing(pinned);
        }

        if (!key.TryGet(KeyProperty, out var name))
        {
            throw new ProviderException(EngineDiagnosticCodes.ResourceKeyUnresolved, $"{TypeName}: the key {key.Describe()} carries no displayName.");
        }

        return await FindCustomAsync(graph, name, key.Api, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The custom role named <paramref name="name"/>: <c>null</c> when none exists, <see cref="BuiltInRole"/> when only a built-in carries the name.</summary>
    private async Task<ExistingResource?> FindCustomAsync(IGraphClient graph, string name, GraphApi api, CancellationToken cancellationToken)
    {
        var matches = await FindByDisplayNameAsync(graph, name, api, cancellationToken).ConfigureAwait(false);
        var custom = matches.Where(m => !RoleDefinitionsRules.IsBuiltIn(m)).ToList();
        var id = ResourceIdentifier.Create(ResourceType, name);
        if (custom.Count == 0)
        {
            if (matches.Count > 0)
            {
                throw new ProviderException(BuiltInRole, $"{id.Id}: {RoleDefinitionsRules.BuiltInMessage(name)}", id);
            }

            return null;
        }

        if (custom.Count > 1)
        {
            string ids = string.Join(", ", custom.Select(m => JsonObjects.GetId(m) ?? "?"));
            throw new ProviderException(
                ProviderException.AmbiguousMatch,
                $"{custom.Count} custom role definitions have displayName '{name}' (ids: {ids}); pin the intended one with 'earm state adopt --resource-id {id.Id} --object-id <id>' or rename the others.",
                id);
        }

        return Existing(custom[0]);
    }

    // ---- plan ----

    public override ResourceChange Plan(ResourceInstance desired, ExistingResource? existing, PlanOptions options)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(options);

        if (desired.IsIgnored || desired.Properties is not { } properties)
        {
            return base.Plan(desired, existing, options);
        }

        if (existing is not null && RoleDefinitionsRules.IsBuiltIn(existing.Raw))
        {
            return Unsupported(desired, existing, $"{desired.Describe()}: {RoleDefinitionsRules.BuiltInMessage(desired.Id.NameLeaf)}");
        }

        var problems = RoleDefinitionsRules.NormalizeDesired(properties);
        if (problems.Count > 0)
        {
            return Unsupported(desired, existing, $"{desired.Describe()}: {string.Join("; ", problems)}.");
        }

        return base.Plan(desired, existing, options);
    }

    // ---- create ----

    public override async Task<AppliedResource> CreateAsync(ResourceInstance desired, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(graph);
        var properties = desired.Properties
            ?? throw new InvalidOperationException($"{desired.Describe()}: properties must be evaluated before creating.");

        RequireValid(desired, properties);
        var body = BuildCreateBody(desired);
        RoleDefinitionsRules.RemoveServerManaged(body);
        RoleDefinitionsRules.ApplyRootScope(body);

        var response = await graph.PostAsync(new GraphRequest(CollectionPath, desired.Api, hint: desired.RetryHint), body, cancellationToken).ConfigureAwait(false);
        var live = response.Body as JsonObject
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: Graph returned no object for the created role definition.", desired.Id);

        return new AppliedResource(desired.Id, RequireId(live, desired), live, ResolveKey(desired));
    }

    // ---- update ----

    public override async Task<AppliedResource> UpdateAsync(ResourceInstance desired, ExistingResource existing, ResourceChange change, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(graph);
        var properties = desired.Properties
            ?? throw new InvalidOperationException($"{desired.Describe()}: properties must be evaluated before updating.");

        RequireValid(desired, properties);
        var body = BuildPatchBody(desired, existing, change);
        RoleDefinitionsRules.RemoveServerManaged(body);

        string path = EntityPath(existing.GraphId);
        JsonObject live;
        if (body.Count == 0)
        {
            live = (JsonObject)JsonValues.Clone(existing.Raw)!;
        }
        else
        {
            await graph.PatchAsync(new GraphRequest(path, desired.Api, hint: desired.RetryHint), body, cancellationToken).ConfigureAwait(false);
            live = await graph.GetAsync(Select(new GraphRequest(path, desired.Api, hint: desired.RetryHint)), cancellationToken).ConfigureAwait(false) as JsonObject
                ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: the role definition ({existing.GraphId}) vanished after the update.", desired.Id);
        }

        return new AppliedResource(desired.Id, existing.GraphId, live, ResolveKey(desired));
    }

    // ---- delete ----

    public override async Task DeleteAsync(StateEntry entry, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(graph);
        var api = entry.ToResourceKey(Catalog).Api;
        try
        {
            await graph.DeleteAsync(new GraphRequest(EntityPath(entry.GraphId), api), cancellationToken).ConfigureAwait(false);
        }
        catch (GraphException ex) when (ex.IsNotFound)
        {
            // Already gone (deleted outside EARM): nothing to do.
        }
        catch (GraphException ex) when (ex.Status == HttpStatusCode.BadRequest)
        {
            // Graph refuses to delete a role definition while role assignments reference it (plan section 3.2).
            throw new ProviderException(
                RoleDefinitionInUse,
                $"{entry.Id.Id}: Graph refused to delete role definition {entry.GraphId} ({ex.Code}: {ex.GraphMessage}). A custom role cannot be deleted while role assignments reference it; remove the Microsoft.Entra/roleAssignments resources and the directoryRoles entries that name it first (Complete mode deletes assignments before role definitions), or remove the remaining assignments in the portal.",
                entry.Id,
                ex);
        }
    }

    // ---- Graph helpers ----

    /// <summary><c>GET /roleManagement/directory/roleDefinitions?$filter=displayName eq '…'</c> (quotes doubled), every page, matched client-side.</summary>
    private async Task<IReadOnlyList<JsonObject>> FindByDisplayNameAsync(IGraphClient graph, string displayName, GraphApi api, CancellationToken cancellationToken)
    {
        string filter = KeyProperty + " eq '" + displayName.Replace("'", "''", StringComparison.Ordinal) + "'";
        var request = Select(new GraphRequest(CollectionPath, api).WithFilter(filter));
        var matches = new List<JsonObject>();
        await foreach (var item in graph.GetAllPagesAsync(request, cancellationToken).ConfigureAwait(false))
        {
            if (item is JsonObject role && string.Equals(JsonObjects.GetString(role, KeyProperty), displayName, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(role);
            }
        }

        return matches;
    }

    private static GraphRequest Select(GraphRequest request)
    {
        return request.WithSelect(RoleDefinitionsRules.ManagedProperties);
    }

    /// <summary>The live object wrapped for the planner, its diff view with canonical action sets.</summary>
    private ExistingResource Existing(JsonObject live)
    {
        var existing = ToExisting(live);
        return existing with { Properties = RoleDefinitionsRules.NormalizeLive(existing.Properties) };
    }

    private string EntityPath(string graphId)
    {
        return CollectionPath + "/" + GraphPath.Segment(graphId);
    }

    // ---- helpers ----

    private static ResourceTypeDefinition RequireDefinition(ResourceTypeCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.Find(ResourceType)
            ?? throw new InvalidOperationException($"The catalog does not define '{ResourceType}'.");
    }

    /// <summary>The same normalisation as <see cref="Plan"/>, for callers that skip planning; problems are a hard error here.</summary>
    private static void RequireValid(ResourceInstance desired, JsonObject properties)
    {
        var problems = RoleDefinitionsRules.NormalizeDesired(properties);
        if (problems.Count > 0)
        {
            throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: {string.Join("; ", problems)}.", desired.Id);
        }
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
}
