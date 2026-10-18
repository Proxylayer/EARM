using System;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Graph;
using Xunit;

namespace EArm.Integration.Tests;

/// <summary>
/// Application certificate credentials (<c>applications/keyCredentials</c>) and directory extensions (<c>applications/extensionProperties</c>) against the real
/// tenant: create, NoChange, an unmanaged certificate kept across a replacement, an extension recreate (data type change) and deployment delete.
/// Skipped without <c>EARM_IT_TENANT</c>.
/// </summary>
[Collection(RealTenantCollection.Name)]
public sealed class ApplicationCertificateScenarioTests : IntegrationTestBase
{
    public ApplicationCertificateScenarioTests(IntegrationFixture fixture)
        : base(fixture)
    {
    }

    private static byte[] PublicCertificate(string commonName)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=" + commonName, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(30));
        return certificate.RawData;
    }

    private static string Thumbprint(byte[] der)
    {
#pragma warning disable CA5350 // The certificate thumbprint is SHA-1 by definition.
        return Convert.ToHexString(SHA1.HashData(der));
#pragma warning restore CA5350
    }

    [Fact]
    public async Task It_CertificatesAndExtensions_RoundTrip_KeepingUnmanagedCertificates()
    {
        Fixture.Require();
        byte[] first = PublicCertificate("earm-it-first");
        var run = Fixture.NewRun("certs", "app-certificates.entra.json").WithParameter("certificate", Convert.ToBase64String(first));
        string appName = Fixture.Name("certapp");
        string appResource = "/providers/Microsoft.Entra/applications/" + appName;

        // 1. Create: the application, its certificate and two extensions.
        var created = await Fixture.Cli.CreateAsync(run, Ct);
        AssertCreated(created, "create");
        Assert.Equal(Thumbprint(first), (string?)created.Json["outputs"]?["thumbprint"]?["value"]);
        var application = await ApplicationByNameAsync(appName, "id", "appId");
        Assert.NotNull(application);
        string appObjectId = application.RequireId();
        string appId = application.Text("appId")!;
        Assert.Equal("extension_" + appId.Replace("-", string.Empty, StringComparison.Ordinal) + "_itCostCenter", (string?)created.Json["outputs"]?["costCenter"]?["value"]);
        var extensions = await Graph.ListAsync(new GraphRequest($"/applications/{appObjectId}/extensionProperties").WithHint(RetryHint.ExpectReplicationLag), Ct);
        Assert.Equal(2, extensions.Count);

        // 2. A certificate added outside EARM (the whole collection, as Graph requires) is not EARM's: what-if stays NoChange.
        byte[] unmanaged = PublicCertificate("earm-it-unmanaged");
        var live = await Graph.GetObjectAsync(new GraphRequest("/applications/" + appObjectId).WithHint(RetryHint.ExpectReplicationLag).WithSelect("id", "keyCredentials"), Ct);
        var collection = (JsonArray)live!["keyCredentials"]!.DeepClone();
        collection.Add(new JsonObject { ["displayName"] = "it-unmanaged", ["type"] = "AsymmetricX509Cert", ["usage"] = "Verify", ["key"] = Convert.ToBase64String(unmanaged) });
        await Graph.PatchAsync(new GraphRequest("/applications/" + appObjectId).WithHint(RetryHint.ExpectReplicationLag), new JsonObject { ["keyCredentials"] = collection }, Ct);
        await AssertNoChangesEventuallyAsync(() => Fixture.Cli.WhatIfAsync(run, Ct, "--exit-code-on-changes"), "what-if after the unmanaged certificate");

        // 3. A new certificate replaces the managed one; the unmanaged one stays.
        byte[] second = PublicCertificate("earm-it-second");
        var rolled = run.WithParameter("certificate", Convert.ToBase64String(second));
        AssertChange(await Fixture.Cli.WhatIfAsync(rolled, Ct, "--exit-code-on-changes"), appResource + "/keyCredentials/it-signing", "Modify", "what-if of the replacement");
        AssertCreated(await Fixture.Cli.CreateAsync(rolled, Ct), "replacement");
        var after = await Graph.GetObjectAsync(new GraphRequest("/applications/" + appObjectId).WithHint(RetryHint.ExpectReplicationLag).WithSelect("id", "keyCredentials"), Ct);
        var credentials = after!["keyCredentials"]!.AsArray().OfType<JsonObject>().ToList();
        Assert.Contains(credentials, c => c.Text("displayName") == "it-unmanaged" && c.Text("key") == Convert.ToBase64String(unmanaged));
        var managed = Assert.Single(credentials, c => c.Text("displayName") == "it-signing");
        Assert.Equal(Convert.ToBase64String(second), managed.Text("key"));

        // 4. A changed data type recreates the extension (confirmed with -y, which the driver always passes).
        var retyped = rolled.WithParameter("extensionType", "Integer");
        AssertChange(await Fixture.Cli.WhatIfAsync(retyped, Ct, "--exit-code-on-changes"), appResource + "/extensionProperties/itCostCenter", "Modify", "what-if of the recreate");
        AssertCreated(await Fixture.Cli.CreateAsync(retyped, Ct), "recreate");
        await AssertNoChangesEventuallyAsync(() => Fixture.Cli.WhatIfAsync(retyped, Ct, "--exit-code-on-changes"), "what-if after the recreate");

        // 5. deployment delete removes the declared objects; the application goes with its credentials and extensions.
        var deleted = await Fixture.Cli.DeleteAllAsync(retyped, Ct);
        AssertExit(0, deleted, "deployment delete");
        Assert.Null(await ApplicationByNameAsync(appName, "id"));
    }
}
