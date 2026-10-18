using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Core;
using EArm.Core.Types;
using EArm.Engine;
using EArm.Engine.Providers;
using EArm.Expressions.Values;

namespace EArm.Providers;

/// <summary>
/// <c>Microsoft.Entra/roleAssignments</c>, permanent active directory role assignments. The resource name is a deterministic GUID the author picks with <c>guid(...)</c>; the Graph
/// object is found by the composite key (<c>roleDefinitionId</c>, <c>principalId</c>, <c>directoryScopeId</c>) and
/// read live on every run. Sugar accepted in <c>properties</c>: <c>role</c> (a role template id, a built-in
/// display name resolved from <see cref="WellKnownIds.RoleTemplates"/>, a custom role's display name looked up in
/// the tenant, or a custom role id) → <c>roleDefinitionId</c>, and <c>administrativeUnit</c> (a unit's id or
/// displayName) → <c>directoryScopeId: /administrativeUnits/{id}</c>; <c>directoryScopeId</c> defaults to <c>/</c>.
/// Every property is part of the key: a changed composite under the same name is another assignment, created (the old one is retained and reported by the orphan report).
/// <para>
/// Requests per operation:
/// <list type="bullet">
/// <item><b>Prepare</b> (before the key): <c>[GET /roleManagement/directory/roleDefinitions?$filter=displayName eq '…']</c> for a custom role name, <c>[GET /directory/administrativeUnits?$filter=displayName eq '…']</c> for a unit name, then <c>GET /directoryObjects/{principalId}?$select=id</c> (the principal's kind) and, for a group, <c>GET /groups/{principalId}?$select=id,isAssignableToRole</c>; all under the instance's retry hint. An unknown role or unit name, a missing principal, a group that is not role-assignable or any other kind of object is <see cref="ProviderException"/> (<see cref="InvalidAssignment"/>): an unsupported what-if row, a failed create.</item>
/// <item><b>Read</b>: <c>GET /roleManagement/directory/roleAssignments?$filter=principalId eq '{principalId}'</c> (every page) matched client-side on <c>roleDefinitionId</c> + <c>directoryScopeId</c>.</item>
/// <item><b>Create</b>: <c>POST /roleManagement/directory/roleAssignments { "@odata.type": "#microsoft.graph.unifiedRoleAssignment", roleDefinitionId, principalId, directoryScopeId }</c> under the instance's retry hint. A 400 "A conflicting object with one or more of the specified property values is present in the directory" is idempotent success: the assignment is re-read by composite and adopted.</item>
/// <item><b>Update</b>: never (every property is part of the key, so an assignment found by its key has nothing to update).</item>
/// <item><b>Delete</b>: <c>DELETE /roleManagement/directory/roleAssignments/{id}</c>; a 404 is "already gone".</item>
/// </list>
/// <c>roleDefinitionId</c> is always a role template id (or a custom role's definition id), never the object id of an
/// activated <c>/directoryRoles/{id}</c>. Scopes: <c>/</c> (tenant), <c>/administrativeUnits/{id}</c>, <c>/{applicationObjectId}</c>.
/// Assigning Global Administrator needs Privileged Role Administrator rights; the what-if row says so.
/// The principal-side <c>directoryRoles</c> property of users, role-assignable groups and service principals
/// produces the same edge (<see cref="Edges.DirectoryRoleEdgeKind"/>): the object pass applies the standalone
/// resource first, so the edge pass finds the assignment in place, writes nothing and records nothing for the
/// principal. <see cref="AppliedResource.Extra"/> records the resolved ids.
/// </para>
/// </summary>
[EntraProvider(ResourceType)]
public sealed class RoleAssignmentsProvider : ProviderBase, IOrphanCandidateSource
{
    public const string ResourceType = "Microsoft.Entra/roleAssignments";

    /// <summary><see cref="ProviderException"/> code: the declared assignment cannot be made (unknown role or unit name, wrong principal kind, bad shape).</summary>
    public const string InvalidAssignment = EngineDiagnosticCodes.RoleAssignmentInvalid;

    public RoleAssignmentsProvider(ResourceTypeDefinition definition, ResourceTypeCatalog? catalog = null)
        : base(definition, catalog)
    {
        if (!ResourceIdentifier.TypeEquals(definition.Type, ResourceType))
        {
            throw new ArgumentException($"{nameof(RoleAssignmentsProvider)} implements '{ResourceType}', not '{definition.Type}'.", nameof(definition));
        }
    }

