using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace EArm.TestSupport;

/// <summary>Self-signed public certificates for the certificate credential tests: only the DER bytes leave this class, never a private key.</summary>
public static class TestCertificates
{
    /// <summary>The DER bytes of a fresh self-signed certificate with subject <c>CN=<paramref name="commonName"/></c>, valid for <paramref name="days"/> days.</summary>
    public static byte[] CreateDer(string commonName = "earm-test", int days = 365)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=" + commonName, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var now = DateTimeOffset.UtcNow;
        using var certificate = request.CreateSelfSigned(now.AddMinutes(-5), now.AddDays(days));
        return certificate.RawData;
    }

    /// <summary>The base64 DER form (a <c>.cer</c> file through <c>Convert.ToBase64String</c>).</summary>
    public static string Base64(byte[] der)
    {
        ArgumentNullException.ThrowIfNull(der);
        return Convert.ToBase64String(der);
    }

    /// <summary>The PEM form (<c>-----BEGIN CERTIFICATE-----</c>).</summary>
    public static string Pem(byte[] der)
    {
        ArgumentNullException.ThrowIfNull(der);
        return PemEncoding.WriteString("CERTIFICATE", der);
    }

    /// <summary>The SHA-1 thumbprint, upper-case hex.</summary>
    public static string Thumbprint(byte[] der)
    {
        ArgumentNullException.ThrowIfNull(der);
#pragma warning disable CA5350 // The certificate thumbprint is SHA-1 by definition.
        return Convert.ToHexString(SHA1.HashData(der));
#pragma warning restore CA5350
    }
}
