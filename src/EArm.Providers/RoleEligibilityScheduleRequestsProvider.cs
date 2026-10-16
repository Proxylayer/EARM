using System.Collections.Generic;
using System.Text.Json.Nodes;
using EArm.Core.Types;
using EArm.Core.Validation;
using EArm.Engine;
using EArm.Engine.Execution;
using EArm.Engine.Providers;

namespace EArm.Providers;

/// <summary>
/// <c>Microsoft.Entra/roleEligibilityScheduleRequests</c>: PIM eligibility for directory roles, shaped like ARM's
/// <c>Microsoft.Authorization/roleEligibilityScheduleRequests</c>. The resource name is a deterministic GUID (a label); the eligibility is found by the
/// tuple (<c>principalId</c>, <c>roleDefinitionId</c>, <c>directoryScopeId</c> or <c>appScopeId</c>) and read live on every run; an absent
/// <c>scheduleInfo</c> is a permanent eligibility. The pure rules are <see cref="RoleEligibilityScheduleRules"/>; the sugar, Prepare, the tuple and the
/// orphan report are <see cref="RoleScheduleRequestsProvider"/>; the lifecycle (adminAssign / adminUpdate / adminRemove, comparison,
/// RoleAssignmentExists adoption, PimPolicyViolation) is <see cref="PimScheduleRequestsProvider{TTuple}"/>.
/// <para>
/// Requests: <c>GET /roleManagement/directory/roleEligibilitySchedules?$filter=…</c> (memberType Direct; schedules include future-dated eligibilities)
/// and <c>POST /roleManagement/directory/roleEligibilityScheduleRequests</c> with action adminAssign / adminUpdate / adminRemove.
/// </para>
/// </summary>
[EntraProvider(ResourceType)]
public sealed class RoleEligibilityScheduleRequestsProvider : RoleScheduleRequestsProvider
{
    public const string ResourceType = RoleEligibilityScheduleRules.ResourceType;

    /// <summary><see cref="ProviderException"/> code: the declared eligibility cannot be requested (unknown role or unit, wrong principal kind, bad shape).</summary>
    public const string InvalidEligibility = EngineDiagnosticCodes.RoleEligibilityInvalid;

    public RoleEligibilityScheduleRequestsProvider(ResourceTypeDefinition definition, ResourceTypeCatalog? catalog = null)
        : this(definition, catalog, null)
    {
    }

    /// <param name="definition">The catalog definition of the type.</param>
    /// <param name="catalog">The catalog.</param>
    /// <param name="settle">How long an adminUpdate answered RoleAssignmentExists is repeated; <see cref="ReplicationWait.Default"/> when null.</param>
    public RoleEligibilityScheduleRequestsProvider(ResourceTypeDefinition definition, ResourceTypeCatalog? catalog, ReplicationWait? settle)
        : base(definition, catalog, ResourceType, settle)
    {
    }

    public RoleEligibilityScheduleRequestsProvider(ResourceTypeCatalog catalog)
        : this(RequireDefinition(catalog, ResourceType), catalog)
    {
    }

    protected override string RequestsPath => RoleSchedulesLookup.EligibilityRequestsPath;

    protected override string SchedulesPath => RoleSchedulesLookup.EligibilitySchedulesPath;

    protected override string OrphanLabel => "eligible";

    protected override string InvalidCode => InvalidEligibility;

    protected override string PolicyOwner => "the role's PIM policy";

    protected override string PolicyAdvice => "Change scheduleInfo (for example a policy that forbids permanent eligibility needs an AfterDateTime or AfterDuration expiration), justification or ticketInfo to satisfy the policy, or change the policy.";

    protected override string NameExample => "[guid(variables('groupName'), 'Global Reader', 'eligible')]";

    protected override IReadOnlyList<PimRequestFinding> Check(JsonObject properties)
    {
        return RoleEligibilityScheduleRules.Check(properties, IsOpaque);
    }

    protected override void NormalizeRules(JsonObject properties)
    {
        PimScheduleRules.Normalize(properties, IsOpaque);
    }
}
