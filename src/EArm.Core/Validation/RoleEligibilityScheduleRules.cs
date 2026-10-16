using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using EArm.Core.Json;

namespace EArm.Core.Validation;

/// <summary>A problem with a declared role eligibility schedule request.</summary>
/// <param name="Path">Where the problem is, relative to <c>properties</c>.</param>
/// <param name="Message">What is wrong.</param>
/// <param name="SchemaCovered">True for a shape problem the type's JSON Schema also reports offline (a type, a GUID or scope pattern); the offline checker
/// leaves those to the schema and reports only the rules the schema cannot express.</param>
public sealed record RoleEligibilityFinding(JsonPath Path, string Message, bool SchemaCovered = false);

/// <summary>
/// The pure rules of <c>Microsoft.Entra/roleEligibilityScheduleRequests</c> (PIM eligibility for directory roles, shaped like ARM's
/// <c>Microsoft.Authorization/roleEligibilityScheduleRequests</c>), shared by the offline validator (literal values) and the provider (evaluated values):
/// the property set (ARM names plus the EARM sugar <c>role</c> / <c>administrativeUnit</c>, the ARM-only <c>condition</c>, <c>conditionVersion</c> and
/// <c>target*ScheduleId</c> refused), the exclusive pairs (role / roleDefinitionId; directoryScopeId / administrativeUnit / appScopeId), <c>requestType</c>
/// (only <c>AdminAssign</c>), and the schedule (expiration type, the endDateTime / duration each type needs, ISO 8601 values, end after start). Values the
/// caller cannot judge yet (a template expression offline, a deploy-time value at plan time) are skipped through the <c>isOpaque</c> predicate.
/// </summary>
public static class RoleEligibilityScheduleRules
{
    public const string ResourceType = "Microsoft.Entra/roleEligibilityScheduleRequests";

    /// <summary>The justification sent when the template declares none (PIM policy may require one; it is request metadata, never compared).</summary>
    public const string DefaultJustification = "Deployed by earm";

    public const string NoExpiration = "noExpiration";

    public const string AfterDateTime = "afterDateTime";

    public const string AfterDuration = "afterDuration";

    /// <summary>The one ARM <c>requestType</c> accepted (the engine chooses the Graph action itself).</summary>
    public const string AdminAssign = "AdminAssign";

    /// <summary>The property names a request accepts (ARM names and EARM sugar).</summary>
    public static IReadOnlyList<string> AllowedProperties { get; } =
        ["principalId", "roleDefinitionId", "role", "directoryScopeId", "administrativeUnit", "appScopeId", "requestType", "scheduleInfo", "justification", "ticketInfo"];

    /// <summary>Request metadata: sent with every request, never compared with the live schedule.</summary>
    public static IReadOnlyList<string> MetadataProperties { get; } = ["justification", "ticketInfo"];

    private static readonly string[] ExpirationTypes = [NoExpiration, AfterDateTime, AfterDuration];

    private static readonly string[] ScopeProperties = ["directoryScopeId", "administrativeUnit", "appScopeId"];

