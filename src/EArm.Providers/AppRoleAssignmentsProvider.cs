using System;
using System.Collections.Generic;
using System.Linq;
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
/// <c>Microsoft.Entra/appRoleAssignments</c> (plan sections 1.3, 1.4, 3.2; work item WI-12). The resource name is a
/// deterministic GUID the author picks with <c>guid(...)</c>; the Graph object is found by the composite key
/// (<c>principalId</c>, <c>resourceId</c>, <c>appRoleId</c>) and its id pinned in the state. Sugar accepted in
/// <c>properties</c>: <c>resourceAppId</c> (the resource application's id, resolved to the service principal object
/// id) and <c>appRole</c> (the role's <c>value</c>, resolved to <c>appRoleId</c> through the resource principal's
/// <c>appRoles</c>; <c>00000000-0000-0000-0000-000000000000</c> is default access). Every property is immutable: a
/// changed composite under the same name is <see cref="ChangeMode.Recreate"/> (delete the old assignment, create the
/// new one).
/// <para>
/// Requests per operation:
/// <list type="bullet">
/// <item><b>Prepare</b> (before the key): <c>GET /servicePrincipals(appId='…')</c> (sugar) or <c>GET /servicePrincipals/{resourceId}</c>, both <c>$select=id,appId,displayName,appRoles,oauth2PermissionScopes</c>, under the instance's retry hint; plus <c>GET /directoryObjects/{principalId}?$select=id</c> when the role restricts <c>allowedMemberTypes</c>. A disabled role, a role the principal's kind may not hold, or an unknown role value is a <see cref="ProviderException"/> (<see cref="InvalidAssignment"/>): an unsupported what-if row, a failed create.</item>
/// <item><b>Read</b>: <c>GET /servicePrincipals/{resourceId}/appRoleAssignedTo/{pinnedId}</c> (state id, when known) → <c>GET /servicePrincipals/{resourceId}/appRoleAssignedTo?$top=999</c> (every page) matched on <c>principalId</c> + <c>appRoleId</c>; a 404 of the resource principal is "does not exist".</item>
/// <item><b>Create</b>: <c>POST /servicePrincipals/{resourceId}/appRoleAssignedTo { principalId, resourceId, appRoleId }</c> under the instance's retry hint (<see cref="RetryHint.ExpectReplicationLag"/> when the principal or the resource was created in this run: the Az layer retries 404 / <c>PrincipalNotFound</c> for up to 90 s). A 400 "Permission being assigned already exists" is idempotent success: the assignment is re-read by composite and adopted.</item>
/// <item><b>Update</b>: never (every property is immutable; the planner asks for a recreate).</item>
/// <item><b>Delete</b>: <c>DELETE /servicePrincipals/{resourceId}/appRoleAssignedTo/{id}</c>; a 404 is "already gone".</item>
/// </list>
/// The principal-side <c>appRoleAssignments</c> property of users, groups and service principals produces the same
/// edge (see <see cref="Edges.AppRoleAssignmentEdgeKind"/>): the object pass creates the standalone resource first,
/// so the edge pass finds the assignment in place, writes nothing and records nothing for the principal.
/// <see cref="AppliedResource.Extra"/> records the resolved ids (<c>principalId</c>, <c>resourceId</c>, <c>appRoleId</c>).
/// </para>
/// </summary>
[EntraProvider(ResourceType)]
public sealed class AppRoleAssignmentsProvider : ProviderBase
{
    public const string ResourceType = "Microsoft.Entra/appRoleAssignments";

    /// <summary><see cref="ProviderException"/> code: the declared assignment cannot be made (unknown role, disabled role, wrong principal kind, bad shape).</summary>
    public const string InvalidAssignment = "AppRoleAssignmentInvalid";

    public AppRoleAssignmentsProvider(ResourceTypeDefinition definition, ResourceTypeCatalog? catalog = null)
        : base(definition, catalog)
    {
        if (!ResourceIdentifier.TypeEquals(definition.Type, ResourceType))
        {
            throw new ArgumentException($"{nameof(AppRoleAssignmentsProvider)} implements '{ResourceType}', not '{definition.Type}'.", nameof(definition));
        }
    }

    public AppRoleAssignmentsProvider(ResourceTypeCatalog catalog)
        : this(RequireDefinition(catalog), catalog)
    {
    }

    /// <summary>Every property is immutable: a composite change under the same name replaces the assignment.</summary>
    protected override bool RecreateOnImmutableChange => true;

    // ---- prepare (sugar + live validation) ----

