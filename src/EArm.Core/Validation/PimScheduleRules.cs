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

/// <summary>A problem with a declared PIM schedule request (a directory role or a group eligibility).</summary>
/// <param name="Path">Where the problem is, relative to <c>properties</c>.</param>
/// <param name="Message">What is wrong.</param>
/// <param name="SchemaCovered">True for a shape problem the type's JSON Schema also reports offline (a type, a GUID or scope pattern); the offline checker
/// leaves those to the schema and reports only the rules the schema cannot express.</param>
public sealed record PimRequestFinding(JsonPath Path, string Message, bool SchemaCovered = false);

/// <summary>
/// The pure rules every PIM schedule request type shares (<c>roleEligibilityScheduleRequests</c>, <c>privilegedAccess/groupEligibilityScheduleRequests</c>):
/// <c>requestType</c> (only <c>AdminAssign</c>; the engine chooses the Graph action), the request metadata (<c>justification</c>, <c>ticketInfo</c>) and the
/// schedule (<c>scheduleInfo</c>: the expiration type, the endDateTime / duration each type needs, ISO 8601 values, end after start), plus the
/// normalisation to Graph's camelCase. Values the caller cannot judge yet are skipped through the <c>isOpaque</c> predicate.
/// </summary>
public static class PimScheduleRules
{
    /// <summary>The justification sent when the template declares none (PIM policy may require one; it is request metadata, never compared).</summary>
    public const string DefaultJustification = "Deployed by earm";

    public const string NoExpiration = "noExpiration";

    public const string AfterDateTime = "afterDateTime";

    public const string AfterDuration = "afterDuration";

    /// <summary>The one ARM <c>requestType</c> accepted (the engine chooses the Graph action itself).</summary>
    public const string AdminAssign = "AdminAssign";

    /// <summary>Request metadata: sent with every request, never compared with the live schedule.</summary>
    public static IReadOnlyList<string> MetadataProperties { get; } = ["justification", "ticketInfo"];

    private static readonly string[] ExpirationTypes = [NoExpiration, AfterDateTime, AfterDuration];

    private static readonly Regex DatePrefix = new("^\\d{4}-\\d{2}-\\d{2}", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

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

    /// <summary>The shared request rules: <c>requestType</c>, <c>justification</c>, <c>ticketInfo</c> and <c>scheduleInfo</c>. Nothing is changed.</summary>
    /// <param name="properties">The declared (offline) or evaluated (plan time) properties.</param>
    /// <param name="isOpaque">True for a value that cannot be judged yet (a template expression, a deploy-time value, a secret).</param>
    /// <param name="findings">Where the problems are added.</param>
    public static void CheckRequest(JsonObject properties, Func<JsonNode?, bool> isOpaque, List<PimRequestFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(isOpaque);
        ArgumentNullException.ThrowIfNull(findings);
        var root = JsonPath.Root;

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
    }

    /// <summary>
    /// Normalises evaluated properties in place before they are diffed and sent: the expiration type in Graph's camelCase, <c>requestType</c> removed (the
    /// engine chooses the action). Opaque values are left alone. Run the type's check first.
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

    /// <summary>Adds a "required" finding when <paramref name="name"/> is absent, else a schema-covered finding when its known value is not a GUID.</summary>
    public static void CheckRequiredGuid(JsonObject properties, string name, string requiredMessage, Func<JsonNode?, bool> isOpaque, List<PimRequestFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(findings);
        var property = Property(properties, name);
        if (property is null || IsAbsent(property.Value.Value))
        {
            findings.Add(new(JsonPath.Root.Append(property?.Key ?? name), requiredMessage));
            return;
        }

        CheckGuid(properties, property.Value.Key, isOpaque, findings);
    }

    /// <summary>Adds a schema-covered finding when the known value of <paramref name="key"/> is not a GUID.</summary>
    public static void CheckGuid(JsonObject properties, string key, Func<JsonNode?, bool> isOpaque, List<PimRequestFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(isOpaque);
        ArgumentNullException.ThrowIfNull(findings);
        if (Judged(properties, key, isOpaque) is (_, var value) && !(TryString(value, out string? text) && Guid.TryParse(text, out _)))
        {
            string shown = TryString(value, out string? raw) ? raw : value?.ToJsonString() ?? "null";
            findings.Add(new(JsonPath.Root.Append(key), $"properties.{key} '{shown}' is not a GUID", SchemaCovered: true));
        }
    }

    /// <summary>Adds a schema-covered finding when the known value of <paramref name="key"/> is not a non-empty string.</summary>
    public static void CheckNonEmptyString(JsonObject properties, string key, Func<JsonNode?, bool> isOpaque, List<PimRequestFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(findings);
        if (Judged(properties, key, isOpaque) is (string found, var value) && (!TryString(value, out string? text) || text.Trim().Length == 0))
        {
            findings.Add(new(JsonPath.Root.Append(found), $"properties.{found} must be a non-empty string", SchemaCovered: true));
        }
    }

    // ---- schedule ----

    private static void CheckSchedule(JsonNode? node, JsonPath path, string where, Func<JsonNode?, bool> isOpaque, List<PimRequestFinding> findings)
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

    // ---- JSON helpers (Core has no JsonObjects) ----

    internal static (string Key, JsonNode? Value)? Property(JsonObject obj, string name)
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

    internal static (string Key, JsonNode? Value)? Judged(JsonObject obj, string name, Func<JsonNode?, bool> isOpaque)
    {
        return Property(obj, name) is (string key, var value) && !IsAbsent(value) && !isOpaque(value) ? (key, value) : null;
    }

    internal static bool IsAbsent(JsonNode? node)
    {
        return node is null || (node is JsonValue value && value.GetValueKind() == JsonValueKind.Null);
    }

    internal static bool TryString(JsonNode? node, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? text)
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