    private static readonly Regex ArmOnlyPattern = new("^(condition|conditionVersion|target\\w*ScheduleId|target\\w*ScheduleInstanceId|linked\\w*ScheduleId)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly Regex DatePrefix = new("^\\d{4}-\\d{2}-\\d{2}", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>True for an ARM property of the analogue type that has no meaning for directory roles (<c>condition</c>, <c>conditionVersion</c>, <c>target*ScheduleId</c>).</summary>
    public static bool IsArmOnlyProperty(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return ArmOnlyPattern.IsMatch(name);
    }

    /// <summary>The Graph (camelCase) expiration type for any casing of NoExpiration / AfterDateTime / AfterDuration, or <c>null</c>.</summary>
    public static string? NormalizeExpirationType(string? type)
    {
        return ExpirationTypes.FirstOrDefault(t => t.Equals(type?.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A positive ISO 8601 duration (<c>P365D</c>, <c>PT8H</c>, <c>P1DT12H</c>).</summary>
    public static bool TryParseDuration(string? text, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text) || !text.TrimStart().StartsWith('P'))
        {
            return false;
        }

        try
        {
            duration = XmlConvert.ToTimeSpan(text.Trim());
            return duration > TimeSpan.Zero;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    /// <summary>An ISO 8601 date-time (<c>2026-01-31T00:00:00Z</c>); a value without an offset is taken as UTC.</summary>
    public static bool TryParseDateTime(string? text, out DateTimeOffset value)
    {
        value = default;
        return text is not null
            && DatePrefix.IsMatch(text.Trim())
            && DateTimeOffset.TryParse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value);
    }

    /// <summary>The problems of a declaration. Nothing is changed.</summary>
    /// <param name="properties">The declared (offline) or evaluated (plan time) properties.</param>
    /// <param name="isOpaque">True for a value that cannot be judged yet (a template expression, a deploy-time value, a secret).</param>
    public static IReadOnlyList<RoleEligibilityFinding> Check(JsonObject properties, Func<JsonNode?, bool> isOpaque)
    {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(isOpaque);
        var findings = new List<RoleEligibilityFinding>();
        var root = JsonPath.Root;

        foreach (var property in properties)
        {
            if (IsArmOnlyProperty(property.Key))
            {
                findings.Add(new(root.Append(property.Key), $"properties.{property.Key} is an ARM property that PIM eligibility for directory roles does not support; remove it"));
            }
            else if (!AllowedProperties.Contains(property.Key, StringComparer.OrdinalIgnoreCase))
            {
                findings.Add(new(root.Append(property.Key), $"properties.{property.Key} is not a property of a role eligibility schedule request; expected principalId, roleDefinitionId or role, directoryScopeId or administrativeUnit or appScopeId, scheduleInfo, justification, ticketInfo and requestType"));
            }
        }

        CheckPrincipal(properties, isOpaque, findings);
        CheckRole(properties, isOpaque, findings);
        CheckScope(properties, isOpaque, findings);

        if (Judged(properties, "requestType", isOpaque) is (string requestKey, var requestType)
            && (!TryString(requestType, out string? request) || !request.Trim().Equals(AdminAssign, StringComparison.OrdinalIgnoreCase)))
        {
            findings.Add(new(root.Append(requestKey), $"properties.{requestKey} must be AdminAssign (or be omitted): EARM chooses adminAssign, adminUpdate or adminRemove itself"));
        }

        if (Judged(properties, "justification", isOpaque) is (string justificationKey, var justification) && !TryString(justification, out _))
        {
            findings.Add(new(root.Append(justificationKey), $"properties.{justificationKey} must be a string", SchemaCovered: true));
        }

        if (Judged(properties, "ticketInfo", isOpaque) is (string ticketKey, var ticket) && ticket is not JsonObject)
        {
            findings.Add(new(root.Append(ticketKey), $"properties.{ticketKey} must be an object ({{ ticketNumber, ticketSystem }})", SchemaCovered: true));
        }

        if (Judged(properties, "scheduleInfo", isOpaque) is (string scheduleKey, var schedule))
        {
            CheckSchedule(schedule, root.Append(scheduleKey), "properties." + scheduleKey, isOpaque, findings);
        }

        return findings;
    }

    /// <summary>
    /// Normalises evaluated properties in place before they are diffed and sent: the expiration type in Graph's camelCase, <c>requestType</c> removed (the
    /// engine chooses the action). Opaque values are left alone. Run <see cref="Check"/> first.
    /// </summary>
    public static void Normalize(JsonObject properties, Func<JsonNode?, bool> isOpaque)
    {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(isOpaque);
        if (Property(properties, "requestType") is (string requestKey, var request) && !isOpaque(request))
        {
            properties.Remove(requestKey);
        }

        if (Property(properties, "scheduleInfo") is (_, JsonObject schedule)
            && Property(schedule, "expiration") is (_, JsonObject expiration)
            && Property(expiration, "type") is (string typeKey, var type)
            && !isOpaque(type) && TryString(type, out string? text) && NormalizeExpirationType(text) is { } normalized)
        {
            expiration[typeKey] = normalized;
        }
    }

    // ---- members ----

    private static void CheckPrincipal(JsonObject properties, Func<JsonNode?, bool> isOpaque, List<RoleEligibilityFinding> findings)
    {
        var principal = Property(properties, "principalId");
        if (principal is null || IsAbsent(principal.Value.Value))
        {
            findings.Add(new(JsonPath.Root.Append(principal?.Key ?? "principalId"), "properties.principalId is required: the object id of the user, role-assignable group or service principal to make eligible"));
            return;
        }

        CheckGuid(properties, principal.Value.Key, isOpaque, findings);
    }

    private static void CheckRole(JsonObject properties, Func<JsonNode?, bool> isOpaque, List<RoleEligibilityFinding> findings)
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

        if (hasRole && Judged(properties, role!.Value.Key, isOpaque) is (string roleKey, var value) && (!TryString(value, out string? text) || text.Trim().Length == 0))
        {
            findings.Add(new(JsonPath.Root.Append(roleKey), $"properties.{roleKey} must be a non-empty string", SchemaCovered: true));
        }
    }

    private static void CheckScope(JsonObject properties, Func<JsonNode?, bool> isOpaque, List<RoleEligibilityFinding> findings)
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

    private static void CheckSchedule(JsonNode? node, JsonPath path, string where, Func<JsonNode?, bool> isOpaque, List<RoleEligibilityFinding> findings)
    {
        if (node is not JsonObject schedule)
        {
            findings.Add(new(path, $"{where} must be an object ({{ startDateTime, expiration }})", SchemaCovered: true));
            return;
        }

        DateTimeOffset? start = null;
        if (Judged(schedule, "startDateTime", isOpaque) is (string startKey, var startNode))
        {
            if (TryString(startNode, out string? text) && TryParseDateTime(text, out var parsed))
            {
                start = parsed;
            }
            else
            {
                findings.Add(new(path.Append(startKey), $"{where}.{startKey} must be an ISO 8601 date-time, for example 2026-01-01T00:00:00Z"));
            }
        }

        if (Judged(schedule, "expiration", isOpaque) is not (string expirationKey, var expirationNode))
        {
            return;
        }

        var expirationPath = path.Append(expirationKey);
        string expirationWhere = $"{where}.{expirationKey}";
        if (expirationNode is not JsonObject expiration)
        {
            findings.Add(new(expirationPath, $"{expirationWhere} must be an object ({{ type, endDateTime, duration }})", SchemaCovered: true));
            return;
        }

        string? type = null;
        var typeProperty = Property(expiration, "type");
        if (typeProperty is null || IsAbsent(typeProperty.Value.Value))
        {
            findings.Add(new(expirationPath.Append("type"), $"{expirationWhere}.type is required: NoExpiration, AfterDateTime or AfterDuration"));
        }
        else if (!isOpaque(typeProperty.Value.Value))
        {
            type = TryString(typeProperty.Value.Value, out string? text) ? NormalizeExpirationType(text) : null;
            if (type is null)
            {
                findings.Add(new(expirationPath.Append(typeProperty.Value.Key), $"{expirationWhere}.{typeProperty.Value.Key} must be one of NoExpiration, AfterDateTime, AfterDuration"));
            }
        }

        var end = Property(expiration, "endDateTime");
        var duration = Property(expiration, "duration");
        bool hasEnd = end is not null && !IsAbsent(end.Value.Value);
        bool hasDuration = duration is not null && !IsAbsent(duration.Value.Value);

        DateTimeOffset? endValue = null;
        if (hasEnd && !isOpaque(end!.Value.Value))
        {
            if (TryString(end.Value.Value, out string? text) && TryParseDateTime(text, out var parsed))
            {
                endValue = parsed;
            }
            else
            {
                findings.Add(new(expirationPath.Append(end.Value.Key), $"{expirationWhere}.{end.Value.Key} must be an ISO 8601 date-time, for example 2027-01-01T00:00:00Z"));
            }
        }

        if (hasDuration && !isOpaque(duration!.Value.Value) && !(TryString(duration.Value.Value, out string? durationText) && TryParseDuration(durationText, out _)))
        {
            findings.Add(new(expirationPath.Append(duration.Value.Key), $"{expirationWhere}.{duration.Value.Key} must be a positive ISO 8601 duration, for example P365D or PT8H"));
        }

        switch (type)
        {
            case NoExpiration:
                if (hasEnd)
                {
                    findings.Add(new(expirationPath.Append(end!.Value.Key), $"{expirationWhere}.{end.Value.Key} is not allowed with type NoExpiration"));
                }

                if (hasDuration)
                {
                    findings.Add(new(expirationPath.Append(duration!.Value.Key), $"{expirationWhere}.{duration.Value.Key} is not allowed with type NoExpiration"));
                }

                break;
            case AfterDateTime:
                if (!hasEnd)
                {
                    findings.Add(new(expirationPath.Append("endDateTime"), $"{expirationWhere}.endDateTime is required with type AfterDateTime"));
                }

                if (hasDuration)
                {
                    findings.Add(new(expirationPath.Append(duration!.Value.Key), $"{expirationWhere}.{duration.Value.Key} is not allowed with type AfterDateTime (declare endDateTime only)"));
                }

                break;
            case AfterDuration:
                if (!hasDuration)
                {
                    findings.Add(new(expirationPath.Append("duration"), $"{expirationWhere}.duration is required with type AfterDuration"));
                }

                if (hasEnd)
                {
                    findings.Add(new(expirationPath.Append(end!.Value.Key), $"{expirationWhere}.{end.Value.Key} is not allowed with type AfterDuration (declare duration only)"));
                }

                break;
        }

        if (start is { } from && endValue is { } to && to <= from)
        {
            findings.Add(new(expirationPath.Append(end!.Value.Key), string.Create(CultureInfo.InvariantCulture, $"{expirationWhere}.{end.Value.Key} ({to:O}) must be after {where}.startDateTime ({from:O})")));
        }
    }

    private static void CheckGuid(JsonObject properties, string key, Func<JsonNode?, bool> isOpaque, List<RoleEligibilityFinding> findings)
    {
        if (Judged(properties, key, isOpaque) is (_, var value) && !(TryString(value, out string? text) && Guid.TryParse(text, out _)))
        {
            string shown = TryString(value, out string? raw) ? raw : value?.ToJsonString() ?? "null";
            findings.Add(new(JsonPath.Root.Append(key), $"properties.{key} '{shown}' is not a GUID", SchemaCovered: true));
        }
    }

    // ---- JSON helpers (Core has no JsonObjects) ----

    private static (string Key, JsonNode? Value)? Property(JsonObject obj, string name)
    {
        foreach (var pair in obj)
        {
            if (pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return (pair.Key, pair.Value);
            }
        }

        return null;
    }

    private static (string Key, JsonNode? Value)? Judged(JsonObject obj, string name, Func<JsonNode?, bool> isOpaque)
    {
        return Property(obj, name) is (string key, var value) && !IsAbsent(value) && !isOpaque(value) ? (key, value) : null;
    }

    private static bool IsAbsent(JsonNode? node)
    {
        return node is null || (node is JsonValue value && value.GetValueKind() == JsonValueKind.Null);
    }

    private static bool TryString(JsonNode? node, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? text)
    {
        text = null;
        if (node is JsonValue value && value.GetValueKind() == JsonValueKind.String && value.TryGetValue(out string? s))
        {
            text = s;
            return true;
        }

        return false;
    }
}
