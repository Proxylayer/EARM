using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Engine.Providers;
using EArm.Expressions.Values;

namespace EArm.Providers;

/// <summary>
/// The pure rules of delegated permission grants: consent types, the <c>scope</c> string as a
/// whitespace-separated set of permission values (order-insensitive, duplicates collapsed), the <c>scopes</c>
/// array sugar, and the check of scope values against the resource principal's <c>oauth2PermissionScopes</c>.
/// </summary>
public static class OAuth2PermissionGrantsRules
{
    public const string ConsentAllPrincipals = "AllPrincipals";

    public const string ConsentPrincipal = "Principal";

    /// <summary>The property names a grant accepts (canonical and sugar).</summary>
    public static readonly string[] AllowedProperties = ["clientId", "resourceId", "consentType", "principalId", "scope", "scopes"];

    /// <summary>The canonical casing of a consent type, or <c>null</c> when it is neither value.</summary>
    public static string? NormalizeConsentType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        if (trimmed.Equals(ConsentAllPrincipals, StringComparison.OrdinalIgnoreCase))
        {
            return ConsentAllPrincipals;
        }

        return trimmed.Equals(ConsentPrincipal, StringComparison.OrdinalIgnoreCase) ? ConsentPrincipal : null;
    }

    /// <summary>The permission values of a scope string: whitespace-split, duplicates removed, order kept.</summary>
    public static IReadOnlyList<string> Tokens(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var tokens = new List<string>();
        foreach (var token in scope.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (seen.Add(token))
            {
                tokens.Add(token);
            }
        }

        return tokens;
    }

    public static string Join(IEnumerable<string> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        return string.Join(' ', tokens);
    }

    /// <summary>The scope string with whitespace collapsed and duplicates removed (order kept).</summary>
    public static string Normalize(string scope)
    {
        return Join(Tokens(scope));
    }

    /// <summary>Order-insensitive equality of two scope strings (values compared ordinally, as Graph does).</summary>
    public static bool SameSet(string? left, string? right)
    {
        var l = new HashSet<string>(Tokens(left), StringComparer.Ordinal);
        var r = new HashSet<string>(Tokens(right), StringComparer.Ordinal);
        return l.SetEquals(r);
    }

    /// <summary>The permission values (ordinal) the resource principal does not expose in <c>oauth2PermissionScopes</c>.</summary>
    public static IReadOnlyList<string> UnknownScopes(IEnumerable<string> tokens, ServicePrincipalSummary resource)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(resource);
        return tokens.Where(token => resource.FindPermissionScopeId(token) is null).ToList();
    }

    /// <summary>The permission values the resource principal exposes but has disabled (<c>isEnabled: false</c>).</summary>
    public static IReadOnlyList<string> DisabledScopes(IEnumerable<string> tokens, ServicePrincipalSummary resource)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(resource);
        var disabled = new List<string>();
        foreach (var token in tokens)
        {
            foreach (var item in resource.Oauth2PermissionScopes)
            {
                if (item is JsonObject scope
                    && string.Equals(JsonObjects.GetString(scope, "value"), token, StringComparison.Ordinal)
                    && JsonObjects.TryGetIgnoreCase(scope, "isEnabled", out var enabled)
                    && JsonValues.TryGetBool(enabled, out bool isEnabled)
                    && !isEnabled)
                {
                    disabled.Add(token);
                }
            }
        }

        return disabled;
    }

    /// <summary>The <c>value</c>s of the resource's scopes, sorted, for messages (truncated after <paramref name="max"/>).</summary>
    public static string ScopeValues(ServicePrincipalSummary resource, int max = 20)
    {
        ArgumentNullException.ThrowIfNull(resource);
        var values = resource.Oauth2PermissionScopes.OfType<JsonObject>().Select(s => JsonObjects.GetString(s, "value")).Where(v => !string.IsNullOrEmpty(v)).Select(v => v!).Order(StringComparer.Ordinal).ToList();
        if (values.Count == 0)
        {
            return "(none)";
        }

        return values.Count <= max ? string.Join(", ", values) : string.Join(", ", values.Take(max)) + $", … ({values.Count} in total)";
    }
}
