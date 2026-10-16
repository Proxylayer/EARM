using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace EArm.Core.Validation;

/// <summary>
/// The pure rules of <c>Microsoft.Entra/roleAssignmentScheduleRequests</c> (a time-bound active directory role assignment through PIM, shaped like ARM's
/// <c>Microsoft.Authorization/roleAssignmentScheduleRequests</c>), shared by the offline validator (literal values) and the provider (evaluated values):
/// the property set, principal, role and scope rules of <see cref="RoleEligibilityScheduleRules"/>, the shared request and schedule rules of
/// <see cref="PimScheduleRules"/>, and the type's own rule: the schedule must be time-bound (<see cref="PimScheduleRules.CheckTimeBound"/>), since a
/// permanent active assignment is <c>Microsoft.Entra/roleAssignments</c>.
/// </summary>
public static class RoleAssignmentScheduleRules
{
    public const string ResourceType = "Microsoft.Entra/roleAssignmentScheduleRequests";

    /// <summary>The type that holds permanent active directory role assignments.</summary>
    public const string PermanentAlternative = "Microsoft.Entra/roleAssignments";

    /// <summary>The problems of a declaration. Nothing is changed.</summary>
    /// <param name="properties">The declared (offline) or evaluated (plan time) properties.</param>
    /// <param name="isOpaque">True for a value that cannot be judged yet (a template expression, a deploy-time value, a secret).</param>
    public static IReadOnlyList<PimRequestFinding> Check(JsonObject properties, Func<JsonNode?, bool> isOpaque)
    {
        var findings = RoleEligibilityScheduleRules.CheckRoleRequest(properties, isOpaque, "role assignment schedule request", "a PIM active assignment of a directory role", "assign the role to");
        PimScheduleRules.CheckTimeBound(properties, isOpaque, findings, PermanentAlternative);
        return findings;
    }
}