    public RoleAssignmentsProvider(ResourceTypeCatalog catalog)
        : this(RequireDefinition(catalog), catalog)
    {
    }

    /// <summary>Every property is immutable; since every one is also part of the key, an assignment found by its key never differs, so this is a safeguard: a difference would replace it.</summary>
    protected override bool RecreateOnImmutableChange => true;

    // ---- prepare (sugar + live validation) ----

    public override async Task PrepareAsync(ResourceInstance desired, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(graph);
        var properties = desired.Properties
            ?? throw new InvalidOperationException($"{desired.Describe()}: properties must be evaluated before preparing.");

        var declared = RoleAssignmentsRules.Parse(properties, principalAllowed: true, out var error)
            ?? throw Invalid(desired, error!);

        // Role: role (template id | built-in name | custom name | custom id) → roleDefinitionId (sugar).
        if (declared.Role is { } role)
        {
            var resolved = await RoleAssignmentsLookup.ResolveRoleAsync(graph, role, desired.Api, desired.RetryHint, cancellationToken).ConfigureAwait(false);
            if (resolved.Id is null)
            {
                throw Invalid(desired, resolved.Error!);
            }

            if (declared.RoleDefinitionId is { } declaredRole && !JsonObjects.KeyEquals(declaredRole, resolved.Id))
            {
                throw Invalid(desired, $"role '{role}' resolves to role definition {resolved.Id}, which differs from the declared roleDefinitionId {declaredRole}; declare one of them.");
            }

            Set(properties, "roleDefinitionId", JsonValue.Create(resolved.Id));
            Remove(properties, "role");
        }
        else if (JsonObjects.TryGetIgnoreCase(properties, "role", out var unknownRole) && UnknownJson.IsUnknown(unknownRole))
        {
            // What-if: the role (a custom role deployed in this template) does not exist yet.
            if (declared.RoleDefinitionId is null)
            {
                Set(properties, "roleDefinitionId", JsonValues.Clone(unknownRole));
            }

            Remove(properties, "role");
        }
        else if (declared.RoleDefinitionId is { } roleDefinitionId)
        {
            Set(properties, "roleDefinitionId", JsonValue.Create(RoleAssignmentsRules.NormalizeId(roleDefinitionId)));
        }

        // Scope: administrativeUnit (id | name) → directoryScopeId (sugar); default "/".
        if (declared.AdministrativeUnit is { } unit)
        {
            var resolved = await RoleAssignmentsLookup.ResolveAdministrativeUnitScopeAsync(graph, unit, desired.Api, desired.RetryHint, cancellationToken).ConfigureAwait(false);
            if (resolved.Id is null)
            {
                throw Invalid(desired, resolved.Error!);
            }

            if (declared.DirectoryScopeId is { } declaredScope && !RoleAssignmentsRules.ScopeEquals(declaredScope, resolved.Id))
            {
                throw Invalid(desired, $"administrativeUnit '{unit}' resolves to scope {resolved.Id}, which differs from the declared directoryScopeId '{declaredScope}'; declare one of them.");
            }

            Set(properties, "directoryScopeId", JsonValue.Create(resolved.Id));
            Remove(properties, "administrativeUnit");
        }
        else if (JsonObjects.TryGetIgnoreCase(properties, "administrativeUnit", out var unknownUnit) && UnknownJson.IsUnknown(unknownUnit))
        {
            if (declared.DirectoryScopeId is null)
            {
                Set(properties, "directoryScopeId", JsonValues.Clone(unknownUnit));
            }

            Remove(properties, "administrativeUnit");
        }
        else if (!JsonObjects.TryGetIgnoreCase(properties, "directoryScopeId", out var scopeNode) || JsonObjects.IsAbsent(scopeNode))
        {
            Set(properties, "directoryScopeId", JsonValue.Create(RoleAssignmentsRules.RootScope));
        }
        else if (declared.DirectoryScopeId is { } scope)
        {
            Set(properties, "directoryScopeId", JsonValue.Create(RoleAssignmentsRules.NormalizeScope(scope)));
        }

        // Principal: must exist and be a user, a role-assignable group or a service principal.
        if (declared.PrincipalId is { } principalId)
        {
            string? collection = await RoleAssignmentsLookup.PrincipalCollectionAsync(graph, principalId, desired.Api, desired.RetryHint, cancellationToken).ConfigureAwait(false);
            bool? roleAssignable = collection == "groups"
                ? await RoleAssignmentsLookup.IsRoleAssignableGroupAsync(graph, principalId, desired.Api, desired.RetryHint, cancellationToken).ConfigureAwait(false)
                : null;
            if (RoleAssignmentsLookup.CheckPrincipalKind(collection, roleAssignable, principalId) is { } reason)
            {
                throw Invalid(desired, reason);
            }

            Set(properties, "principalId", JsonValue.Create(RoleAssignmentsRules.NormalizeId(principalId)));
        }
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

        if (!desired.IsSymbolic && !RoleAssignmentsRules.IsGuid(desired.Id.NameLeaf))
        {
            return Unsupported(desired, existing, $"{desired.Describe()}: the name '{desired.Id.NameLeaf}' is not a GUID; name role assignments with a deterministic GUID, for example [guid(variables('groupName'), 'Global Reader', '/')].");
        }

        foreach (var property in properties)
        {
            if (!RoleAssignmentsRules.AllowedProperties.Contains(property.Key, StringComparer.OrdinalIgnoreCase))
            {
                return Unsupported(desired, existing, $"{desired.Describe()}: properties.{property.Key} is not a property of a directory role assignment; expected roleDefinitionId or role, principalId, and directoryScopeId or administrativeUnit.");
            }
        }

        if (existing is null)
        {
            // Not yet prepared (a symbolic name is planned before PrepareAsync runs): sugar stands in for the canonical property.
            StandIn(properties, "role", "roleDefinitionId");
            StandIn(properties, "administrativeUnit", "directoryScopeId");
        }

        var change = base.Plan(desired, existing, options);
        if (!change.HasError && change.Type is ChangeType.Create or ChangeType.Modify && RoleAssignmentsRules.IsGlobalAdministrator(JsonObjects.GetString(properties, "roleDefinitionId")))
        {
            change = change with { Reasons = [.. change.Reasons, RoleAssignmentsRules.GlobalAdministratorReason] };
        }

        return change;
    }

