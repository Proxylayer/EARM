using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EArm.Core.Secrets;
using EArm.Engine.Providers;
using EArm.Expressions.Values;

namespace EArm.Providers;

/// <summary>A live certificate credential as the provider sees it: its identity, the SHA-1 thumbprint of its public certificate and its attributes.</summary>
public sealed record KeyCredentialSummary(string KeyId, string? DisplayName, string? Thumbprint, string? Type, string? Usage, DateTimeOffset? StartDateTime, DateTimeOffset? EndDateTime, bool HasKey);

/// <summary>The declared certificate credential: the DER bytes of the public certificate (never a private key), what they imply, and the declared attributes.</summary>
public sealed record DesiredKeyCredential(byte[] Der, string Thumbprint, DateTimeOffset NotBefore, DateTimeOffset NotAfter, string Type, string Usage, DateTimeOffset? StartDateTime, DateTimeOffset? EndDateTime, bool KeyIsSecret);

/// <summary>One difference between the declared and the live certificate credential.</summary>
public sealed record KeyCredentialDifference(string Property, JsonNode? Before, JsonNode? After);

/// <summary>
/// The pure rules of the application certificate credentials provider (<c>Microsoft.Entra/applications/keyCredentials</c>), free of Graph I/O: the
/// certificate parsing (public X.509 certificates only, base64 DER or PEM; private keys and PKCS#12 are refused), the thumbprint, the matching by
/// <c>displayName</c>, the comparison and the replacement <c>keyCredentials</c> collection that keeps every credential EARM does not manage.
/// Learn: https://learn.microsoft.com/en-us/graph/api/resources/keycredential?view=graph-rest-1.0 and
/// https://learn.microsoft.com/en-us/graph/applications-how-to-add-certificate.
/// </summary>
public static class KeyCredentialsRules
{
    public const string KeyCredentialsProperty = "keyCredentials";

    public const string DisplayNameProperty = "displayName";

    public const string KeyProperty = "key";

    public const string KeyIdProperty = "keyId";

    public const string TypeProperty = "type";

    public const string UsageProperty = "usage";

    public const string ThumbprintProperty = "thumbprint";

    public const string CustomKeyIdentifierProperty = "customKeyIdentifier";

    /// <summary>The only credential type EARM writes: a public certificate (<c>X509CertAndPassword</c> would carry a private key).</summary>
    public const string AsymmetricX509Cert = "AsymmetricX509Cert";

    /// <summary>The default usage (Learn: an application's certificate credential is <c>AsymmetricX509Cert</c> + <c>Verify</c>).</summary>
    public const string Verify = "Verify";

    public const string Sign = "Sign";

    /// <summary>Learn: the displayName has "a maximum length of 90 characters. Longer values are accepted but shortened".</summary>
    public const int MaxDisplayNameLength = 90;

    /// <summary>The overlay's name constraint: 1-90 characters, starting with a letter or digit; letters, digits, space and <c>. _ = , ( ) @ -</c>.</summary>
    public const string DefaultNamePattern = "^[A-Za-z0-9][A-Za-z0-9 ._=,()@-]{0,89}$";

    /// <summary>The single-object read that returns the certificate bytes (<c>key</c> is only returned for <c>GET /applications/{id}?$select=keyCredentials</c>).</summary>
    public static readonly string[] ParentSelect = ["id", KeyCredentialsProperty];

    private static readonly string[] KnownUsages = [Verify, Sign];

    // ---- certificates ----

    /// <summary>
    /// Parses the declared <c>key</c>: a base64 DER certificate (a <c>.cer</c> file read with <c>Convert.ToBase64String</c>) or a PEM
    /// <c>-----BEGIN CERTIFICATE-----</c> block. Returns why it is not acceptable, or <c>null</c> with the DER bytes.
    /// </summary>
    public static string? ParseCertificate(string text, out byte[] der, out X509Certificate2? certificate)
    {
        ArgumentNullException.ThrowIfNull(text);
        der = [];
        certificate = null;
        if (text.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase))
        {
            return "properties.key contains a private key; EARM only accepts the public certificate (base64 DER or a PEM CERTIFICATE block) and never handles private keys.";
        }

        string body = text.Trim();
        if (body.Contains("-----BEGIN", StringComparison.Ordinal))
        {
            var match = Regex.Match(body, "-----BEGIN CERTIFICATE-----(?<b64>[A-Za-z0-9+/=\\s]+)-----END CERTIFICATE-----", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            if (!match.Success)
            {
                return "properties.key is PEM but holds no CERTIFICATE block; give the public certificate (base64 DER or a PEM CERTIFICATE block).";
            }

            body = match.Groups["b64"].Value;
        }

        try
        {
            der = Convert.FromBase64String(Regex.Replace(body, "\\s+", string.Empty, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)));
        }
        catch (FormatException)
        {
            return "properties.key is neither base64 DER nor a PEM CERTIFICATE block.";
        }

