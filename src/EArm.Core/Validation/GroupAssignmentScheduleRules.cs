using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace EArm.Core.Validation;

/// <summary>
/// The pure rules of <c>Microsoft.Entra/privilegedAccess/groupAssignmentScheduleRequests</c> (a time-bound active membership or ownership of a group
/// through PIM for Groups), shared by the offline validator (literal values) and the provider (evaluated values): the property set, group, principal and
/// accessId rules of <see cref="GroupEligibilityScheduleRules"/>, the shared request and schedule rules of <see cref="PimScheduleRules"/>, and the type's
/// own rule: the schedule must be time-bound (<see cref="PimScheduleRules.CheckTimeBound"/>), since a permanent membership or ownership is the group's
/// <c>members</c> / <c>owners</c>.
/// </summary>
public static class GroupAssignmentScheduleRules
{
    public const string ResourceType = "Microsoft.Entra/privilegedAccess/groupAssignmentScheduleRequests";

    /// <summary>Where permanent memberships and ownerships are declared.</summary>
    public const string PermanentAlternative = "the members / owners of the Microsoft.Entra/groups resource";

    /// <summary>The problems of a declaration. Nothing is changed.</summary>
    /// <param name="properties">The declared (offline) or evaluated (plan time) properties.</param>
    /// <param name="isOpaque">True for a value that cannot be judged yet (a template expression, a deploy-time value, a secret).</param>
    public static IReadOnlyList<PimRequestFinding> Check(JsonObject properties, Func<JsonNode?, bool> isOpaque)
    {
        var findings = GroupEligibilityScheduleRules.CheckGroupRequest(properties, isOpaque, "group assignment schedule request", "make an active member or owner", "active membership", "active ownership");
        PimScheduleRules.CheckTimeBound(properties, isOpaque, findings, PermanentAlternative);
        return findings;
    }
}