    // ---- read ----

    public override async Task<ExistingResource?> ReadAsync(ResourceKey key, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(graph);

        var (roleDefinitionId, principalId, scope) = RequireComposite(key, null);
        var found = await RoleAssignmentsLookup.FindAssignmentAsync(graph, principalId, roleDefinitionId, scope, key.Api, RetryHint.Default, cancellationToken).ConfigureAwait(false);
        return found is null ? null : Existing(found);
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

        var key = ResolveKey(desired);
        var (roleDefinitionId, principalId, scope) = RequireComposite(key, desired.Id);
        var body = RoleAssignmentsRules.AssignmentBody(roleDefinitionId, principalId, scope);

        JsonObject live;
        try
        {
            var response = await graph.PostAsync(new GraphRequest(RoleAssignmentsRules.AssignmentsPath, desired.Api, hint: desired.RetryHint), body, cancellationToken).ConfigureAwait(false);
            live = response.Body as JsonObject
                ?? await RoleAssignmentsLookup.FindAssignmentAsync(graph, principalId, roleDefinitionId, scope, desired.Api, RetryHint.ExpectReplicationLag, cancellationToken).ConfigureAwait(false)
                ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: the create returned no object and the assignment could not be read back.", desired.Id);
        }
        catch (GraphException ex) when (ex.IsAlreadyExists)
        {
            // "A conflicting object with one or more of the specified property values is present in the directory":
            // created by another run or not yet visible to the read that planned this create. Adopt it.
            live = await RoleAssignmentsLookup.FindAssignmentAsync(graph, principalId, roleDefinitionId, scope, desired.Api, RetryHint.ExpectReplicationLag, cancellationToken).ConfigureAwait(false)
                ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: Graph reported that the assignment already exists, but it could not be read back.", desired.Id);
        }

        var extra = new JsonObject
        {
            ["roleDefinitionId"] = RoleAssignmentsRules.NormalizeId(roleDefinitionId),
            ["principalId"] = RoleAssignmentsRules.NormalizeId(principalId),
            ["directoryScopeId"] = RoleAssignmentsRules.NormalizeScope(scope),
        };
        return new AppliedResource(desired.Id, RequireId(live, desired), live, key, extra);
    }

    // ---- update ----

    public override Task<AppliedResource> UpdateAsync(ResourceInstance desired, ExistingResource existing, ResourceChange change, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: directory role assignments are never edited in place; the planner asks for a recreate.", desired.Id);
    }

