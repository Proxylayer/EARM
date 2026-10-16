using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core;
using EArm.Core.Types;
using EArm.Core.Validation;
using EArm.Engine;
using EArm.Engine.Providers;
using EArm.Graph;
using GraphApi = EArm.Graph.GraphApi;

namespace EArm.Providers;

/// <summary>
/// <c>Microsoft.Entra/roleEligibilityScheduleRequests</c>: PIM eligibility for directory roles, shaped like ARM's
/// <c>Microsoft.Authorization/roleEligibilityScheduleRequests</c>. The resource name is a deterministic GUID (a label); the eligibility is found by the
/// tuple (<c>principalId</c>, <c>roleDefinitionId</c>, <c>directoryScopeId</c> or <c>appScopeId</c>) and read live on every run. Sugar:
/// <c>role</c> → <c>roleDefinitionId</c> and <c>administrativeUnit</c> → <c>directoryScopeId</c> (as for <c>roleAssignments</c>); the scope defaults to
/// <c>/</c> and an absent <c>scheduleInfo</c> to a permanent eligibility. The pure rules are <see cref="RoleEligibilityScheduleRules"/>; the shared
/// lifecycle (adminAssign / adminUpdate / adminRemove, comparison, RoleAssignmentExists adoption, PimPolicyViolation) is
/// <see cref="PimScheduleRequestsProvider{TTuple}"/>.
/// <para>
/// Requests per operation:
/// <list type="bullet">
/// <item><b>Prepare</b>: the role / unit name lookups of <see cref="RoleAssignmentsLookup"/>, then <c>GET /directoryObjects/{principalId}</c> and, for a
/// group, <c>GET /groups/{id}?$select=id,isAssignableToRole</c>: a group that is not role-assignable cannot be made eligible (<see cref="InvalidEligibility"/>).</item>
/// <item><b>Read</b>: <c>GET /roleManagement/directory/roleEligibilitySchedules?$filter=principalId eq … and roleDefinitionId eq … and directoryScopeId eq …</c>,
/// keeping <c>memberType</c> Direct (<see cref="RoleEligibilitySchedulesLookup"/>). Schedules include future-dated eligibilities.</item>
/// <item><b>Create / Update / Delete</b>: <c>POST /roleManagement/directory/roleEligibilityScheduleRequests</c> with action adminAssign / adminUpdate / adminRemove.</item>
/// </list>
/// A changed tuple is a new eligibility: the old one is reported as retained (orphan scope: the declared principals).
/// </para>
/// </summary>
[EntraProvider(ResourceType)]
public sealed class RoleEligibilityScheduleRequestsProvider : PimScheduleRequestsProvider<EligibilityTuple>
{
    public const string ResourceType = RoleEligibilityScheduleRules.ResourceType;

    /// <summary><see cref="ProviderException"/> code: the declared eligibility cannot be requested (unknown role or unit, wrong principal kind, bad shape).</summary>
    public const string InvalidEligibility = EngineDiagnosticCodes.RoleEligibilityInvalid;

    private static readonly IReadOnlyList<(string Sugar, string Canonical)> Sugar = [("role", "roleDefinitionId"), ("administrativeUnit", "directoryScopeId")];

    public RoleEligibilityScheduleRequestsProvider(ResourceTypeDefinition definition, ResourceTypeCatalog? catalog = null)
        : base(definition, catalog, ResourceType)
    {
    }

    public RoleEligibilityScheduleRequestsProvider(ResourceTypeCatalog catalog)
        : this(RequireDefinition(catalog, ResourceType), catalog)
    {
    }

    protected override string RequestsPath => RoleEligibilitySchedulesLookup.RequestsPath;

    protected override string InvalidCode => InvalidEligibility;

    protected override string PolicyOwner => "the role's PIM policy";

    protected override string PolicyAdvice => "Change scheduleInfo (for example a policy that forbids permanent eligibility needs an AfterDateTime or AfterDuration expiration), justification or ticketInfo to satisfy the policy, or change the policy.";

    protected override string NameExample => "[guid(variables('groupName'), 'Global Reader', 'eligible')]";

    protected override IReadOnlyList<(string Sugar, string Canonical)> SugarProperties => Sugar;

    protected override IReadOnlyList<PimRequestFinding> Check(JsonObject properties)
    {
        return RoleEligibilityScheduleRules.Check(properties, IsOpaque);
    }

    protected override void NormalizeRules(JsonObject properties)
    {
        PimScheduleRules.Normalize(properties, IsOpaque);
    }

    // ---- prepare (sugar + live validation) ----

