using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Engine;
using EArm.Engine.Execution;
using EArm.Engine.Providers;
using EArm.Providers.Tests.Infrastructure;
using EArm.TestSupport;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>The application certificate credentials provider end to end: create, rerun, replacement, what-if, delete, the ambiguous key and the unmanaged credentials it must keep.</summary>
public sealed class KeyCredentialsProviderTests
{
    private const string CertType = KeyCredentialsProvider.ResourceType;
    private const string AppName = "test-api";
    private const string CertName = "ci-signing";

    private static string App(string properties = "\"displayName\": \"Test API\"")
    {
        return "{ \"type\": \"Microsoft.Entra/applications\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"" + AppName + "\", \"properties\": { " + properties + " } }";
    }

    private static string Cert(string key, string extra = "", string name = CertName)
    {
        return "{ \"type\": \"" + CertType + "\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"" + AppName + "/" + name + "\", \"properties\": { \"key\": \"" + key + "\"" + extra + " } }";
    }

    private static string Template(byte[] der, string extra = "")
    {
        return App() + ", " + Cert(TestCertificates.Base64(der), extra);
    }

    private static JsonArray LiveCredentials(ProviderHost host)
    {
        return host.Directory.Find("/applications", "uniqueName", AppName)!["keyCredentials"] as JsonArray ?? [];
    }

    private static FakeGraphRequest KeyCredentialsPatch(Run run)
    {
        return Assert.Single(run.Writes, w => w.Method == "PATCH" && w.Body is JsonObject body && body.ContainsKey("keyCredentials"));
    }

    [Fact]
    public void Discovery_RegistersTheProvider()
    {
        var registry = ProviderDiscovery.CreateRegistry(EArm.Core.Types.ResourceTypeCatalog.Default);
        Assert.IsType<KeyCredentialsProvider>(registry.Get(CertType));
    }

    [Fact]
    public async Task Create_PatchesTheCollection_WithThePublicCertificate_AndPublishesTheThumbprint()
    {
        var host = new ProviderHost();
        byte[] der = TestCertificates.CreateDer("ci");
        const string Outputs = "\"thumbprint\": { \"type\": \"string\", \"value\": \"[reference(resourceId('Microsoft.Entra/applications/keyCredentials', 'test-api', 'ci-signing')).thumbprint]\" }";

        var run = await host.CreateAsync(host.Resources(Template(der), Outputs));

        Check.Succeeded(run);
        string appId = host.IdOf("/applications", "uniqueName", AppName);
        var patch = KeyCredentialsPatch(run);
        Assert.Equal("/applications/" + appId, patch.Path);
        var entry = Assert.IsType<JsonObject>(Assert.Single(Check.Body(patch)["keyCredentials"]!.AsArray()));
        Assert.Equal(["displayName", "type", "usage", "key"], entry.Select(p => p.Key).ToList());
        Assert.Equal(CertName, entry["displayName"]!.GetValue<string>());
        Assert.Equal("AsymmetricX509Cert", entry["type"]!.GetValue<string>());
        Assert.Equal("Verify", entry["usage"]!.GetValue<string>());
        Assert.Equal(TestCertificates.Base64(der), entry["key"]!.GetValue<string>());
        var live = Assert.IsType<JsonObject>(Assert.Single(LiveCredentials(host)));
        Assert.Equal(live["keyId"]!.GetValue<string>(), run.Op(CertName).GraphId);
        Assert.Equal(TestCertificates.Thumbprint(der), run.Output("thumbprint")!.GetValue<string>());
        Assert.Contains(run.Requests, r => r.Method == "GET" && r.Path == "/applications/" + appId && r.UrlParameters!["$select"] == "id,keyCredentials");
    }

    [Fact]
    public async Task Pem_IsAccepted_AndMatchesTheSameCertificateInDer()
    {
        var host = new ProviderHost();
        byte[] der = TestCertificates.CreateDer("pem");
        string pem = TestCertificates.Pem(der).Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
        Check.Succeeded(await host.CreateAsync(host.Resources(App() + ", " + Cert(pem))));

        var rerun = await host.CreateAsync(host.Resources(Template(der)));

        Check.Succeeded(rerun);
        Assert.Equal(ChangeType.NoChange, rerun.Op(CertName).ChangeType);
        Check.NoWrites(rerun);
    }

    [Fact]
    public async Task Rerun_IsNoChange_WithoutWrites()
    {
        var host = new ProviderHost();
        byte[] der = TestCertificates.CreateDer();
        Check.Succeeded(await host.CreateAsync(host.Resources(Template(der))));

        var rerun = await host.CreateAsync(host.Resources(Template(der)));

        Check.Succeeded(rerun);
        Assert.All(rerun.Executed.Operations, o => Assert.Equal(ChangeType.NoChange, o.ChangeType));
        Check.NoWrites(rerun);
    }

