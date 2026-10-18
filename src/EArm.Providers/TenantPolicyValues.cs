using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using EArm.Core.Secrets;
using EArm.Engine.Providers;
using EArm.Expressions.Values;

namespace EArm.Providers;

/// <summary>Small value helpers of the tenant policy rules: opaque (deploy-time or secret) values, declared strings and case-insensitive enum values.</summary>
public static class TenantPolicyValues
{
    /// <summary>A deploy-time-unknown or secret value cannot be judged at plan time.</summary>
    public static bool IsOpaque(JsonNode? node)
    {
        return UnknownJson.IsUnknown(node) || SecretJson.IsSecret(node, out _);
    }

    /// <summary>The declared value of <paramref name="property"/> when present and not opaque; <c>false</c> when absent or opaque.</summary>
    public static bool TryGetDeclared(JsonObject properties, string property, out string key, out JsonNode? value)
    {
        ArgumentNullException.ThrowIfNull(properties);
        key = JsonObjects.FindKey(properties, property) ?? property;
        value = null;
        if (!properties.TryGetPropertyValue(key, out value) || IsOpaque(value))
        {
            value = null;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Replaces a declared string enum value by its canonical spelling (case-insensitive match); a value outside <paramref name="allowed"/> adds a problem.
    /// Absent and opaque values are left alone.
    /// </summary>
    public static void CanonicalizeEnum(JsonObject properties, string property, IReadOnlyList<string> allowed, List<string> problems)
    {
        ArgumentNullException.ThrowIfNull(allowed);
        ArgumentNullException.ThrowIfNull(problems);
        if (!TryGetDeclared(properties, property, out var key, out var value) || value is null)
        {
            return;
        }

        if (!JsonValues.TryGetString(value, out var text))
        {
            problems.Add($"properties.{property} must be a string ({string.Join(", ", allowed)})");
            return;
        }

        string? canonical = allowed.FirstOrDefault(a => a.Equals(text.Trim(), StringComparison.OrdinalIgnoreCase));
        if (canonical is null)
        {
            problems.Add($"properties.{property} '{text}' is not one of {string.Join(", ", allowed)}");
            return;
        }

        properties[key] = canonical;
    }

    /// <summary>When the declared and live values of <paramref name="property"/> are strings equal ignoring case, the declared value adopts the live spelling.</summary>
    public static void AdoptLiveSpelling(JsonObject properties, JsonObject live, string property)
    {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(live);
        if (TryGetDeclared(properties, property, out var key, out var value)
            && JsonValues.TryGetString(value, out var declared)
            && JsonObjects.GetString(live, property) is { } liveText
            && declared.Equals(liveText, StringComparison.OrdinalIgnoreCase))
        {
            properties[key] = liveText;
        }
    }

    /// <summary>True for a JSON integer within <paramref name="minimum"/>..<paramref name="maximum"/>.</summary>
    public static bool IsIntegerInRange(JsonNode? node, long minimum, long maximum)
    {
        return node is JsonValue value
            && value.GetValueKind() == JsonValueKind.Number
            && value.TryGetValue<long>(out long number)
            && number >= minimum
            && number <= maximum;
    }
}