    public override async Task PrepareAsync(ResourceInstance desired, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(graph);
        var properties = PreparedProperties(desired);

        if (Check(properties) is { Count: > 0 } findings)
        {
            throw Invalid(desired, string.Join("; ", findings.Select(f => f.Message)));
        }

        // Role: role (template id | built-in name | custom name | custom id) → roleDefinitionId.
        if (Text(properties, "role") is { } role)
        {
            var resolved = await RoleAssignmentsLookup.ResolveRoleAsync(graph, role, desired.Api, desired.RetryHint, cancellationToken).ConfigureAwait(false);
            Set(properties, "roleDefinitionId", JsonValue.Create(resolved.Id ?? throw Invalid(desired, resolved.Error!)));
            Remove(properties, "role");
        }
        else if (Unknown(properties, "role") is { } unknownRole)
        {
            Set(properties, "roleDefinitionId", unknownRole);
            Remove(properties, "role");
        }
        else if (Text(properties, "roleDefinitionId") is { } roleDefinitionId)
        {
            Set(properties, "roleDefinitionId", JsonValue.Create(RoleAssignmentsRules.NormalizeId(roleDefinitionId)));
        }

        // Scope: administrativeUnit (id | name) → directoryScopeId; neither a directory nor an app scope → "/".
        if (Text(properties, "administrativeUnit") is { } unit)
        {
            var resolved = await RoleAssignmentsLookup.ResolveAdministrativeUnitScopeAsync(graph, unit, desired.Api, desired.RetryHint, cancellationToken).ConfigureAwait(false);
            Set(properties, "directoryScopeId", JsonValue.Create(resolved.Id ?? throw Invalid(desired, resolved.Error!)));
            Remove(properties, "administrativeUnit");
        }
        else if (Unknown(properties, "administrativeUnit") is { } unknownUnit)
        {
            Set(properties, "directoryScopeId", unknownUnit);
            Remove(properties, "administrativeUnit");
        }
        else if (Text(properties, "directoryScopeId") is { } scope)
        {
            Set(properties, "directoryScopeId", JsonValue.Create(RoleAssignmentsRules.NormalizeScope(scope)));
        }
        else if (!Declares(properties, "directoryScopeId") && !Declares(properties, "appScopeId"))
        {
            Set(properties, "directoryScopeId", JsonValue.Create(RoleAssignmentsRules.RootScope));
        }

        // Principal: must exist and be a user, a role-assignable group or a service principal.
        if (Text(properties, "principalId") is { } principalId)
        {
            string? collection = await RoleAssignmentsLookup.PrincipalCollectionAsync(graph, principalId, desired.Api, desired.RetryHint, cancellationToken).ConfigureAwait(false);
            bool? roleAssignable = collection == "groups"
                ? await RoleAssignmentsLookup.IsRoleAssignableGroupAsync(graph, principalId, desired.Api, desired.RetryHint, cancellationToken).ConfigureAwait(false)
                : null;
            if (collection == "groups" && roleAssignable != true)
            {
                throw Invalid(desired, $"group {principalId} is not role-assignable; PIM can only make a group eligible for a directory role when it was created with isAssignableToRole: true (the property is immutable, so recreate the group).");
            }

            if (RoleAssignmentsLookup.CheckPrincipalKind(collection, roleAssignable, principalId) is { } reason)
            {
                throw Invalid(desired, reason);
            }

            Set(properties, "principalId", JsonValue.Create(RoleAssignmentsRules.NormalizeId(principalId)));
        }

        Normalize(properties);
    }

    // ---- the type's Graph side ----

    protected override Task<JsonObject?> FindAsync(IGraphClient graph, EligibilityTuple tuple, GraphApi api, RetryHint hint, CancellationToken cancellationToken)
    {
        return RoleEligibilitySchedulesLookup.FindAsync(graph, tuple, api, hint, cancellationToken);
    }

    protected override JsonObject RequestBody(string action, EligibilityTuple tuple, JsonNode? scheduleInfo, JsonNode? justification, JsonNode? ticketInfo)
    {
        return RoleEligibilitySchedulesLookup.RequestBody(action, tuple, scheduleInfo, justification, ticketInfo);
    }

    protected override JsonObject Extra(EligibilityTuple tuple)
    {
        return RoleEligibilitySchedulesLookup.TupleObject(tuple);
    }

    protected override JsonObject LiveView(JsonObject live)
    {
        return RoleEligibilitySchedulesLookup.LiveView(live);
    }

    protected override EligibilityTuple RequireTuple(ResourceKey key, ResourceIdentifier? id)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!key.TryGet("principalId", out var principalId) || !key.TryGet("roleDefinitionId", out var roleDefinitionId))
        {
            throw new ProviderException(EngineDiagnosticCodes.ResourceKeyUnresolved, $"{TypeName}: the key {key.Describe()} does not carry principalId and roleDefinitionId.", id);
        }

        return key.TryGet("appScopeId", out var appScope) && !key.TryGet("directoryScopeId", out _)
            ? new EligibilityTuple(principalId, roleDefinitionId, appScope, IsAppScope: true)
            : new EligibilityTuple(principalId, roleDefinitionId, key.TryGet("directoryScopeId", out var scope) ? scope : RoleAssignmentsRules.RootScope, IsAppScope: false);
    }

    // ---- orphan report ----

    /// <summary>The orphan report lists the Direct eligibilities of the principals the declared resources name (reported as retained).</summary>
    public override OrphanScope OrphanScope => OrphanScope.DeclaredPrincipals;

    public override async Task<IReadOnlyList<OrphanCandidate>> ListOrphanCandidatesAsync(OrphanQuery query, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(graph);
        var candidates = new List<OrphanCandidate>();
        foreach (var principalId in OrphanListing.DistinctKeyValues(query.DeclaredKeys, "principalId"))
        {
            foreach (var schedule in await RoleEligibilitySchedulesLookup.ReadPrincipalSchedulesAsync(graph, principalId, query.Api, RetryHint.Default, cancellationToken).ConfigureAwait(false))
            {
                if (JsonObjects.GetId(schedule) is { } id)
                {
                    string scopeProperty = JsonObjects.GetString(schedule, "directoryScopeId") is not null ? "directoryScopeId" : "appScopeId";
                    candidates.Add(new OrphanCandidate(id, id, null, "eligible: " + OrphanListing.Describe(schedule, "principalId", "roleDefinitionId", scopeProperty)));
                }
            }
        }

        return candidates;
    }
}