    [Fact]
    public async Task NewCertificate_ReplacesTheManagedOne_InOnePatch_AndKeepsTheUnmanagedOnes()
    {
        var host = new ProviderHost();
        var app = host.Directory.AddApplication(AppName, "Test API");
        string appId = app["id"]!.GetValue<string>();
        byte[] unmanaged = TestCertificates.CreateDer("portal-upload");
        var kept = host.Directory.AddKeyCredential(appId, "CN=portal-upload", unmanaged);
        byte[] first = TestCertificates.CreateDer("first");
        Check.Succeeded(await host.CreateAsync(host.Resources(Template(first))));
        byte[] second = TestCertificates.CreateDer("second");

        var run = await host.CreateAsync(host.Resources(Template(second)));

        Check.Succeeded(run);
        var op = run.Op(CertName);
        Assert.Equal(ChangeType.Modify, op.ChangeType);
        var change = run.Change(CertName);
        var delta = Assert.Single(change.Deltas);
        Assert.Equal("properties.thumbprint", delta.Path.ToString());
        Assert.Equal(TestCertificates.Thumbprint(first), delta.Before!.GetValue<string>());
        Assert.Equal(TestCertificates.Thumbprint(second), delta.After!.GetValue<string>());
        var sent = Check.Body(KeyCredentialsPatch(run))["keyCredentials"]!.AsArray();
        Assert.Equal(2, sent.Count);
        var sentKept = Assert.Single(sent, e => e!["displayName"]!.GetValue<string>() == "CN=portal-upload")!;
        Assert.Equal(kept["keyId"]!.GetValue<string>(), sentKept["keyId"]!.GetValue<string>());
        Assert.Equal(TestCertificates.Base64(unmanaged), sentKept["key"]!.GetValue<string>());
        var live = LiveCredentials(host);
        Assert.Equal(2, live.Count);
        Assert.Contains(live, e => e!["keyId"]!.GetValue<string>() == kept["keyId"]!.GetValue<string>());
        var managed = Assert.Single(live, e => e!["displayName"]!.GetValue<string>() == CertName)!;
        Assert.Equal(TestCertificates.Base64(second), managed["key"]!.GetValue<string>());
        Assert.Equal(managed["keyId"]!.GetValue<string>(), op.GraphId);
    }

    [Fact]
    public async Task Create_KeepsAnUnmanagedCertificate()
    {
        var host = new ProviderHost();
        var app = host.Directory.AddApplication(AppName, "Test API");
        byte[] unmanaged = TestCertificates.CreateDer("legacy");
        var kept = host.Directory.AddKeyCredential(app["id"]!.GetValue<string>(), "legacy", unmanaged);

        var run = await host.CreateAsync(host.Resources(Template(TestCertificates.CreateDer("new"))));

        Check.Succeeded(run);
        Assert.Equal(ChangeType.Create, run.Op(CertName).ChangeType);
        var live = LiveCredentials(host);
        Assert.Equal(2, live.Count);
        var legacy = Assert.Single(live, e => e!["displayName"]!.GetValue<string>() == "legacy")!;
        Assert.Equal(kept["keyId"]!.GetValue<string>(), legacy["keyId"]!.GetValue<string>());
        Assert.Equal(TestCertificates.Base64(unmanaged), legacy["key"]!.GetValue<string>());
    }

    [Fact]
    public async Task DeclaredDates_AreCompared_AndAChangeIsAReplacement()
    {
        var host = new ProviderHost();
        byte[] der = TestCertificates.CreateDer();
        Check.Succeeded(await host.CreateAsync(host.Resources(Template(der, ", \"endDateTime\": \"2027-01-01T00:00:00Z\""))));

        var same = await host.WhatIfAsync(host.Resources(Template(der, ", \"endDateTime\": \"2027-01-01T00:00:00+00:00\"")));
        var changed = await host.WhatIfAsync(host.Resources(Template(der, ", \"startDateTime\": \"2026-01-01T00:00:00Z\", \"endDateTime\": \"2027-02-01T00:00:00Z\"")));

        Assert.Equal(ChangeType.NoChange, same.ChangeOfType(CertType).Type);
        var row = changed.ChangeOfType(CertType);
        Assert.Equal(ChangeType.Modify, row.Type);
        Assert.Equal(["properties.startDateTime", "properties.endDateTime"], row.Deltas.Select(d => d.Path.ToString()).ToList());
        Check.NoWrites(changed);
    }

