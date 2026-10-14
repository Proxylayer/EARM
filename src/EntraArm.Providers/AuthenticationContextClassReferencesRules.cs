using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EntraArm.Core.Secrets;
using EntraArm.Engine.Providers;
using EntraArm.Expressions.Values;

namespace EntraArm.Providers;

/// <summary>
/// The pure rules of <see cref="AuthenticationContextClassReferencesProvider"/> (plan sections 1.3, 3.2): the id
/// pattern the resource name must match (<c>c1</c>..<c>c99</c>, it is the Graph id), the required
/// <c>displayName</c>, the <c>isAvailable</c> default (<c>true</c>) applied at plan time so drift to <c>false</c>
/// is detected, the scalar shape checks, and the server-assigned property stripped from bodies. Testable without
/// Graph.
/// </summary>
public static partial class AuthenticationContextClassReferencesRules
{
    /// <summary>The ids Graph accepts: <c>c1</c>..<c>c99</c> (no leading zero, no <c>c0</c>, no <c>c100</c>).</summary>
    public const string IdPattern = "^c([1-9]|[1-9][0-9])$";

    public const string DisplayNameProperty = "displayName";

    public const string DescriptionProperty = "description";

    public const string IsAvailableProperty = "isAvailable";

    /// <summary>Properties Graph assigns (the id is the URL segment); never sent in a body.</summary>
    public static readonly string[] ServerAssignedProperties = ["id"];

    [GeneratedRegex(IdPattern, RegexOptions.CultureInvariant)]
    private static partial Regex IdRegex();

    /// <summary>True when the name is a valid authentication context id (<c>c1</c>..<c>c99</c>, lower-case <c>c</c>).</summary>
    public static bool IsValidId(string? name)
    {
        return !string.IsNullOrEmpty(name) && IdRegex().IsMatch(name);
    }

    /// <summary>The problem with a resource name that is not a valid id, or <c>null</c>.</summary>
    public static string? NameProblem(string? name)
    {
        return IsValidId(name)
            ? null
            : $"'{name}' is not a valid authentication context class reference id; the resource name is the Graph id and must be c1..c99";
    }

    /// <summary>
    /// Normalises the evaluated properties in place (<c>isAvailable</c> defaults to <c>true</c>, an explicit
    /// <c>null</c> included) and returns the problems that make the object unsupported: a missing or non-string
    /// <c>displayName</c>, a non-string <c>description</c>, a non-boolean <c>isAvailable</c>. Deploy-time-unknown
    /// values are left alone.
    /// </summary>
    public static IReadOnlyList<string> NormalizeDesired(JsonObject properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        var problems = new List<string>();

        JsonObjects.TryGetIgnoreCase(properties, DisplayNameProperty, out var displayName);
        if (JsonObjects.IsAbsent(displayName))
        {
            problems.Add($"properties.{DisplayNameProperty} is required");
        }
        else if (!UnknownJson.IsUnknown(displayName) && !IsStringLike(displayName))
        {
            problems.Add($"properties.{DisplayNameProperty} must be a string");
        }

        if (JsonObjects.TryGetIgnoreCase(properties, DescriptionProperty, out var description)
            && !JsonObjects.IsAbsent(description) && !UnknownJson.IsUnknown(description) && !IsStringLike(description))
        {
            problems.Add($"properties.{DescriptionProperty} must be a string");
        }

        JsonObjects.TryGetIgnoreCase(properties, IsAvailableProperty, out var isAvailable);
        if (JsonObjects.IsAbsent(isAvailable))
        {
            properties[JsonObjects.FindKey(properties, IsAvailableProperty) ?? IsAvailableProperty] = true;
        }
        else if (!UnknownJson.IsUnknown(isAvailable) && !IsBoolean(isAvailable))
        {
            problems.Add($"properties.{IsAvailableProperty} must be a boolean");
        }

        return problems;
    }

    /// <summary>The live <c>isAvailable</c> flag, or <c>null</c> when the object does not carry a boolean.</summary>
    public static bool? IsAvailable(JsonObject? live)
    {
        if (!JsonObjects.TryGetIgnoreCase(live, IsAvailableProperty, out var value) || !IsBoolean(value))
        {
            return null;
        }

        return ((JsonValue)value!).GetValueKind() == JsonValueKind.True;
    }

    /// <summary>The body that makes a context unavailable, the step Graph requires before a delete.</summary>
    public static JsonObject UnavailableBody()
    {
        return new JsonObject { [IsAvailableProperty] = false };
    }

    /// <summary><c>id</c> is the URL segment and is rejected in bodies.</summary>
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

    /// <summary>
    /// True for a string, or for a secret string leaf (a <c>securestring</c> parameter reference): its content is
    /// unknown to the planner but it is a string, and it reaches only the upsert body.
    /// </summary>
    private static bool IsStringLike(JsonNode? node)
    {
        return JsonValues.TryGetString(node, out _) || (SecretJson.IsSecret(node, out var secret) && secret.Kind == JsonValueKind.String);
    }

    private static bool IsBoolean(JsonNode? node)
    {
        return node is JsonValue value && !UnknownJson.IsUnknown(value) && value.GetValueKind() is JsonValueKind.True or JsonValueKind.False;
    }
}
