using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Engine.Providers;
using EArm.Expressions.Values;

namespace EArm.Providers;

/// <summary>
/// The pure rules of <see cref="AuthenticationStrengthsProvider"/>: the built-in /
/// custom split on <c>policyType</c>, the set comparison of <c>allowedCombinations</c> (each combination is
/// compared trimmed, lower-cased and with its comma-separated parts sorted, so <c>"Password, microsoftAuthenticatorPush"</c>
/// equals <c>"microsoftauthenticatorpush,password"</c>; the array is compared as a set), the shape checks, and the
/// server-assigned properties stripped from bodies. Values are sent to Graph as declared (trimmed); only the
/// comparison is normalised. Testable without Graph.
/// </summary>
public static class AuthenticationStrengthsRules
{
    public const string PolicyTypeProperty = "policyType";

    public const string CustomPolicyType = "custom";

    public const string BuiltInPolicyType = "builtIn";

    public const string CombinationsProperty = "allowedCombinations";

    public const string DisplayNameProperty = "displayName";

    /// <summary>The action that replaces <c>allowedCombinations</c> (the property is not PATCHable).</summary>
    public const string UpdateCombinationsAction = "updateAllowedCombinations";

    /// <summary>Properties Graph assigns or manages; never sent in a body.</summary>
    public static readonly string[] ServerAssignedProperties = ["id", "createdDateTime", "modifiedDateTime", PolicyTypeProperty, "requirementsSatisfied", "combinationConfigurations"];

    /// <summary>True when the (raw) live object is a built-in strength (<c>policyType: builtIn</c>), which Graph never lets anyone modify or delete.</summary>
    public static bool IsBuiltIn(JsonObject? live)
    {
        return JsonObjects.GetString(live, PolicyTypeProperty) is { } type && type.Equals(BuiltInPolicyType, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>One combination in comparison form: parts split on <c>,</c>, trimmed, lower-cased, sorted and re-joined.</summary>
    public static string NormalizeCombination(string combination)
    {
        ArgumentNullException.ThrowIfNull(combination);
        var parts = combination
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.ToLowerInvariant())
            .Order(StringComparer.Ordinal);
        return string.Join(",", parts);
    }

    /// <summary>
    /// The combinations of a node as a sorted, de-duplicated list of comparison forms, or <c>null</c> when the node
    /// is not an array of plain strings (absent, unknown, or malformed).
    /// </summary>
    public static IReadOnlyList<string>? CanonicalCombinations(JsonNode? combinations)
    {
        if (combinations is not JsonArray array)
        {
            return null;
        }

        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var item in array)
        {
            if (item is not JsonValue value || UnknownJson.IsUnknown(value) || !JsonValues.TryGetString(value, out var text))
            {
                return null;
            }

            string normalized = NormalizeCombination(text);
            if (normalized.Length > 0)
            {
                set.Add(normalized);
            }
        }

        return set.ToList();
    }

    /// <summary>True when both objects carry <c>allowedCombinations</c> and the sets compare equal.</summary>
    public static bool CombinationsEqual(JsonObject? desired, JsonObject? live)
    {
        JsonObjects.TryGetIgnoreCase(desired, CombinationsProperty, out var desiredNode);
        JsonObjects.TryGetIgnoreCase(live, CombinationsProperty, out var liveNode);
        var left = CanonicalCombinations(desiredNode);
        var right = CanonicalCombinations(liveNode);
        return left is not null && right is not null && left.SequenceEqual(right, StringComparer.Ordinal);
    }

    /// <summary>True when the evaluated properties declare <c>allowedCombinations</c> with at least one entry.</summary>
    public static bool DeclaresCombinations(JsonObject? properties)
    {
        return JsonObjects.TryGetIgnoreCase(properties, CombinationsProperty, out var node) && !JsonObjects.IsAbsent(node);
    }

