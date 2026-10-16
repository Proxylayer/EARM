using System.Collections.Generic;
using System.Text.Json.Nodes;
using EArm.Core.Types;
using EArm.Core.Validation;
using EArm.Engine;
using EArm.Engine.Providers;

namespace EArm.Providers;

/// <summary>
/// <c>Microsoft.Entra/privilegedAccess/groupEligibilityScheduleRequests</c>: PIM for Groups eligibility, a principal (a user or a group) made eligible
/// for the membership or the ownership (<c>accessId</c>) of a group. No ARM analogue; the shape follows Graph's
/// <c>privilegedAccessGroupEligibilityScheduleRequest</c> and the schedule / metadata rules of <c>roleEligibilityScheduleRequests</c>. The resource
/// name is a deterministic GUID (a label); the eligibility is found by (<c>groupId</c>, <c>principalId</c>, <c>accessId</c>) and read live on every run.
/// The pure rules are <see cref="GroupEligibilityScheduleRules"/>; the sugar, Prepare, the tuple and the orphan report are
/// <see cref="GroupScheduleRequestsProvider"/>; the lifecycle is <see cref="PimScheduleRequestsProvider{TTuple}"/>.
/// <para>
/// Requests: <c>GET /identityGovernance/privilegedAccess/group/eligibilitySchedules?$filter=groupId eq … and principalId eq …</c> and
/// <c>POST /identityGovernance/privilegedAccess/group/eligibilityScheduleRequests</c> with action adminAssign / adminUpdate / adminRemove. A group's default
/// PIM policy can forbid permanent eligibility: the <see cref="PimScheduleRequestsProvider{TTuple}.PimPolicyViolation"/> message points at an expiration or
/// the group's policy. Eligibility never touches the group's members or owners, so it does not conflict with a pruning <c>members</c> / <c>owners</c> list.
/// </para>
/// </summary>
[EntraProvider(ResourceType)]
public sealed class GroupEligibilityScheduleRequestsProvider : GroupScheduleRequestsProvider
{
    public const string ResourceType = GroupEligibilityScheduleRules.ResourceType;

    /// <summary><see cref="ProviderException"/> code: the declared eligibility cannot be requested (unknown or unsupported group, wrong principal kind, bad shape).</summary>
    public const string InvalidEligibility = EngineDiagnosticCodes.GroupEligibilityInvalid;

    public GroupEligibilityScheduleRequestsProvider(ResourceTypeDefinition definition, ResourceTypeCatalog? catalog = null)
        : base(definition, catalog, ResourceType)
    {
    }

    public GroupEligibilityScheduleRequestsProvider(ResourceTypeCatalog catalog)
        : this(RequireDefinition(catalog, ResourceType), catalog)
    {
    }

    protected override string RequestsPath => GroupSchedulesLookup.EligibilityRequestsPath;

    protected override string SchedulesPath => GroupSchedulesLookup.EligibilitySchedulesPath;

    protected override string OrphanLabel => "eligible";

    protected override string InvalidCode => InvalidEligibility;

    protected override string PolicyOwner => "the group's PIM policy";

    protected override string PolicyAdvice => "A group's PIM policy often forbids permanent eligibility (its Expiration_Admin_Eligibility rule): declare scheduleInfo with an AfterDateTime or AfterDuration expiration, or allow permanent eligibility in the group's policy (Microsoft.Entra/roleManagementPolicies); satisfy any justification or ticketing rule with justification / ticketInfo.";

    protected override string NameExample => "[guid('ops-admins', variables('principalId'), 'member')]";

    protected override IReadOnlyList<PimRequestFinding> Check(JsonObject properties)
    {
        return GroupEligibilityScheduleRules.Check(properties, IsOpaque);
    }
}
