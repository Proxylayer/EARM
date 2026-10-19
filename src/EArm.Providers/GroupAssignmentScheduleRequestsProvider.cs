using System.Collections.Generic;
using System.Text.Json.Nodes;
using EArm.Core.Types;
using EArm.Core.Validation;
using EArm.Engine;
using EArm.Engine.Execution;
using EArm.Engine.Planning;
using EArm.Engine.Providers;

namespace EArm.Providers;

/// <summary>
/// <c>Microsoft.Entra/privilegedAccess/groupAssignmentScheduleRequests</c>: a time-bound ACTIVE membership or ownership (<c>accessId</c>) of a group
/// through PIM for Groups. Same shape, sugar and identity as <c>privilegedAccess/groupEligibilityScheduleRequests</c>
/// (<see cref="GroupScheduleRequestsProvider"/>). The pure rules are <see cref="GroupAssignmentScheduleRules"/>: the expiration must be time-bound; a
/// permanent membership or ownership is the group's <c>members</c> / <c>owners</c>.
/// <para>
/// Requests: <c>GET /identityGovernance/privilegedAccess/group/assignmentSchedules?$filter=groupId eq … and principalId eq …</c> (the filter is required),
/// keeping <c>memberType</c> Direct and <c>assignmentType</c> assigned (an activation of an eligibility is the principal's);
/// <c>POST /identityGovernance/privilegedAccess/group/assignmentScheduleRequests</c> with action adminAssign / adminUpdate / adminRemove.
/// </para>
/// <para>
/// An active assignment is a real membership or ownership: it appears in <c>/groups/{id}/members</c> (or <c>/owners</c>). So it must never meet a pruning
/// list: when the target group of the template declares <c>members</c> (for accessId member) or <c>owners</c> (for owner), the edge pass would remove the
/// principal, and a principal's <c>memberOf</c> / <c>ownerOf</c> entry for the group would re-add it permanently when the schedule ends. Both are refused
/// before planning (<see cref="DeclarationConflicts"/>). A permanent schedule under the tuple (a membership added through the members API or the
/// group's <c>members</c>) is not this type's: refused at plan time, on adoption and on delete, and left out of the orphan report.
/// </para>
/// </summary>
[EntraProvider(ResourceType)]
public sealed class GroupAssignmentScheduleRequestsProvider : GroupScheduleRequestsProvider, IDeclarationConflictSource
{
    public const string ResourceType = GroupAssignmentScheduleRules.ResourceType;

    /// <summary><see cref="ProviderException"/> code: the declared active assignment cannot be requested (unknown or unsupported group, wrong principal kind, bad shape).</summary>
    public const string InvalidAssignment = EngineDiagnosticCodes.GroupAssignmentScheduleInvalid;

    public GroupAssignmentScheduleRequestsProvider(ResourceTypeDefinition definition, ResourceTypeCatalog? catalog = null)
        : this(definition, catalog, null)
    {
    }

    /// <param name="definition">The catalog definition of the type.</param>
    /// <param name="catalog">The catalog.</param>
    /// <param name="settle">How long an adminUpdate answered RoleAssignmentExists is repeated; <see cref="ReplicationWait.Default"/> when null.</param>
    public GroupAssignmentScheduleRequestsProvider(ResourceTypeDefinition definition, ResourceTypeCatalog? catalog, ReplicationWait? settle)
        : base(definition, catalog, ResourceType, settle)
    {
    }

    public GroupAssignmentScheduleRequestsProvider(ResourceTypeCatalog catalog)
        : this(RequireDefinition(catalog, ResourceType), catalog)
    {
    }

    protected override string RequestsPath => GroupSchedulesLookup.AssignmentRequestsPath;

    protected override string SchedulesPath => GroupSchedulesLookup.AssignmentSchedulesPath;

    protected override string OrphanLabel => "active";

    protected override string ScheduleNoun => "active assignment";

    protected override string InvalidCode => InvalidAssignment;

    protected override string PolicyOwner => "the group's PIM policy";

    protected override string PolicyAdvice => "A group's PIM policy limits active assignments (Expiration_Admin_Assignment: the maximum duration; Enablement_Admin_Assignment: justification or MFA): shorten scheduleInfo, satisfy any justification or ticketing rule with justification / ticketInfo, or change the group's policy (Microsoft.Entra/roleManagementPolicies).";

    protected override string NameExample => "[guid('ops-admins', variables('principalId'), 'member', 'active')]";

    protected override IReadOnlyList<PimRequestFinding> Check(JsonObject properties)
    {
        return GroupAssignmentScheduleRules.Check(properties, IsOpaque);
    }

    /// <summary>Only schedules an administrator assigned: an activation of an eligibility (<c>assignmentType</c> activated) is the principal's.</summary>
    protected override bool Keep(JsonObject schedule)
    {
        return PimScheduleRequests.IsAssigned(schedule);
    }

    /// <summary>The permanent memberships are the group's <c>members</c> / <c>owners</c>; only time-bound ones are listed here.</summary>
    protected override bool ListsAsOrphan(JsonObject schedule)
    {
        return !PimScheduleComparison.IsPermanent(schedule);
    }

    protected override string? LiveConflict(JsonObject live)
    {
        string access = JsonObjects.GetString(live, "accessId") ?? "member";
        string list = access.Equals(GroupEligibilityScheduleRules.Owner, System.StringComparison.OrdinalIgnoreCase) ? "owners" : "members";
        return PimScheduleComparison.IsPermanent(live)
            ? $"the principal is already a permanent {access} of the group ({JsonObjects.GetId(live)}): it was added through the group's {list} (or the portal, or another tool), and {ResourceType} manages only time-bound assignments. Keep it in the group's {list}, or remove it first; EARM does not turn it into a time-bound assignment or remove it."
            : null;
    }

    public IEnumerable<DeclarationConflict> DescribeConflicts(ResourceInstance instance, IReadOnlyList<ResourceInstance> declared, DeploymentMode mode)
    {
        return DeclarationConflicts.GroupAssignment(instance, declared, mode);
    }
}
