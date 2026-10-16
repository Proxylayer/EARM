using System.Collections.Generic;
using System.Text.Json.Nodes;
using EArm.Core.Types;
using EArm.Core.Validation;
using EArm.Engine;
using EArm.Engine.Execution;
using EArm.Engine.Providers;

namespace EArm.Providers;

/// <summary>
/// <c>Microsoft.Entra/roleAssignmentScheduleRequests</c>: a time-bound ACTIVE directory role assignment through PIM, shaped like ARM's
/// <c>Microsoft.Authorization/roleAssignmentScheduleRequests</c>. Same shape, sugar and identity as <c>roleEligibilityScheduleRequests</c>
/// (<see cref="RoleScheduleRequestsProvider"/>): the guid() name is a label, the assignment is found by (<c>principalId</c>, <c>roleDefinitionId</c>,
/// <c>directoryScopeId</c> or <c>appScopeId</c>). The pure rules are <see cref="RoleAssignmentScheduleRules"/>: the expiration must be time-bound
/// (AfterDateTime or AfterDuration); a permanent active assignment is <c>Microsoft.Entra/roleAssignments</c>.
/// <para>
/// Requests: <c>GET /roleManagement/directory/roleAssignmentSchedules?$filter=…</c>, keeping <c>memberType</c> Direct and <c>assignmentType</c> Assigned
/// (an <c>Activated</c> schedule is a principal's activation of an eligibility, never the template's); <c>POST /roleManagement/directory/roleAssignmentScheduleRequests</c>
/// with action adminAssign / adminUpdate / adminRemove.
/// </para>
/// <para>
/// The schedule list also carries the permanent assignments of the roleAssignments API (Learn: unifiedRoleAssignmentSchedule; they are Assigned and
/// never expire, and Graph does not mark their origin reliably). A permanent schedule under the declared tuple is therefore not this type's: it is refused
/// at plan time (<see cref="EngineDiagnosticCodes.PimAssignmentConflict"/>), when a create would adopt it, and on delete (kept, never removed), and the
/// orphan report lists only time-bound schedules (the permanent ones are reported by <c>roleAssignments</c>). The same tuple declared by a
/// <c>roleAssignments</c> resource or a <c>directoryRoles</c> entry of the template is refused before planning (<see cref="DeclarationConflicts"/>).
/// </para>
/// </summary>
[EntraProvider(ResourceType)]
public sealed class RoleAssignmentScheduleRequestsProvider : RoleScheduleRequestsProvider, IDeclarationConflictSource
{
    public const string ResourceType = RoleAssignmentScheduleRules.ResourceType;

    /// <summary><see cref="ProviderException"/> code: the declared active assignment cannot be requested (unknown role or unit, wrong principal kind, bad shape).</summary>
    public const string InvalidAssignment = EngineDiagnosticCodes.RoleAssignmentScheduleInvalid;

    public RoleAssignmentScheduleRequestsProvider(ResourceTypeDefinition definition, ResourceTypeCatalog? catalog = null)
        : this(definition, catalog, null)
    {
    }

    /// <param name="definition">The catalog definition of the type.</param>
    /// <param name="catalog">The catalog.</param>
    /// <param name="settle">How long an adminUpdate answered RoleAssignmentExists is repeated; <see cref="ReplicationWait.Default"/> when null.</param>
    public RoleAssignmentScheduleRequestsProvider(ResourceTypeDefinition definition, ResourceTypeCatalog? catalog, ReplicationWait? settle)
        : base(definition, catalog, ResourceType, settle)
    {
    }

    public RoleAssignmentScheduleRequestsProvider(ResourceTypeCatalog catalog)
        : this(RequireDefinition(catalog, ResourceType), catalog)
    {
    }

    protected override string RequestsPath => RoleSchedulesLookup.AssignmentRequestsPath;

    protected override string SchedulesPath => RoleSchedulesLookup.AssignmentSchedulesPath;

    protected override string OrphanLabel => "active";

    protected override string ScheduleNoun => "active assignment";

    protected override string InvalidCode => InvalidAssignment;

    protected override string PolicyOwner => "the role's PIM policy";

    protected override string PolicyAdvice => "A role's PIM policy limits active assignments (Expiration_Admin_Assignment: the maximum duration; Enablement_Admin_Assignment: justification or MFA): shorten scheduleInfo, satisfy any justification or ticketing rule with justification / ticketInfo, or change the policy.";

    protected override string NameExample => "[guid(parameters('principalId'), 'Exchange Administrator', 'active')]";

    protected override IReadOnlyList<PimRequestFinding> Check(JsonObject properties)
    {
        return RoleAssignmentScheduleRules.Check(properties, IsOpaque);
    }

    protected override void NormalizeRules(JsonObject properties)
    {
        PimScheduleRules.Normalize(properties, IsOpaque);
    }

    /// <summary>Only schedules an administrator assigned: an activation of an eligibility (<c>assignmentType</c> Activated) is the principal's.</summary>
    protected override bool Keep(JsonObject schedule)
    {
        return PimScheduleRequests.IsAssigned(schedule);
    }

    /// <summary>The permanent assignments are <c>roleAssignments</c>' (which reports them itself); only time-bound ones are listed here.</summary>
    protected override bool ListsAsOrphan(JsonObject schedule)
    {
        return !PimScheduleComparison.IsPermanent(schedule);
    }

    protected override string? LiveConflict(JsonObject live)
    {
        return PimScheduleComparison.IsPermanent(live)
            ? $"a permanent active assignment of this principal, role and scope already exists ({JsonObjects.GetId(live)}): it was made with Microsoft.Entra/roleAssignments (or a principal's directoryRoles), in the portal or by another tool, and {ResourceType} manages only time-bound assignments. Declare it as Microsoft.Entra/roleAssignments, or remove it first; EARM does not turn it into a time-bound assignment or remove it."
            : null;
    }

    public IEnumerable<DeclarationConflict> DescribeConflicts(ResourceInstance instance, IReadOnlyList<ResourceInstance> declared)
    {
        return DeclarationConflicts.RoleAssignment(instance, declared);
    }
}
