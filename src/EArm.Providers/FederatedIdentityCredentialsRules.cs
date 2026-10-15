using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EArm.Core.Secrets;
using EArm.Engine.Providers;
using EArm.Expressions.Values;

namespace EArm.Providers;

/// <summary>A live federated identity credential as the provider's sibling snapshot keeps it: enough to check uniqueness and the per-application limit.</summary>
public sealed record FederatedCredentialSummary(string? Id, string Name, string? Issuer, string? Subject);

/// <summary>
/// The pure rules of the federated identity credentials provider, free of Graph I/O so
/// they can be tested on their own: the name, issuer and subject constraints Graph enforces, the
/// <c>issuer + subject</c> pair that must be unique per application, and the 20-per-application limit.
/// </summary>
public static class FederatedIdentityCredentialsRules
{
    /// <summary>Graph allows at most this many federated identity credentials per application.</summary>
    public const int MaxPerApplication = 20;

    /// <summary>The overlay's name constraint: 3–120 characters, letters, digits, <c>-</c> and <c>_</c>, starting with a letter or digit.</summary>
    public const string DefaultNamePattern = "^[A-Za-z0-9][A-Za-z0-9_-]{2,119}$";

    /// <summary>Only the beta endpoint (<c>2026-10-01-preview.beta</c>) accepts this property.</summary>
    public const string PreviewOnlyProperty = "claimsMatchingExpression";

    /// <summary>The managed property set every v1.0 read selects.</summary>
    public static readonly string[] ManagedProperties = ["id", "name", "issuer", "subject", "audiences", "description"];

    /// <summary>The managed property set every beta read selects (adds <see cref="PreviewOnlyProperty"/>).</summary>
    public static readonly string[] PreviewManagedProperties = [.. ManagedProperties, PreviewOnlyProperty];

    /// <summary>The properties the sibling list read selects (uniqueness and limit checks only).</summary>
    public static readonly string[] SummaryProperties = ["id", "name", "issuer", "subject"];

    /// <summary>Why <paramref name="name"/> is not a valid credential name, or <c>null</c> when it is.</summary>
    public static string? NameProblem(string name, string? pattern = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        string regex = string.IsNullOrEmpty(pattern) ? DefaultNamePattern : pattern;
        if (Regex.IsMatch(name, regex, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
        {
            return null;
        }

        return $"the credential name '{name}' is not valid: Graph requires 3 to 120 characters, letters, digits, '-' and '_' only, starting with a letter or digit (pattern {regex}).";
    }

    /// <summary>
    /// Why the declared <c>issuer</c> is not acceptable, or <c>null</c>: absent, secret and deploy-time values are
    /// not judged; a plain value must be an absolute <c>https</c> URL.
    /// </summary>
    public static string? IssuerProblem(JsonNode? issuer)
    {
        if (!IsPlainValue(issuer))
        {
            return null;
        }

        if (!JsonValues.TryGetString(issuer, out var text) || string.IsNullOrWhiteSpace(text))
        {
            return "properties.issuer must be a non-empty https URL.";
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return $"properties.issuer ('{text}') must be an absolute https URL (the issuer of the external identity provider's tokens).";
        }

        return null;
    }

    /// <summary>Why the declared <c>subject</c> is not acceptable, or <c>null</c>; secret and deploy-time values are not judged.</summary>
    public static string? SubjectProblem(JsonNode? subject)
    {
        if (!IsPlainValue(subject))
        {
            return null;
        }

        if (!JsonValues.TryGetString(subject, out var text) || string.IsNullOrWhiteSpace(text))
        {
            return "properties.subject must be a non-empty string (compared case-sensitively by Graph).";
        }

        return null;
    }

    /// <summary>The <c>(issuer, subject)</c> pair when both are plain strings; <c>null</c> when either is absent, secret or deploy-time.</summary>
    public static (string Issuer, string Subject)? PairOf(JsonObject properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        string? issuer = JsonObjects.GetString(properties, "issuer");
        string? subject = JsonObjects.GetString(properties, "subject");
        return issuer is null || subject is null ? null : (issuer, subject);
    }

    /// <summary>Graph compares the subject case-sensitively; the issuer is compared the same way so that no valid pair is refused by the provider.</summary>
    public static bool SamePair((string Issuer, string Subject) left, (string Issuer, string Subject) right)
    {
        return string.Equals(left.Issuer, right.Issuer, StringComparison.Ordinal) && string.Equals(left.Subject, right.Subject, StringComparison.Ordinal);
    }

    /// <summary>Credential names are an alternate key; Graph matches them case-insensitively.</summary>
    public static bool NamesEqual(string? left, string? right)
    {
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The live credential, other than <paramref name="exceptName"/>, that already uses <paramref name="pair"/>, or <c>null</c>.</summary>
    public static FederatedCredentialSummary? FindPairHolder(IEnumerable<FederatedCredentialSummary> siblings, (string Issuer, string Subject) pair, string exceptName)
    {
        ArgumentNullException.ThrowIfNull(siblings);
        ArgumentNullException.ThrowIfNull(exceptName);
        foreach (var sibling in siblings)
        {
            if (NamesEqual(sibling.Name, exceptName) || sibling.Issuer is null || sibling.Subject is null)
            {
                continue;
            }

            if (SamePair(pair, (sibling.Issuer, sibling.Subject)))
            {
                return sibling;
            }
        }

        return null;
    }

    /// <summary>The distinct credential names (case-insensitive) across the live siblings and the names declared elsewhere in the deployment.</summary>
    public static int CountOthers(IEnumerable<FederatedCredentialSummary> siblings, IEnumerable<string> declaredNames, string exceptName)
    {
        ArgumentNullException.ThrowIfNull(siblings);
        ArgumentNullException.ThrowIfNull(declaredNames);
        ArgumentNullException.ThrowIfNull(exceptName);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sibling in siblings)
        {
            if (!NamesEqual(sibling.Name, exceptName))
            {
                names.Add(sibling.Name);
            }
        }

        foreach (var declared in declaredNames)
        {
            if (!NamesEqual(declared, exceptName))
            {
                names.Add(declared);
            }
        }

        return names.Count;
    }

    /// <summary>Reduces live credential objects to their summaries; objects without a <c>name</c> are skipped.</summary>
    public static IReadOnlyList<FederatedCredentialSummary> Summarize(IEnumerable<JsonObject> live)
    {
        ArgumentNullException.ThrowIfNull(live);
        var summaries = new List<FederatedCredentialSummary>();
        foreach (var credential in live)
        {
            if (JsonObjects.GetString(credential, "name") is { } name)
            {
                summaries.Add(new FederatedCredentialSummary(JsonObjects.GetId(credential), name, JsonObjects.GetString(credential, "issuer"), JsonObjects.GetString(credential, "subject")));
            }
        }

        return summaries;
    }

    private static bool IsPlainValue(JsonNode? node)
    {
        return !JsonObjects.IsAbsent(node) && !SecretJson.IsSecret(node) && !UnknownJson.IsUnknown(node);
    }
}