    [Fact]
    public async Task SignUsage_IsRefused_BecauseItNeedsThePrivateKey()
    {
        var host = new ProviderHost();

        var run = await host.WhatIfAsync(host.Resources(Template(TestCertificates.CreateDer(), ", \"usage\": \"Sign\"")));

        var row = run.ChangeOfType(CertType);
        Assert.True(row.HasError);
        Assert.Contains("needs the private key", row.UnsupportedReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhatIf_ShowsTheThumbprint_AndWritesNothing()
    {
        var host = new ProviderHost();
        byte[] der = TestCertificates.CreateDer();

        var run = await host.WhatIfAsync(host.Resources(Template(der)));

        Check.NoWrites(run);
        var row = run.ChangeOfType(CertType);
        Assert.Equal(ChangeType.Create, row.Type);
        Assert.Null(row.UnsupportedReason);
        Assert.Equal(TestCertificates.Thumbprint(der), row.After!["thumbprint"]!.GetValue<string>());
        Assert.DoesNotContain(TestCertificates.Base64(der), run.WhatIfTextOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeploymentDelete_RemovesOnlyTheManagedCertificate()
    {
        var host = new ProviderHost();
        var app = host.Directory.AddApplication(AppName, "Test API");
        string appId = app["id"]!.GetValue<string>();
        var kept = host.Directory.AddKeyCredential(appId, "legacy", TestCertificates.CreateDer("legacy"));
        byte[] der = TestCertificates.CreateDer();
        Check.Succeeded(await host.CreateAsync(host.Resources(Template(der))));
        host.ClearRequests();

        var deletion = await host.DeleteDeclaredAsync(host.Resources(Cert(TestCertificates.Base64(der))));

        Assert.True(deletion.Succeeded, string.Join("; ", deletion.Outcomes.Select(o => o.Error)));
        var write = Assert.Single(host.Fake.Writes);
        Assert.Equal("PATCH", write.Method);
        Assert.Equal("/applications/" + appId, write.Path);
        var remaining = Assert.Single(LiveCredentials(host))!;
        Assert.Equal(kept["keyId"]!.GetValue<string>(), remaining["keyId"]!.GetValue<string>());
    }

    [Fact]
    public async Task TwoLiveCredentialsWithTheName_AreAmbiguous_AndNothingIsWritten()
    {
        var host = new ProviderHost();
        var app = host.Directory.AddApplication(AppName, "Test API");
        string appId = app["id"]!.GetValue<string>();
        host.Directory.AddKeyCredential(appId, CertName, TestCertificates.CreateDer("a"));
        host.Directory.AddKeyCredential(appId, CertName, TestCertificates.CreateDer("b"));
        var document = host.Resources(Template(TestCertificates.CreateDer("c")));

        var whatIf = await host.WhatIfAsync(document);
        var row = whatIf.ChangeOfType(CertType);
        Assert.True(row.HasError);
        Assert.Contains("more than one certificate credential named 'ci-signing'", row.UnsupportedReason, StringComparison.Ordinal);
        Assert.Contains(EngineDiagnosticCodes.AmbiguousMatch, row.UnsupportedReason, StringComparison.Ordinal);

        var run = await host.CreateAsync(document);
        Assert.Equal(DeploymentStatus.Failed, run.Executed.Status);
        Assert.DoesNotContain(run.Writes, w => w.Body is JsonObject body && body.ContainsKey("keyCredentials"));
        Assert.Equal(2, LiveCredentials(host).Count);
    }

    [Theory]
    [InlineData("-----BEGIN PRIVATE KEY-----\\nMIIB\\n-----END PRIVATE KEY-----", "private key")]
    [InlineData("bm90IGEgY2VydGlmaWNhdGU=", "not an X.509 certificate")]
    [InlineData("%%%", "neither base64 DER nor a PEM")]
    public async Task AnInvalidKey_IsAnUnsupportedRow(string key, string expected)
    {
        var host = new ProviderHost();

        var run = await host.WhatIfAsync(host.Resources(App() + ", " + Cert(key)));

        var row = run.ChangeOfType(CertType);
        Assert.True(row.HasError);
        Assert.Contains(expected, row.UnsupportedReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AParentDeclaringKeyCredentials_IsRefused()
    {
        var host = new ProviderHost();
        string key = TestCertificates.Base64(TestCertificates.CreateDer("app-level"));
        string document = App("\"displayName\": \"Test API\", \"keyCredentials\": [ { \"type\": \"AsymmetricX509Cert\", \"usage\": \"Verify\", \"key\": \"" + key + "\" } ]") + ", " + Cert(TestCertificates.Base64(TestCertificates.CreateDer()));

        var run = await host.WhatIfAsync(host.Resources(document));

        var row = run.ChangeOfType(CertType);
        Assert.True(row.HasError);
        Assert.Contains("also declares properties.keyCredentials", row.UnsupportedReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnmanagedCredentialWithoutItsKey_BlocksTheWrite()
    {
        var host = new ProviderHost();
        var app = host.Directory.AddApplication(AppName, "Test API");
        app["keyCredentials"] = new JsonArray(new JsonObject { ["keyId"] = Guid.NewGuid().ToString("D"), ["displayName"] = "opaque", ["type"] = "AsymmetricX509Cert", ["usage"] = "Verify" });

        var run = await host.CreateAsync(host.Resources(Template(TestCertificates.CreateDer())));

        Assert.Equal(DeploymentStatus.Failed, run.Executed.Status);
        Assert.Contains("without their certificate bytes", run.Errors, StringComparison.Ordinal);
        Assert.DoesNotContain(run.Writes, w => w.Body is JsonObject body && body.ContainsKey("keyCredentials"));
    }
}
