using System;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using EArm.TestSupport;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>The pure certificate credential rules: parsing, thumbprints, comparison and the ensure-only replacement collection.</summary>
public sealed class KeyCredentialsRulesTests
{
    [Fact]
    public void DerAndPem_ParseToTheSameThumbprint()
    {
        byte[] der = TestCertificates.CreateDer();

        Assert.Null(KeyCredentialsRules.DescribeDesired(new JsonObject { ["key"] = TestCertificates.Base64(der) }, out var fromDer));
        Assert.Null(KeyCredentialsRules.DescribeDesired(new JsonObject { ["key"] = TestCertificates.Pem(der) }, out var fromPem));

        Assert.Equal(TestCertificates.Thumbprint(der), fromDer!.Thumbprint);
        Assert.Equal(fromDer.Thumbprint, fromPem!.Thumbprint);
        Assert.Equal(KeyCredentialsRules.AsymmetricX509Cert, fromDer.Type);
        Assert.Equal(KeyCredentialsRules.Verify, fromDer.Usage);
    }

    [Fact]
    public void APfx_IsRefused_BecauseItCarriesAPrivateKey()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=pfx", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(30));
        string pfx = Convert.ToBase64String(certificate.Export(X509ContentType.Pfx));

        string? problem = KeyCredentialsRules.DescribeDesired(new JsonObject { ["key"] = pfx }, out var desired);

        Assert.Null(desired);
        Assert.Contains("PFX/PKCS#12", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("type", "X509CertAndPassword", "not supported")]
    [InlineData("usage", "Encrypt", "not valid")]
    [InlineData("endDateTime", "soon", "ISO 8601")]
    public void UnsupportedAttributes_AreDescribed(string property, string value, string expected)
    {
        var properties = new JsonObject { ["key"] = TestCertificates.Base64(TestCertificates.CreateDer()), [property] = value };

        Assert.Contains(expected, KeyCredentialsRules.DescribeDesired(properties, out _), StringComparison.Ordinal);
    }

    [Fact]
    public void ReplaceCollection_KeepsEveryOtherEntry_AsRead()
    {
        var other = new JsonObject { ["keyId"] = "k1", ["displayName"] = "other", ["key"] = "AAAA", ["customKeyIdentifier"] = "BBBB" };
        var live = new JsonArray(other.DeepClone(), new JsonObject { ["keyId"] = "k2", ["displayName"] = "managed", ["key"] = "CCCC" });

        var replaced = KeyCredentialsRules.ReplaceCollection(live, "managed", new JsonObject { ["displayName"] = "managed", ["key"] = "DDDD" });
        var removed = KeyCredentialsRules.ReplaceCollection(live, "managed", null);

        Assert.Equal(2, replaced.Count);
        Assert.True(JsonNode.DeepEquals(other, replaced[0]));
        Assert.Equal("DDDD", replaced[1]!["key"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(other, Assert.Single(removed)));
    }

    [Fact]
    public void Compare_FindsOnlyDeclaredDifferences()
    {
        byte[] der = TestCertificates.CreateDer();
        KeyCredentialsRules.DescribeDesired(new JsonObject { ["key"] = TestCertificates.Base64(der) }, out var desired);
        var live = new KeyCredentialSummary("k", "n", TestCertificates.Thumbprint(der).ToLowerInvariant(), "AsymmetricX509Cert", "Verify", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1), true);

        Assert.Empty(KeyCredentialsRules.Compare(desired!, live));
        Assert.Equal(["usage"], KeyCredentialsRules.Compare(desired!, live with { Usage = "Sign" }).Select(d => d.Property).ToList());
        Assert.Equal(["thumbprint"], KeyCredentialsRules.Compare(desired!, live with { Thumbprint = "00" }).Select(d => d.Property).ToList());
    }
}
