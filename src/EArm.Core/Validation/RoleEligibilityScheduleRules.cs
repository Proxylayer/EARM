using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EArm.Core.Json;
using static EArm.Core.Validation.PimScheduleRules;

namespace EArm.Core.Validation;

/// <summary>
/// The pure rules of <c>Microsoft.Entra/roleEligibilityScheduleRequests</c> (PIM eligibility for directory roles, shaped like ARM's
/// <c>Microsoft.Authorization/roleEligibilityScheduleRequests</c>), shared by the offline validator (literal values) and the provider (evaluated values):
/// the property set (ARM names plus the EARM sugar <c>role</c> / <c>administrativeUnit</c>, the ARM-only <c>condition</c>, <c>conditionVersion</c> and
/// <c>target*ScheduleId</c> refused) and the exclusive pairs (role / roleDefinitionId; directoryScopeId / administrativeUnit / appScopeId). The request
/// and schedule rules every PIM type shares are <see cref="PimScheduleRules"/>.
/// </summary>
public static class RoleEligibilityScheduleRules
{
    public const string ResourceType = "Microsoft.Entra/roleEligibilityScheduleRequests";

    /// <summary>The property names a request accepts (ARM names and EARM sugar).</summary>
    public static IReadOnlyList<string> AllowedProperties { get; } =
        ["principalId", "roleDefinitionId", "role", "directoryScopeId", "administrativeUnit", "appScopeId", "requestType", "scheduleInfo", "justification", "ticketInfo"];

    private static readonly string[] ScopeProperties = ["directoryScopeId", "administrativeUnit", "appScopeId"];

    private static readonly Regex ArmOnlyPattern = new("^(condition|conditionVersion|target\\w*ScheduleId|target\\w*ScheduleInstanceId|linked\\w*ScheduleId)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>True for an ARM property of the analogue type that has no meaning for directory roles (<c>condition</c>, <c>conditionVersion</c>, <c>target*ScheduleId</c>).</summary>
    public static bool IsArmOnlyProperty(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return ArmOnlyPattern.IsMatch(name);
    }

    /// <summary>The problems of a declaration. Nothing is changed.</summary>
    /// <param name="properties">The declared (offline) or evaluated (plan time) properties.</param>
    /// <param name="isOpaque">True for a value that cannot be judged yet (a template expression, a deploy-time value, a secret).</param>
    public static IReadOnlyList<PimRequestFinding> Check(JsonObject properties, Func<JsonNode?, bool> isOpaque)
    {
        return CheckRoleRequest(properties, isOpaque, "role eligibility schedule request", "PIM eligibility for directory roles", "make eligible");
    }

    /// <summary>
    /// The rules every directory role schedule request shares (an eligibility, an active assignment): the property set, the principal, the role, the scope
    /// and the shared request rules; <paramref name="requestName"/>, <paramref name="feature"/> and <paramref name="grant"/> word the messages.
    /// </summary>
    internal static List<PimRequestFinding> CheckRoleRequest(JsonObject properties, Func<JsonNode?, bool> isOpaque, string requestName, string feature, string grant)
    {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(isOpaque);
        var findings = new List<PimRequestFinding>();
        var root = JsonPath.Root;

        foreach (var property in properties)
        {
            if (IsArmOnlyProperty(property.Key))
            {
                findings.Add(new(root.Append(property.Key), $"properties.{property.Key} is an ARM property that {feature} does not support; remove it"));
            }
            else if (!AllowedProperties.Contains(property.Key, StringComparer.OrdinalIgnoreCase))
            {
                findings.Add(new(root.Append(property.Key), $"properties.{property.Key} is not a property of a {requestName}; expected principalId, roleDefinitionId or role, directoryScopeId or administrativeUnit or appScopeId, scheduleInfo, justification, ticketInfo and requestType"));
            }
        }

        CheckRequiredGuid(properties, "principalId", $"properties.principalId is required: the object id of the user, role-assignable group or service principal to {grant}", isOpaque, findings);
        CheckRole(properties, isOpaque, findings);
        CheckScope(properties, isOpaque, findings);
        CheckRequest(properties, isOpaque, findings);
        return findings;
    }

    // ---- members ----

    private static void CheckRole(JsonObject properties, Func<JsonNode?, bool> isOpaque, List<PimRequestFinding> findings)
    {
        var definition = Property(properties, "roleDefinitionId");
        var role = Property(properties, "role");
        bool hasDefinition = definition is not null && !IsAbsent(definition.Value.Value);
        bool hasRole = role is not null && !IsAbsent(role.Value.Value);
        if (hasDefinition && hasRole)
        {
            findings.Add(new(JsonPath.Root.Append(role!.Value.Key), "declare one of properties.role and properties.roleDefinitionId, not both"));
        }
        else if (!hasDefinition && !hasRole)
        {
            findings.Add(new(JsonPath.Root.Append("roleDefinitionId"), "the role is required: properties.roleDefinitionId (a role template id, for example roleTemplate('Global Reader')) or properties.role (a template id, a built-in display name, a custom role's display name or id)"));
        }

        if (hasDefinition)
        {
            CheckGuid(properties, definition!.Value.Key, isOpaque, findings);
        }

        if (hasRole)
        {
            CheckNonEmptyString(properties, role!.Value.Key, isOpaque, findings);
        }
    }

    private static void CheckScope(JsonObject properties, Func<JsonNode?, bool> isOpaque, List<PimRequestFinding> findings)
    {
        var declared = ScopeProperties.Select(name => Property(properties, name)).Where(p => p is not null && !IsAbsent(p.Value.Value)).Select(p => p!.Value.Key).ToList();
        if (declared.Count > 1)
        {
            findings.Add(new(JsonPath.Root.Append(declared[1]), $"declare at most one of properties.directoryScopeId, properties.administrativeUnit and properties.appScopeId (found {string.Join(" and ", declared)})"));
        }

        foreach (string name in declared)
        {
            if (Judged(properties, name, isOpaque) is not (string key, var value))
            {
                continue;
            }

            if (!TryString(value, out string? text) || text.Trim().Length == 0)
            {
                findings.Add(new(JsonPath.Root.Append(key), $"properties.{key} must be a non-empty string", SchemaCovered: true));
            }
            else if (key.Equals("directoryScopeId", StringComparison.OrdinalIgnoreCase) && !text.StartsWith('/'))
            {
                findings.Add(new(JsonPath.Root.Append(key), $"properties.{key} '{text}' must start with '/': '/' (tenant), '/administrativeUnits/{{id}}' or '/{{objectId}}'", SchemaCovered: true));
            }
        }
    }
}