    // ---- delete ----

    public override async Task DeleteAsync(ResourceKey key, ExistingResource live, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(graph);
        var api = key.Api;
        try
        {
            await graph.DeleteAsync(new GraphRequest(RoleAssignmentsLookup.AssignmentPath(live.GraphId), api), cancellationToken).ConfigureAwait(false);
        }
        catch (GraphException ex) when (ex.IsNotFound)
        {
            // Already gone (the principal or the assignment was deleted outside EARM).
        }
    }

    // ---- helpers ----

    private static ResourceTypeDefinition RequireDefinition(ResourceTypeCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.Find(ResourceType)
            ?? throw new InvalidOperationException($"The catalog does not define '{ResourceType}'.");
    }

    /// <summary>The live object wrapped for the planner, its diff view with the scope in canonical form.</summary>
    private ExistingResource Existing(JsonObject live)
    {
        var existing = ToExisting(live);
        var view = existing.Properties;
        string scopeKey = JsonObjects.FindKey(view, "directoryScopeId") ?? "directoryScopeId";
        view[scopeKey] = RoleAssignmentsRules.NormalizeScope(JsonObjects.GetString(view, "directoryScopeId"));
        if (JsonObjects.FindKey(view, "@odata.type") is { } odataKey)
        {
            view.Remove(odataKey);
        }

        return existing;
    }

    private (string RoleDefinitionId, string PrincipalId, string DirectoryScopeId) RequireComposite(ResourceKey key, ResourceIdentifier? id)
    {
        if (!key.TryGet("roleDefinitionId", out var roleDefinitionId) || !key.TryGet("principalId", out var principalId))
        {
            throw new ProviderException(EngineDiagnosticCodes.ResourceKeyUnresolved, $"{TypeName}: the key {key.Describe()} does not carry roleDefinitionId and principalId.", id);
        }

        string scope = key.TryGet("directoryScopeId", out var declaredScope) ? declaredScope : RoleAssignmentsRules.RootScope;
        return (roleDefinitionId, principalId, RoleAssignmentsRules.NormalizeScope(scope));
    }

    private ProviderException Invalid(ResourceInstance desired, string message)
    {
        return new ProviderException(InvalidAssignment, $"{desired.Describe()}: {message}", desired.Id);
    }

    private static void Set(JsonObject properties, string name, JsonNode? value)
    {
        string key = JsonObjects.FindKey(properties, name) ?? name;
        properties[key] = value;
    }

    private static void Remove(JsonObject properties, string name)
    {
        if (JsonObjects.FindKey(properties, name) is { } key)
        {
            properties.Remove(key);
        }
    }

    /// <summary>When only the sugar property is declared, a deploy-time placeholder stands in for the canonical one so the base plan does not report it missing.</summary>
    private static void StandIn(JsonObject properties, string sugar, string canonical)
    {
        bool hasCanonical = JsonObjects.TryGetIgnoreCase(properties, canonical, out var existing) && !JsonObjects.IsAbsent(existing);
        if (!hasCanonical && JsonObjects.TryGetIgnoreCase(properties, sugar, out var value) && !JsonObjects.IsAbsent(value))
        {
            properties[canonical] = UnknownJson.IsUnknown(value, out var unknown) ? UnknownJson.Create(unknown) : UnknownJson.Create(sugar, UnknownOrigins.Reference);
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

    // ---- orphan report ----

    /// <summary>The orphan report lists the directory role assignments of the principals the declared assignments name (reported as retained).</summary>
    public OrphanScope OrphanScope => OrphanScope.DeclaredPrincipals;

    public async Task<IReadOnlyList<OrphanCandidate>> ListOrphanCandidatesAsync(OrphanQuery query, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(graph);
        var candidates = new List<OrphanCandidate>();
        foreach (var principalId in OrphanListing.DistinctKeyValues(query.DeclaredKeys, "principalId"))
        {
            foreach (var assignment in await RoleAssignmentsLookup.ReadPrincipalAssignmentsAsync(graph, principalId, query.Api, RetryHint.Default, cancellationToken).ConfigureAwait(false))
            {
                if (JsonObjects.GetId(assignment) is { } id)
                {
                    candidates.Add(new OrphanCandidate(id, id, null, OrphanListing.Describe(assignment, "principalId", "roleDefinitionId", "directoryScopeId")));
                }
            }
        }

        return candidates;
    }
}