        try
        {
            certificate = X509CertificateLoader.LoadCertificate(der);
        }
        catch (CryptographicException)
        {
            der = [];
            return "properties.key is not an X.509 certificate (a PFX/PKCS#12 file, which carries a private key, is refused: give the public certificate only).";
        }

        return null;
    }

    /// <summary>The SHA-1 thumbprint of a DER certificate, upper-case hex (what the portal and <c>Get-PfxCertificate</c> show).</summary>
    public static string Thumbprint(byte[] der)
    {
        ArgumentNullException.ThrowIfNull(der);
#pragma warning disable CA5350 // The certificate thumbprint is SHA-1 by definition; it identifies, it does not protect.
        return Convert.ToHexString(SHA1.HashData(der));
#pragma warning restore CA5350
    }

    // ---- desired ----

    /// <summary>Interprets the declared properties; returns why they are not acceptable, or <c>null</c> with <paramref name="desired"/> set.</summary>
    public static string? DescribeDesired(JsonObject properties, out DesiredKeyCredential? desired)
    {
        ArgumentNullException.ThrowIfNull(properties);
        desired = null;
        if (!JsonObjects.TryGetIgnoreCase(properties, KeyProperty, out var keyNode) || JsonObjects.IsAbsent(keyNode))
        {
            return "properties.key (the public certificate, base64 DER or PEM) is required.";
        }

        bool secret = SecretJson.IsSecret(keyNode, out var secretValue);
        string? text = secret ? secretValue!.Value : JsonValues.TryGetString(keyNode, out var plain) ? plain : null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return "properties.key must be a non-empty string (the public certificate, base64 DER or PEM).";
        }

        if (ParseCertificate(text, out var der, out var certificate) is { } problem)
        {
            return problem;
        }

        using (certificate)
        {
            string type = StringOr(properties, TypeProperty, AsymmetricX509Cert);
            if (!string.Equals(type, AsymmetricX509Cert, StringComparison.Ordinal))
            {
                return $"properties.type '{type}' is not supported: EARM writes public certificates only ('{AsymmetricX509Cert}'); other types need a private key or a symmetric secret.";
            }

            string usage = StringOr(properties, UsageProperty, Verify);
            if (Array.IndexOf(KnownUsages, usage) < 0)
            {
                return $"properties.usage '{usage}' is not valid; use '{Verify}'.";
            }

            if (string.Equals(usage, Sign, StringComparison.Ordinal))
            {
                // Learn (application-addkey): "AsymmetricX509Cert: The usage must be Verify"; Sign goes with X509CertAndPassword, which carries the private key.
                return $"properties.usage '{Sign}' is not supported: a signing key needs the private key (type X509CertAndPassword), which EARM never handles; an '{AsymmetricX509Cert}' certificate is used with '{Verify}'.";
            }

            if (!TryDate(properties, PasswordCredentialsRules.StartDateTimeProperty, out var start, out var startProblem))
            {
                return startProblem;
            }

            if (!TryDate(properties, PasswordCredentialsRules.EndDateTimeProperty, out var end, out var endProblem))
            {
                return endProblem;
            }

            if (start is { } s && end is { } e && e <= s)
            {
                return "properties.endDateTime must be later than properties.startDateTime.";
            }

            desired = new DesiredKeyCredential(der, Thumbprint(der), certificate!.NotBefore.ToUniversalTime(), certificate.NotAfter.ToUniversalTime(), type, usage, start, end, secret);
            return null;
        }
    }

    /// <summary>The <c>keyCredentials</c> entry written for the declared credential: name, type, usage, the certificate and the declared dates.</summary>
    public static JsonObject DesiredEntry(string displayName, DesiredKeyCredential desired)
    {
        ArgumentNullException.ThrowIfNull(displayName);
        ArgumentNullException.ThrowIfNull(desired);
        var entry = new JsonObject
        {
            [DisplayNameProperty] = displayName,
            [TypeProperty] = desired.Type,
            [UsageProperty] = desired.Usage,
            [KeyProperty] = Convert.ToBase64String(desired.Der),
        };
        if (desired.StartDateTime is { } start)
        {
            entry[PasswordCredentialsRules.StartDateTimeProperty] = PasswordCredentialsRules.Format(start);
        }

        if (desired.EndDateTime is { } end)
        {
            entry[PasswordCredentialsRules.EndDateTimeProperty] = PasswordCredentialsRules.Format(end);
        }

        return entry;
    }

    // ---- live ----

    /// <summary>Reduces the application's <c>keyCredentials</c> array to summaries; entries without <c>keyId</c> are skipped.</summary>
    public static IReadOnlyList<KeyCredentialSummary> Summarize(JsonNode? keyCredentials)
    {
        var summaries = new List<KeyCredentialSummary>();
        if (keyCredentials is not JsonArray array)
        {
            return summaries;
        }

        foreach (var item in array)
        {
            if (item is JsonObject credential && SummaryOf(credential) is { } summary)
            {
                summaries.Add(summary);
            }
        }

        return summaries;
    }

    /// <summary>One live entry as a summary (the thumbprint computed from <c>key</c> when Graph returned it); <c>null</c> without <c>keyId</c>.</summary>
    public static KeyCredentialSummary? SummaryOf(JsonObject credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        if (JsonObjects.GetString(credential, KeyIdProperty) is not { Length: > 0 } keyId)
        {
            return null;
        }

        string? thumbprint = null;
        string? key = JsonObjects.GetString(credential, KeyProperty);
        if (key is { Length: > 0 })
        {
            try
            {
                thumbprint = Thumbprint(Convert.FromBase64String(key));
            }
            catch (FormatException)
            {
                thumbprint = null;
            }
        }

        thumbprint ??= JsonObjects.GetString(credential, ThumbprintProperty);
        JsonObjects.TryGetIgnoreCase(credential, PasswordCredentialsRules.StartDateTimeProperty, out var startNode);
        JsonObjects.TryGetIgnoreCase(credential, PasswordCredentialsRules.EndDateTimeProperty, out var endNode);
        PasswordCredentialsRules.TryParseDateTime(startNode, out var start);
        PasswordCredentialsRules.TryParseDateTime(endNode, out var end);
        return new KeyCredentialSummary(
            keyId,
            JsonObjects.GetString(credential, DisplayNameProperty),
            thumbprint,
            JsonObjects.GetString(credential, TypeProperty),
            JsonObjects.GetString(credential, UsageProperty),
            start == default ? null : start,
            end == default ? null : end,
            key is { Length: > 0 });
    }

    /// <summary>The live credentials whose <c>displayName</c> is exactly <paramref name="displayName"/> (the managed key).</summary>
    public static IReadOnlyList<KeyCredentialSummary> SameName(IEnumerable<KeyCredentialSummary> credentials, string displayName)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(displayName);
        var matches = new List<KeyCredentialSummary>();
        foreach (var credential in credentials)
        {
            if (string.Equals(credential.DisplayName, displayName, StringComparison.Ordinal))
            {
                matches.Add(credential);
            }
        }

        return matches;
    }

    /// <summary>The differences that make the declared credential replace the live one; empty when they match.</summary>
    public static IReadOnlyList<KeyCredentialDifference> Compare(DesiredKeyCredential desired, KeyCredentialSummary live)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(live);
        var differences = new List<KeyCredentialDifference>();
        if (!string.Equals(desired.Thumbprint, live.Thumbprint, StringComparison.OrdinalIgnoreCase))
        {
            differences.Add(new KeyCredentialDifference(ThumbprintProperty, live.Thumbprint, desired.KeyIsSecret ? SecretValue.Mask : desired.Thumbprint));
        }

        if (!string.Equals(desired.Type, live.Type, StringComparison.Ordinal))
        {
            differences.Add(new KeyCredentialDifference(TypeProperty, live.Type, desired.Type));
        }

        if (!string.Equals(desired.Usage, live.Usage, StringComparison.Ordinal))
        {
            differences.Add(new KeyCredentialDifference(UsageProperty, live.Usage, desired.Usage));
        }

        if (desired.StartDateTime is { } start && !PasswordCredentialsRules.SameInstant(start, live.StartDateTime))
        {
            differences.Add(new KeyCredentialDifference(PasswordCredentialsRules.StartDateTimeProperty, FormatOrNull(live.StartDateTime), PasswordCredentialsRules.Format(start)));
        }

        if (desired.EndDateTime is { } end && !PasswordCredentialsRules.SameInstant(end, live.EndDateTime))
        {
            differences.Add(new KeyCredentialDifference(PasswordCredentialsRules.EndDateTimeProperty, FormatOrNull(live.EndDateTime), PasswordCredentialsRules.Format(end)));
        }

        return differences;
    }

    /// <summary>
    /// The full <c>keyCredentials</c> collection to PATCH: every live entry whose <c>displayName</c> is not <paramref name="displayName"/>, sent back as
    /// read (with its <c>key</c>, so Graph keeps it), plus <paramref name="replacement"/> when given. Ensure-only: an unmanaged credential is never dropped.
    /// </summary>
    public static JsonArray ReplaceCollection(JsonNode? live, string displayName, JsonObject? replacement)
    {
        ArgumentNullException.ThrowIfNull(displayName);
        var collection = new JsonArray();
        if (live is JsonArray entries)
        {
            foreach (var item in entries)
            {
                if (item is JsonObject entry && !string.Equals(JsonObjects.GetString(entry, DisplayNameProperty), displayName, StringComparison.Ordinal))
                {
                    collection.Add(entry.DeepClone());
                }
            }
        }

        if (replacement is not null)
        {
            collection.Add(replacement.DeepClone());
        }

        return collection;
    }

    /// <summary>The live entries Graph returned without their certificate bytes: PATCHing the collection back would lose them, so no write is attempted.</summary>
    public static IReadOnlyList<KeyCredentialSummary> WithoutKey(IEnumerable<KeyCredentialSummary> credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        var missing = new List<KeyCredentialSummary>();
        foreach (var credential in credentials)
        {
            if (!credential.HasKey)
            {
                missing.Add(credential);
            }
        }

        return missing;
    }

    // ---- views ----

    /// <summary>The what-if live side: the name, thumbprint, type, usage and dates (the certificate bytes are summarized by their thumbprint).</summary>
    public static JsonObject DiffView(KeyCredentialSummary credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        var view = new JsonObject
        {
            [DisplayNameProperty] = credential.DisplayName,
            [ThumbprintProperty] = credential.Thumbprint,
            [TypeProperty] = credential.Type,
            [UsageProperty] = credential.Usage,
        };
        if (credential.StartDateTime is { } start)
        {
            view[PasswordCredentialsRules.StartDateTimeProperty] = PasswordCredentialsRules.Format(start);
        }

        if (credential.EndDateTime is { } end)
        {
            view[PasswordCredentialsRules.EndDateTimeProperty] = PasswordCredentialsRules.Format(end);
        }

        return view;
    }

    /// <summary>The published live object for <c>reference()</c>: the diff view plus the <c>keyId</c>.</summary>
    public static JsonObject LiveView(KeyCredentialSummary credential)
    {
        var view = DiffView(credential);
        view[KeyIdProperty] = credential.KeyId;
        view["id"] = credential.KeyId;
        return view;
    }

    /// <summary>The what-if desired side, shaped like <see cref="DiffView"/> (the thumbprint masked when the certificate came from a secure value).</summary>
    public static JsonObject AfterView(string displayName, DesiredKeyCredential desired)
    {
        ArgumentNullException.ThrowIfNull(desired);
        var view = new JsonObject
        {
            [DisplayNameProperty] = displayName,
            [ThumbprintProperty] = desired.KeyIsSecret ? SecretValue.Mask : desired.Thumbprint,
            [TypeProperty] = desired.Type,
            [UsageProperty] = desired.Usage,
        };
        if (desired.StartDateTime is { } start)
        {
            view[PasswordCredentialsRules.StartDateTimeProperty] = PasswordCredentialsRules.Format(start);
        }

        if (desired.EndDateTime is { } end)
        {
            view[PasswordCredentialsRules.EndDateTimeProperty] = PasswordCredentialsRules.Format(end);
        }

        return view;
    }

    // ---- helpers ----

    private static string StringOr(JsonObject properties, string property, string fallback)
    {
        return JsonObjects.TryGetIgnoreCase(properties, property, out var node) && !JsonObjects.IsAbsent(node) && JsonValues.TryGetString(node, out var text) ? text : fallback;
    }

    private static bool TryDate(JsonObject properties, string property, out DateTimeOffset? value, out string? problem)
    {
        value = null;
        problem = null;
        if (!JsonObjects.TryGetIgnoreCase(properties, property, out var node) || JsonObjects.IsAbsent(node))
        {
            return true;
        }

        if (!PasswordCredentialsRules.TryParseDateTime(node, out var parsed))
        {
            problem = $"properties.{property} must be an ISO 8601 date-time (UTC), for example 2027-01-01T00:00:00Z.";
            return false;
        }

        value = parsed;
        return true;
    }

    private static string? FormatOrNull(DateTimeOffset? value)
    {
        return value is { } v ? PasswordCredentialsRules.Format(v) : null;
    }
}
