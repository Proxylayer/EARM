using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Core.Json;

namespace EArm.Core.Validation;

/// <summary>
/// The pure rules of <c>Microsoft.Entra/azureRoleEligibilityScheduleRequests</c> (a PIM eligibility for an Azure RBAC role at an Azure scope, requested
/// through Azure Resource Manager as <c>Microsoft.Authorization/roleEligibilityScheduleRequests</c>), shared by the offline validator (literal values) and the
/// provider (evaluated values). Rules checked here: the property set, the scope (<see cref="AzureScopeRules.CheckScope"/>), one of <c>role</c> /
/// <c>roleDefinitionId</c> (<see cref="AzureScopeRules.CheckRole"/>), a GUID <c>principalId</c>, and the <c>justification</c> and <c>scheduleInfo</c> shape
/// of the Entra PIM requests (<see cref="PimScheduleRules.CheckRequest"/>: an expiration type of NoExpiration, AfterDateTime with endDateTime, or
/// AfterDuration with duration).
/// </summary>
public static class AzureRoleEligibilityScheduleRules
{
    public const string ResourceType = "Microsoft.Entra/azureRoleEligibilityScheduleRequests";

    /// <summary>The property names an eligibility declaration accepts (ARM names and EARM sugar).</summary>
    public static IReadOnlyList<string> AllowedProperties { get; } = ["scope", "roleDefinitionId", "role", "principalId", "justification", "scheduleInfo"];

    /// <summary>The problems of a declaration. Nothing is changed.</summary>
    /// <param name="properties">The declared (offline) or evaluated (plan time) properties.</param>
    /// <param name="isOpaque">True for a value that cannot be judged yet (a template expression, a deploy-time value, a secret).</param>
    public static IReadOnlyList<PimRequestFinding> Check(JsonObject properties, Func<JsonNode?, bool> isOpaque)
    {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(isOpaque);
        var findings = new List<PimRequestFinding>();
        foreach (var property in properties)
        {
            if (!AllowedProperties.Contains(property.Key, StringComparer.OrdinalIgnoreCase))
            {
                findings.Add(new(JsonPath.Root.Append(property.Key), $"properties.{property.Key} is not a property of an Azure role eligibility; expected scope, role or roleDefinitionId, principalId, justification and scheduleInfo (ARM's requestType, condition and target*ScheduleId are chosen or refused by EARM)"));
            }
        }

        AzureScopeRules.CheckScope(properties, isOpaque, findings, "the eligibility");
        AzureScopeRules.CheckRole(properties, isOpaque, findings);
        AzureScopeRules.CheckPrincipal(properties, isOpaque, findings);
        PimScheduleRules.CheckRequest(properties, isOpaque, findings);
        return findings;
    }
}
