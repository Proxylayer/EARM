using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using EArm.Graph;

namespace EArm.TestSupport;

/// <summary>
/// The certificate credentials of <see cref="FakeDirectory"/> applications: <c>PATCH /applications/{id} { "keyCredentials": [...] }</c> replaces the whole
/// collection (Learn: applications-how-to-add-certificate), and Graph fills in what the caller leaves out: a new <c>keyId</c>, the
/// <c>customKeyIdentifier</c> (the certificate thumbprint) and the validity dates (from the certificate). An entry without its certificate bytes
/// (<c>key</c>) is refused, like Graph refuses a credential it cannot read.
/// </summary>
public sealed partial class FakeDirectory
{
    /// <summary>How many <c>PATCH /applications/{id}</c> requests carried a <c>keyCredentials</c> collection.</summary>
    public int KeyCredentialsPatches { get; private set; }

    /// <summary>Seeds a certificate credential on an application, as Graph stores it.</summary>
    public JsonObject AddKeyCredential(string applicationId, string displayName, byte[] der, string usage = "Verify", string? keyId = null)
    {
        ArgumentNullException.ThrowIfNull(der);
        lock (_gate)
        {
            var app = LocateById(applicationId, out _) ?? throw new ArgumentException($"No application '{applicationId}'.", nameof(applicationId));
            var entry = NormalizeKeyCredential(new JsonObject
            {
                ["displayName"] = displayName,
                ["type"] = "AsymmetricX509Cert",
                ["usage"] = usage,
                ["key"] = Convert.ToBase64String(der),
                ["keyId"] = keyId,
            }, RetryHint.Default);
            if (app["keyCredentials"] is not JsonArray credentials)
            {
                credentials = [];
                app["keyCredentials"] = credentials;
            }

            credentials.Add(entry);
            return (JsonObject)entry.DeepClone();
        }
    }

    /// <summary>The PATCH body with every <c>keyCredentials</c> entry normalized the way Graph stores it; other bodies are returned unchanged.</summary>
    private JsonNode? PrepareKeyCredentialsPatch(string collectionPath, JsonNode? body, RetryHint hint)
    {
        if (!collectionPath.Equals("/applications", StringComparison.OrdinalIgnoreCase) || body is not JsonObject payload || payload["keyCredentials"] is not JsonArray entries)
        {
            return body;
        }

        KeyCredentialsPatches++;
        var normalized = new JsonArray();
        foreach (var item in entries)
        {
            if (item is not JsonObject entry)
            {
                throw BadRequest("Each keyCredentials entry must be an object.", hint);
            }

            normalized.Add(NormalizeKeyCredential((JsonObject)entry.DeepClone(), hint));
        }

        var copy = (JsonObject)payload.DeepClone();
        copy["keyCredentials"] = normalized;
        return copy;
    }

    private static JsonObject NormalizeKeyCredential(JsonObject entry, RetryHint hint)
    {
        if (entry["key"] is not JsonValue keyValue || !keyValue.TryGetValue<string>(out var key) || key.Length == 0)
        {
            throw BadRequest("Invalid value specified for property 'key' of resource 'KeyCredential'.", hint);
        }

        byte[] der;
        X509Certificate2 certificate;
        try
        {
            der = Convert.FromBase64String(key);
            certificate = X509CertificateLoader.LoadCertificate(der);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            throw BadRequest("Invalid certificate: Key value is invalid certificate.", hint);
        }

        using (certificate)
        {
            if (entry["keyId"] is null)
            {
                entry["keyId"] = Guid.NewGuid().ToString("D");
            }

#pragma warning disable CA5350 // The certificate thumbprint is SHA-1 by definition.
            entry["customKeyIdentifier"] ??= Convert.ToBase64String(SHA1.HashData(der));
#pragma warning restore CA5350
            entry["startDateTime"] ??= certificate.NotBefore.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            entry["endDateTime"] ??= certificate.NotAfter.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            entry["type"] ??= "AsymmetricX509Cert";
            entry["usage"] ??= "Verify";
            entry["displayName"] ??= certificate.Subject;
        }

        return entry;
    }
}