    /// <summary>
    /// Normalises the evaluated properties in place (string combinations trimmed; a declared <c>policyType: custom</c>
    /// is dropped, it is implied) and returns the problems that make the object unsupported: a <c>policyType</c>
    /// other than <c>custom</c> (built-in strengths cannot be managed), an <c>allowedCombinations</c> that is not an
    /// array of non-empty strings, or one declared empty / <c>null</c>. Deploy-time-unknown values are left alone;
    /// an absent <c>allowedCombinations</c> is reported by <c>requiredOnCreate</c>.
    /// </summary>
    public static IReadOnlyList<string> NormalizeDesired(JsonObject properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        var problems = new List<string>();

        if (JsonObjects.FindKey(properties, PolicyTypeProperty) is { } policyTypeKey && !JsonObjects.IsAbsent(properties[policyTypeKey]) && !UnknownJson.IsUnknown(properties[policyTypeKey]))
        {
            if (JsonValues.TryGetString(properties[policyTypeKey], out var policyType) && policyType.Trim().Equals(CustomPolicyType, StringComparison.OrdinalIgnoreCase))
            {
                properties.Remove(policyTypeKey);
            }
            else
            {
                problems.Add($"properties.{PolicyTypeProperty} must be '{CustomPolicyType}' (built-in authentication strengths cannot be managed; reference them instead)");
            }
        }

        if (JsonObjects.FindKey(properties, CombinationsProperty) is { } combinationsKey)
        {
            var node = properties[combinationsKey];
            if (UnknownJson.IsUnknown(node))
            {
                // Known only at deployment time; checked again right before the write.
            }
            else if (node is JsonArray array)
            {
                if (array.Count == 0)
                {
                    problems.Add($"properties.{CombinationsProperty} must list at least one authentication method combination");
                }

                NormalizeArray(array, problems);
            }
            else if (JsonObjects.IsAbsent(node))
            {
                problems.Add($"properties.{CombinationsProperty} must list at least one authentication method combination");
            }
            else
            {
                problems.Add($"properties.{CombinationsProperty} must be an array of strings such as 'fido2' or 'password,microsoftAuthenticatorPush'");
            }
        }

        return problems;
    }

    /// <summary>
    /// When the desired and live combination sets compare equal, the desired array takes the live spelling (order and
    /// casing) so the shared diff and the what-if row see no change. Returns <c>true</c> when adopted.
    /// </summary>
    public static bool AdoptLiveCombinations(JsonObject desired, JsonObject? live)
    {
        ArgumentNullException.ThrowIfNull(desired);
        if (!CombinationsEqual(desired, live) || !JsonObjects.TryGetIgnoreCase(live, CombinationsProperty, out var liveNode))
        {
            return false;
        }

        desired[JsonObjects.FindKey(desired, CombinationsProperty) ?? CombinationsProperty] = JsonValues.Clone(liveNode);
        return true;
    }

    /// <summary>The body of <c>POST …/updateAllowedCombinations</c>: the declared combinations, as declared.</summary>
    public static JsonObject UpdateCombinationsBody(JsonObject properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        JsonObjects.TryGetIgnoreCase(properties, CombinationsProperty, out var node);
        return new JsonObject { [CombinationsProperty] = JsonValues.Clone(node) ?? new JsonArray() };
    }

    /// <summary>Removes <c>allowedCombinations</c> from a PATCH body; the property only travels through the action.</summary>
    public static bool RemoveCombinations(JsonObject body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return JsonObjects.FindKey(body, CombinationsProperty) is { } key && body.Remove(key);
    }

    /// <summary>The server-assigned and server-managed properties are rejected in bodies.</summary>
    public static void RemoveServerAssigned(JsonObject body)
    {
        ArgumentNullException.ThrowIfNull(body);
        foreach (string property in ServerAssignedProperties)
        {
            if (JsonObjects.FindKey(body, property) is { } key)
            {
                body.Remove(key);
            }
        }
    }

    // ---- helpers ----

    private static void NormalizeArray(JsonArray array, List<string> problems)
    {
        for (int i = 0; i < array.Count; i++)
        {
            var item = array[i];
            if (item is JsonValue value && UnknownJson.IsUnknown(value))
            {
                continue;
            }

            if (item is not JsonValue leaf || !JsonValues.TryGetString(leaf, out var text))
            {
                problems.Add($"properties.{CombinationsProperty}[{i}] must be a string such as 'fido2' or 'password,microsoftAuthenticatorPush'");
                continue;
            }

            string trimmed = text.Trim();
            if (trimmed.Length == 0)
            {
                problems.Add($"properties.{CombinationsProperty}[{i}] is empty");
                continue;
            }

            if (!string.Equals(trimmed, text, StringComparison.Ordinal))
            {
                array[i] = trimmed;
            }
        }
    }
}
