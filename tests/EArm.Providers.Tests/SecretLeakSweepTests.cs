using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Providers.Tests.Infrastructure;
using EArm.TestSupport;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>
/// The secret-leak sweep: one test per provider runs what-if and create with a <c>securestring</c> parameter in
/// play and asserts the secret text appears in no request except the intended write body, no state document, no
/// what-if JSON / text, no change row, no diagnostic, no public output and no log line.
/// </summary>
public sealed class SecretLeakSweepTests
{
    private const string Secret = "S3cr3t-Sweep-Value-9f2a";
    private const string SecretParameter = "sweepSecret";
    private const string Reference = "[parameters('sweepSecret')]";
    private const string GroupId = "aaaaaaaa-1111-2222-3333-444444444444";

    private static TemplateDocumentFactory Factory(ProviderHost host)
    {
        return new TemplateDocumentFactory(host);
    }

    private readonly record struct TemplateDocumentFactory(ProviderHost Host)
    {
        public EArm.Core.TemplateDocument Make(string resources, string? outputs = null)
        {
            return Host.Resources(resources, outputs, Templates.SecureParameter(SecretParameter), (SecretParameter, Secret));
        }
    }

    private static bool IsWrite(FakeGraphRequest request)
    {
        return request.IsWrite;
    }

    private static async Task SweepAsync(ProviderHost host, string resources, int expectedIntendedBodies, string? outputs = null, bool createMayFail = false)
    {
        var document = Factory(host).Make(resources, outputs);

        var whatIf = await host.WhatIfAsync(document);
        var create = await host.CreateAsync(document);

        if (!createMayFail)
        {
            Check.Succeeded(create);
        }

        SecretSweep.AssertNoLeak(host, whatIf, create, Secret, IsWrite, expectedIntendedBodies);
        Assert.DoesNotContain(Secret, host.Logs.Lines.Aggregate(string.Empty, (a, b) => a + b), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Applications_SecretInNotes_ReachesOnlyTheUpsertBody()
    {
        var host = new ProviderHost();

        await SweepAsync(host, Templates.Resource("Microsoft.Entra/applications", "sweep-app", "{ \"displayName\": \"Sweep\", \"notes\": \"" + Reference + "\" }"), expectedIntendedBodies: 1);

        Assert.Equal(Secret, host.Directory.Find("/applications", "uniqueName", "sweep-app")!["notes"]!.GetValue<string>());
    }

    [Fact]
    public async Task ServicePrincipals_SecretInNotes_ReachesOnlyThePostBody()
    {
        var host = new ProviderHost();
        const string app = """{ "type": "Microsoft.Entra/applications", "apiVersion": "2026-10-01-preview", "name": "sweep-app", "properties": { "displayName": "Sweep" } }""";
        string sp = "{ \"type\": \"Microsoft.Entra/servicePrincipals\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"[reference('sweep-app').appId]\", \"properties\": { \"notes\": \"" + Reference + "\" }, \"dependsOn\": [\"[resourceId('Microsoft.Entra/applications', 'sweep-app')]\"] }";

        await SweepAsync(host, app + "," + sp, expectedIntendedBodies: 1);

        Assert.Equal(Secret, host.Directory.Objects("/servicePrincipals").Single()["notes"]!.GetValue<string>());
    }

    [Fact]
    public async Task Groups_SecretInDescription_ReachesOnlyTheUpsertBody()
    {
        var host = new ProviderHost();

        await SweepAsync(host, Templates.Resource("Microsoft.Entra/groups", "sweep-group", "{ \"displayName\": \"Sweep\", \"description\": \"" + Reference + "\" }"), expectedIntendedBodies: 1);

        Assert.Equal(Secret, host.Directory.Find("/groups", "uniqueName", "sweep-group")!["description"]!.GetValue<string>());
    }

    [Fact]
    public async Task FederatedIdentityCredentials_SecretInDescription_ReachesOnlyTheCredentialUpsert()
    {
        var host = new ProviderHost();
        const string app = """{ "type": "Microsoft.Entra/applications", "apiVersion": "2026-10-01-preview", "name": "sweep-app", "properties": { "displayName": "Sweep" } }""";
        string fic = "{ \"type\": \"Microsoft.Entra/applications/federatedIdentityCredentials\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"sweep-app/github-main\", \"properties\": { \"issuer\": \"https://token.actions.githubusercontent.com\", \"subject\": \"repo:x\", \"description\": \"" + Reference + "\" } }";

        await SweepAsync(host, app + "," + fic, expectedIntendedBodies: 1);

        string appId = host.IdOf("/applications", "uniqueName", "sweep-app");
        Assert.Equal(Secret, host.Directory.Objects("/applications/" + appId + "/federatedIdentityCredentials").Single()["description"]!.GetValue<string>());
    }

    [Fact]
    public async Task PasswordCredentials_TheGeneratedSecret_ReachesOnlyTheSecureOutput()
    {
        var host = new ProviderHost();
        const string app = """{ "type": "Microsoft.Entra/applications", "apiVersion": "2026-10-01-preview", "name": "sweep-app", "properties": { "displayName": "Sweep" } }""";
        const string credential = """{ "type": "Microsoft.Entra/applications/passwordCredentials", "apiVersion": "2026-10-01-preview", "name": "sweep-app/ci-secret", "properties": { } }""";
        const string outputs = """ "ciSecret": { "type": "securestring", "value": "[reference(resourceId('Microsoft.Entra/applications/passwordCredentials', 'sweep-app', 'ci-secret')).secretText]" } """;
        var document = host.Resources(app + "," + credential, outputs);

        var whatIf = await host.WhatIfAsync(document);
        var create = await host.CreateAsync(document);

        Check.Succeeded(create);
        string secret = create.SecureOutput("ciSecret")!.GetValue<string>();
        Assert.StartsWith("fake-secret-", secret, StringComparison.Ordinal);
        SecretSweep.AssertNoLeak(host, whatIf, create, secret, _ => false, expectedIntendedBodies: 0);
    }

    [Fact]
    public async Task PasswordCredentials_ASecureRotationTag_IsRejected_NotRecorded()
    {
        var host = new ProviderHost();
        const string app = """{ "type": "Microsoft.Entra/applications", "apiVersion": "2026-10-01-preview", "name": "sweep-app", "properties": { "displayName": "Sweep" } }""";
        string credential = "{ \"type\": \"Microsoft.Entra/applications/passwordCredentials\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"sweep-app/ci-secret\", \"properties\": { \"rotationTag\": \"" + Reference + "\" } }";

        await SweepAsync(host, app + "," + credential, expectedIntendedBodies: 0, createMayFail: true);
    }

    [Fact]
    public async Task Users_ThePasswordParameter_IsSentOnce_AndNowhereElse()
    {
        var host = new ProviderHost();

        await SweepAsync(host, Templates.Resource("Microsoft.Entra/users", "sweep@contoso.com", "{ \"displayName\": \"Sweep\", \"passwordProfile\": { \"password\": \"" + Reference + "\" } }"), expectedIntendedBodies: 1);

        var post = host.Fake.Writes.Single(w => w.Method == "POST" && w.Path == "/users");
        Assert.Equal(Secret, post.Body!["passwordProfile"]!["password"]!.GetValue<string>());
    }

    [Fact]
    public async Task NamedLocations_ASecretWhereACidrIsExpected_IsNeverEchoedInMessages()
    {
        var host = new ProviderHost();
        string location = "{ \"type\": \"Microsoft.Entra/conditionalAccess/namedLocations\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"Sweep offices\", \"properties\": { \"@odata.type\": \"#microsoft.graph.ipNamedLocation\", \"ipRanges\": [ { \"cidrAddress\": \"" + Reference + "\" } ] } }";

        await SweepAsync(host, location, expectedIntendedBodies: 0, createMayFail: true);
    }

    [Fact]
    public async Task NamedLocations_ASecretCountryCode_ReachesOnlyThePostBody()
    {
        var host = new ProviderHost();
        string location = "{ \"type\": \"Microsoft.Entra/conditionalAccess/namedLocations\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"Sweep countries\", \"properties\": { \"@odata.type\": \"#microsoft.graph.countryNamedLocation\", \"countriesAndRegions\": [ \"" + Reference + "\" ] } }";

        await SweepAsync(host, location, expectedIntendedBodies: 1, createMayFail: true);
    }

    [Fact]
    public async Task ConditionalAccessPolicies_ASecretGroupId_ReachesOnlyThePostBody()
    {
        var host = new ProviderHost();
        string policy = "{ \"type\": \"Microsoft.Entra/conditionalAccess/policies\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"CA-Sweep\", \"properties\": { \"conditions\": { \"users\": { \"includeGroups\": [\"" + Reference + "\"] }, \"applications\": { \"includeApplications\": [\"All\"] } }, \"grantControls\": { \"builtInControls\": [\"mfa\"] } } }";

        await SweepAsync(host, policy, expectedIntendedBodies: 1);
    }

    [Fact]
    public async Task AuthenticationContexts_SecretDescription_NeverLeaks()
    {
        var host = new ProviderHost();

        // Whether the provider accepts a secure string here (see the skipped test below) or rejects it, the text must not leak.
        await SweepAsync(host, Templates.Resource("Microsoft.Entra/conditionalAccess/authenticationContextClassReferences", "c10", "{ \"displayName\": \"Sweep\", \"description\": \"" + Reference + "\" }"), expectedIntendedBodies: -1, createMayFail: true);
    }

    [Fact]
    public async Task AuthenticationContexts_SecretDescription_IsAccepted_AndReachesOnlyTheUpsertBody()
    {
        var host = new ProviderHost();

        await SweepAsync(host, Templates.Resource("Microsoft.Entra/conditionalAccess/authenticationContextClassReferences", "c10", "{ \"displayName\": \"Sweep\", \"description\": \"" + Reference + "\" }"), expectedIntendedBodies: 1);
    }

    [Fact]
    public async Task AuthenticationStrengths_SecretDescription_ReachesOnlyThePostBody()
    {
        var host = new ProviderHost();

        await SweepAsync(host, Templates.Resource("Microsoft.Entra/conditionalAccess/authenticationStrengths", "Sweep strength", "{ \"displayName\": \"Sweep strength\", \"description\": \"" + Reference + "\", \"allowedCombinations\": [\"fido2\"] }"), expectedIntendedBodies: 1);
    }

    [Fact]
    public async Task Rerun_WithTheSecretUnchanged_OnlyEverSendsItInTheWriteBody()
    {
        var host = new ProviderHost();
        var document = Factory(host).Make(Templates.Resource("Microsoft.Entra/applications", "sweep-app", "{ \"displayName\": \"Sweep\", \"notes\": \"" + Reference + "\" }"));
        Check.Succeeded(await host.CreateAsync(document));
        host.ClearRequests();

        var rerun = await host.CreateAsync(document);

        // Secret-valued properties are never compared (JsonDiff), so the rerun re-PATCHes them: documented engine behaviour,
        // and the text still travels only in that body.
        Check.Succeeded(rerun);
        Assert.Equal("""{"notes":"S3cr3t-Sweep-Value-9f2a"}""", Assert.Single(rerun.Writes).Body!.ToJsonString());
        SecretSweep.AssertNoLeak(host, null, rerun, Secret, IsWrite, expectedIntendedBodies: 1);
    }

    [Fact]
    public async Task Rerun_TheChangeRows_NeverShowTheLiveValueOfASecretProperty()
    {
        var host = new ProviderHost();
        var document = Factory(host).Make(Templates.Resource("Microsoft.Entra/applications", "sweep-app", "{ \"displayName\": \"Sweep\", \"notes\": \"" + Reference + "\" }"));
        Check.Succeeded(await host.CreateAsync(document));
        host.ClearRequests();

        var rerun = await host.CreateAsync(document);
        var whatIf = await host.WhatIfAsync(document);

        SecretSweep.AssertNoLeak(host, whatIf, rerun, Secret, IsWrite, expectedIntendedBodies: 1);
        Assert.Contains("\"notes\":\"***\"", whatIf.Change("sweep-app").Before!.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rerun_ANestedSecret_MasksOnlyThatLeafInTheBeforeView()
    {
        var host = new ProviderHost();
        var document = Factory(host).Make(Templates.Resource("Microsoft.Entra/applications", "sweep-app", "{ \"displayName\": \"Sweep\", \"web\": { \"homePageUrl\": \"" + Reference + "\", \"redirectUris\": [\"https://visible.example/signin\"] } }"));
        Check.Succeeded(await host.CreateAsync(document));
        host.ClearRequests();

        var rerun = await host.CreateAsync(document);
        var whatIf = await host.WhatIfAsync(document);

        Check.Succeeded(rerun);
        SecretSweep.AssertNoLeak(host, whatIf, rerun, Secret, IsWrite, expectedIntendedBodies: 1);
        string before = whatIf.Change("sweep-app").Before!.ToJsonString();
        Assert.Contains("\"homePageUrl\":\"***\"", before, StringComparison.Ordinal);
        Assert.Contains("https://visible.example/signin", before, StringComparison.Ordinal);
        Assert.Contains("\"displayName\":\"Sweep\"", before, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnsupportedRows_WhereTheLiveValueEqualsTheSecret_MaskItInBefore()
    {
        var host = new ProviderHost();
        Check.Succeeded(await host.CreateAsync(Factory(host).Make(Templates.Resource("Microsoft.Entra/applications", "sweep-app", "{ \"displayName\": \"Sweep\", \"notes\": \"" + Reference + "\" }"))));
        host.ClearRequests();
        // An expression-valued uniqueName that differs from the name makes the row unsupported while the live notes equal the secret.
        var conflicting = Factory(host).Make(Templates.Resource("Microsoft.Entra/applications", "sweep-app", "{ \"displayName\": \"Sweep\", \"uniqueName\": \"[concat('other', '-name')]\", \"notes\": \"" + Reference + "\" }"));

        var whatIf = await host.WhatIfAsync(conflicting);
        var create = await host.CreateAsync(conflicting);

        var row = whatIf.Change("sweep-app");
        Assert.True(row.HasError);
        Assert.Contains("conflicts with the resource name", row.UnsupportedReason, StringComparison.Ordinal);
        Assert.Contains("\"notes\":\"***\"", row.Before!.ToJsonString(), StringComparison.Ordinal);
        Assert.False(create.Succeeded);
        SecretSweep.AssertNoLeak(host, whatIf, create, Secret, IsWrite, expectedIntendedBodies: -1);
        Check.NoWrites(create);
    }
}