    public override async Task PrepareAsync(ResourceInstance desired, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(graph);
        var properties = desired.Properties
            ?? throw new InvalidOperationException($"{desired.Describe()}: properties must be evaluated before preparing.");

        var declared = AppRoleAssignmentsRules.Parse(properties, principalAllowed: true, out var error)
            ?? throw Invalid(desired, error!);

        // Resource: resourceAppId → service principal object id (sugar), or resourceId checked to exist.
        ServicePrincipalSummary? resource = null;
        if (declared.ResourceAppId is { } resourceAppId)
        {
            resource = await ServicePrincipalLookup.FindByAppIdAsync(graph, resourceAppId, desired.Api, desired.RetryHint, cancellationToken).ConfigureAwait(false)
                ?? throw Invalid(desired, $"no service principal exists for application id '{resourceAppId}' (resourceAppId); deploy a Microsoft.Entra/servicePrincipals resource for it, or declare resourceId.");
            if (declared.ResourceId is { } declaredResourceId && !JsonObjects.KeyEquals(declaredResourceId, resource.Id))
            {
                throw Invalid(desired, $"resourceAppId '{resourceAppId}' resolves to service principal {resource.Id}, which differs from the declared resourceId {declaredResourceId}; declare one of them.");
            }

            Set(properties, "resourceId", JsonValue.Create(resource.Id));
            Remove(properties, "resourceAppId");
        }
        else if (JsonObjects.TryGetIgnoreCase(properties, "resourceAppId", out var unknownAppId) && UnknownJson.IsUnknown(unknownAppId))
        {
            // What-if: the resource application does not exist yet; the id is a deploy-time value.
            if (declared.ResourceId is null)
            {
                Set(properties, "resourceId", JsonValues.Clone(unknownAppId));
            }

            Remove(properties, "resourceAppId");
        }
        else if (declared.ResourceId is { } resourceId)
        {
            resource = await AppRoleAssignmentsLookup.FindByIdAsync(graph, resourceId, desired.Api, desired.RetryHint, cancellationToken).ConfigureAwait(false)
                ?? throw Invalid(desired, $"resource service principal '{resourceId}' does not exist.");
        }

        // Role: appRole (value) → appRoleId (sugar), or appRoleId checked against the resource's app roles.
        JsonObject? role = null;
        string? appRoleId = declared.AppRoleId;
        if (declared.AppRole is { } appRole)
        {
            if (resource is null)
            {
                if (appRoleId is null)
                {
                    Set(properties, "appRoleId", UnknownJson.Create($"appRole('{appRole}')", UnknownOrigins.Reference));
                }
            }
            else
            {
                string? roleError = AppRoleAssignmentsRules.ResolveRole(resource, appRoleId, appRole, out var resolvedId, out role);
                if (roleError is not null)
                {
                    throw Invalid(desired, roleError);
                }

                appRoleId = resolvedId;
                Set(properties, "appRoleId", JsonValue.Create(resolvedId));
            }

            Remove(properties, "appRole");
        }
        else if (JsonObjects.TryGetIgnoreCase(properties, "appRole", out var unknownRole) && UnknownJson.IsUnknown(unknownRole))
        {
            if (appRoleId is null)
            {
                Set(properties, "appRoleId", JsonValues.Clone(unknownRole));
            }

            Remove(properties, "appRole");
        }
        else if (resource is not null && appRoleId is not null)
        {
            string? roleError = AppRoleAssignmentsRules.ResolveRole(resource, appRoleId, null, out _, out role);
            if (roleError is not null)
            {
                throw Invalid(desired, roleError);
            }
        }

        // Validation needing the live role: disabled, or the principal's kind is not an allowed member type.
        if (role is not null)
        {
            string? memberType = null;
            if (AppRoleAssignmentsRules.RestrictsMemberType(role) && declared.PrincipalId is { } principalId)
            {
                memberType = await AppRoleAssignmentsLookup.MemberTypeOfPrincipalAsync(graph, principalId, desired.Api, desired.RetryHint, cancellationToken).ConfigureAwait(false);
            }

            if (AppRoleAssignmentsRules.CheckAssignable(role, memberType) is { } reason)
            {
                throw Invalid(desired, reason);
            }
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

        if (!desired.IsSymbolic && !AppRoleAssignmentsRules.IsGuid(desired.Id.NameLeaf))
        {
            return Unsupported(desired, existing, $"{desired.Describe()}: the name '{desired.Id.NameLeaf}' is not a GUID; name app role assignments with a deterministic GUID, for example [guid(variables('groupName'), variables('apiName'), 'Orders.Admin')].");
        }

        foreach (var property in properties)
        {
            if (!AppRoleAssignmentsRules.AllowedProperties.Contains(property.Key, StringComparer.OrdinalIgnoreCase))
            {
                return Unsupported(desired, existing, $"{desired.Describe()}: properties.{property.Key} is not a property of an app role assignment; expected principalId, resourceId or resourceAppId, and appRoleId or appRole.");
            }
        }

        if (existing is null)
        {
            // Not yet prepared (a symbolic name is planned before PrepareAsync runs): sugar stands in for the canonical property.
            StandIn(properties, "resourceAppId", "resourceId");
            StandIn(properties, "appRole", "appRoleId");
        }

        var change = base.Plan(desired, existing, options);
        if (change is { Type: ChangeType.Modify, Mode: ChangeMode.Patch, HasError: false })
        {
            // The overlay marks every property immutable ("*"): the base plan has no per-property list to match, so
            // any effective difference replaces the assignment.
            return change with
            {
                Mode = ChangeMode.Recreate,
                Reasons = [.. change.Reasons, "the composite key (principalId, resourceId, appRoleId) changed under the same name; the assignment is deleted and created again"],
            };
        }

        return change;
    }

    // ---- read ----

    public override async Task<ExistingResource?> ReadAsync(ResourceKey key, string? stateId, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(graph);

        var (principalId, resourceId, appRoleId) = RequireComposite(key, null);
        if (stateId is not null)
        {
            var pinned = await graph.GetAsync(new GraphRequest(AppRoleAssignmentsLookup.AssignedToPath(resourceId) + "/" + GraphPath.Segment(stateId), key.Api), cancellationToken).ConfigureAwait(false) as JsonObject;
            if (pinned is not null)
            {
                return ToExisting(pinned);
            }
        }

        var found = await AppRoleAssignmentsLookup.FindAssignmentAsync(graph, resourceId, principalId, appRoleId, key.Api, RetryHint.Default, cancellationToken).ConfigureAwait(false);
        return found is null ? null : ToExisting(found);
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
        var (principalId, resourceId, appRoleId) = RequireComposite(key, desired.Id);
        var body = new JsonObject
        {
            ["principalId"] = principalId,
            ["resourceId"] = resourceId,
            ["appRoleId"] = appRoleId,
        };

        JsonObject live;
        try
        {
            var response = await graph.PostAsync(new GraphRequest(AppRoleAssignmentsLookup.AssignedToPath(resourceId), desired.Api, hint: desired.RetryHint), body, cancellationToken).ConfigureAwait(false);
            live = response.Body as JsonObject
                ?? await AppRoleAssignmentsLookup.FindAssignmentAsync(graph, resourceId, principalId, appRoleId, desired.Api, RetryHint.ExpectReplicationLag, cancellationToken).ConfigureAwait(false)
                ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: the create returned no object and the assignment could not be read back.", desired.Id);
        }
        catch (GraphException ex) when (ex.IsAlreadyExists)
        {
            // "Permission being assigned already exists on the object": created by another run or not yet visible
            // to the read that planned this create. Adopt it.
            live = await AppRoleAssignmentsLookup.FindAssignmentAsync(graph, resourceId, principalId, appRoleId, desired.Api, RetryHint.ExpectReplicationLag, cancellationToken).ConfigureAwait(false)
                ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: Graph reported that the assignment already exists, but it could not be read back.", desired.Id);
        }

        var extra = new JsonObject
        {
            ["principalId"] = principalId,
            ["resourceId"] = resourceId,
            ["appRoleId"] = appRoleId,
        };
        return new AppliedResource(desired.Id, RequireId(live, desired), live, key, extra);
    }

    // ---- update ----

    public override Task<AppliedResource> UpdateAsync(ResourceInstance desired, ExistingResource existing, ResourceChange change, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: app role assignments are never edited in place; the planner asks for a recreate.", desired.Id);
    }

    // ---- delete ----

    public override async Task DeleteAsync(StateEntry entry, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(graph);
        var key = entry.ToResourceKey(Catalog);
        string resourceId = key.TryGet("resourceId", out var fromKey) ? fromKey
            : JsonObjects.GetString(entry.Extra, "resourceId")
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceKeyUnresolved, $"{TypeName}: the state entry '{entry.Id.Id}' records no resourceId, so the assignment {entry.GraphId} cannot be addressed.", entry.Id);

        try
        {
            await graph.DeleteAsync(new GraphRequest(AppRoleAssignmentsLookup.AssignedToPath(resourceId) + "/" + GraphPath.Segment(entry.GraphId), key.Api), cancellationToken).ConfigureAwait(false);
        }
        catch (GraphException ex) when (ex.IsNotFound)
        {
            // Already gone (the resource or the assignment was deleted outside EARM).
        }
    }

    // ---- helpers ----

    private static ResourceTypeDefinition RequireDefinition(ResourceTypeCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.Find(ResourceType)
            ?? throw new InvalidOperationException($"The catalog does not define '{ResourceType}'.");
    }

    private (string PrincipalId, string ResourceId, string AppRoleId) RequireComposite(ResourceKey key, ResourceIdentifier? id)
    {
        if (!key.TryGet("principalId", out var principalId) || !key.TryGet("resourceId", out var resourceId) || !key.TryGet("appRoleId", out var appRoleId))
        {
            throw new ProviderException(EngineDiagnosticCodes.ResourceKeyUnresolved, $"{TypeName}: the key {key.Describe()} does not carry principalId, resourceId and appRoleId.", id);
        }

        return (principalId, resourceId, appRoleId);
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
}
