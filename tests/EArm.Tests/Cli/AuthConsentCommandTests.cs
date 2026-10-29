using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Azure.Identity;
using EArm.Cli.Commands;
using EArm.Cli.Hosting;
using EArm.Engine.Permissions;
using EArm.Graph;
using EArm.Graph.Auth;
using Xunit;

namespace EArm.Tests.Cli;

/// <summary>
/// DES-0002 phase 3 at the command line: <c>auth login</c> with the default client, <c>--scopes</c> and <c>-f</c> (the scopes go to the fake identity
/// provider, the Key Vault consent is a second request as the same account), the china and <c>--auth default</c> refusals, <c>auth status</c> with the client
/// and the missing scopes of a template, and the consent-prompt decision of <see cref="ConsentPrompting"/>.
/// </summary>
public sealed class AuthConsentCommandTests
{
    private const string RecordJson = "{\"username\":\"alice@contoso.example\",\"authority\":\"login.microsoftonline.com\",\"homeAccountId\":\"oid.tid\",\"tenantId\":\"11111111-1111-1111-1111-111111111111\",\"clientId\":\"CLIENT\",\"version\":\"1.0\"}";

    private static AuthenticationRecord Record(string clientId)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(RecordJson.Replace("CLIENT", clientId, StringComparison.Ordinal)));
        return AuthenticationRecord.Deserialize(stream);
    }

    /// <summary>The real <c>LoginAsync</c> over a fake identity provider that records every interactive request.</summary>
    private sealed class RecordingLoginFactory : GraphAuthFactory
    {
        public RecordingLoginFactory(AuthRecordStore store)
            : base(store, cacheDirectories: [Path.Combine(Path.GetTempPath(), "earm-test-token-cache-" + Guid.NewGuid().ToString("N"))]) // a private, never-created directory: a logout here has nothing to delete
        {
        }

        public List<(GraphAuthOptions Options, TokenScopes Scopes, AuthenticationRecord? Account)> Requests { get; } = [];

        internal override Task<AuthenticationRecord> AuthenticateAsync(GraphAuthOptions options, TokenScopes scopes, AuthenticationRecord? account, bool deviceCode, Func<DeviceCodeInfo, CancellationToken, Task>? deviceCodeCallback, CancellationToken cancellationToken)
        {
            Requests.Add((options, scopes, account));
            return Task.FromResult(account ?? Record(options.ClientId!));
        }
    }

    private static IDisposable Use(CliSandbox sandbox, RecordingLoginFactory factory, GraphCloud? cloud = null)
    {
        return ServiceOverrides.UseAmbient(new ServiceOverrides
        {
            Handler = sandbox.Handler,
            Tokens = sandbox.Tokens,
            Delay = new NoDelay(),
            RecordStore = sandbox.Overrides.RecordStore,
            AuthFactory = factory,
            Cloud = cloud,
        });
    }

    private static string[] Orders(params string[] args)
    {
        return [.. args, "-f", TestPaths.Sample("orders-api.entra.json"), "-p", "@" + TestPaths.Sample("orders-api.parameters.json")];
    }

    // ---- login ----

    [Fact]
    public async Task Login_Plain_RequestsTheBaseSet_WithMicrosoftGraphCommandLineTools()
    {
        using var sandbox = new CliSandbox(nameof(Login_Plain_RequestsTheBaseSet_WithMicrosoftGraphCommandLineTools));
        var factory = new RecordingLoginFactory(sandbox.Overrides.RecordStore!);
        using var scope = Use(sandbox, factory);

        var table = await CliRunner.RunAsync("auth", "login");
        var json = await CliRunner.RunAsync("auth", "login", "-o", "json");

        Assert.Equal(0, table.ExitCode);
        Assert.Contains("to Microsoft Graph Command Line Tools (14d82eec-204b-4c2f-b7e8-296a70dab67e)", table.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Delegated scopes consented: User.Read", table.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("Organization.Read.All", table.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("shared by every tool", table.StandardOutput, StringComparison.Ordinal);
        Assert.All(factory.Requests, r => Assert.Equal(["https://graph.microsoft.com/User.Read"], r.Scopes.Scopes));
        Assert.All(factory.Requests, r => Assert.Equal(PublicClients.GraphCommandLineTools, r.Options.ClientId));
        Assert.Equal(0, json.ExitCode);
        Assert.Equal(PublicClients.GraphCommandLineTools, json.Json["clientId"]!.GetValue<string>());
        Assert.Equal("Microsoft Graph Command Line Tools", json.Json["clientName"]!.GetValue<string>());
        Assert.Empty(json.Json["keyVaultScopes"]!.AsArray());
        Assert.Equal(PublicClients.GraphCommandLineTools, new AuthRecordStore(sandbox.RecordPath).TryLoad()!.ClientId);
    }

    [Fact]
    public async Task Login_Scopes_AddToTheBaseSet_InAnySpelling()
    {
        using var sandbox = new CliSandbox(nameof(Login_Scopes_AddToTheBaseSet_InAnySpelling));
        var factory = new RecordingLoginFactory(sandbox.Overrides.RecordStore!);
        using var scope = Use(sandbox, factory);

        var result = await CliRunner.RunAsync("auth", "login", "--scopes", "Group.ReadWrite.All,Policy.Read.All", "https://graph.microsoft.com/Application.ReadWrite.All", "user.read", "-o", "json");

        Assert.Equal(0, result.ExitCode);
        var request = Assert.Single(factory.Requests);
        Assert.Equal(["User.Read", "Group.ReadWrite.All", "Policy.Read.All", "Application.ReadWrite.All"], request.Scopes.Names);
        Assert.Equal(4, result.Json["scopes"]!.AsArray().Count);
    }

    [Fact]
    public async Task Login_ScopesWithTheKeyVaultScope_ConsentItInASecondRequestAsTheSameAccount()
    {
        using var sandbox = new CliSandbox(nameof(Login_ScopesWithTheKeyVaultScope_ConsentItInASecondRequestAsTheSameAccount));
        var factory = new RecordingLoginFactory(sandbox.Overrides.RecordStore!);
        using var scope = Use(sandbox, factory);

        var result = await CliRunner.RunAsync("auth", "login", "--scopes", "https://vault.azure.net/user_impersonation");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(2, factory.Requests.Count);
        Assert.Equal(["https://vault.azure.net/user_impersonation"], factory.Requests[1].Scopes.Scopes);
        Assert.Equal("alice@contoso.example", factory.Requests[1].Account!.Username);
        Assert.Contains("Key Vault scopes consented: https://vault.azure.net/user_impersonation", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_ScopesWithTheResourceManagerScope_ConsentItInASecondRequestAsTheSameAccount()
    {
        using var sandbox = new CliSandbox(nameof(Login_ScopesWithTheResourceManagerScope_ConsentItInASecondRequestAsTheSameAccount));
        var factory = new RecordingLoginFactory(sandbox.Overrides.RecordStore!);
        using var scope = Use(sandbox, factory);

        var table = await CliRunner.RunAsync("auth", "login", "--scopes", "https://management.azure.com/user_impersonation");
        var tableRequests = factory.Requests.ToList();
        factory.Requests.Clear();
        var json = await CliRunner.RunAsync("auth", "login", "--scopes", "https://management.azure.com/user_impersonation", "-o", "json");

        Assert.Equal(0, table.ExitCode);
        Assert.Equal(2, tableRequests.Count);
        Assert.Equal(["https://graph.microsoft.com/User.Read"], tableRequests[0].Scopes.Scopes);
        Assert.Equal(["https://management.azure.com/user_impersonation"], tableRequests[1].Scopes.Scopes);
        Assert.Equal("alice@contoso.example", tableRequests[1].Account!.Username);
        Assert.Contains("Azure Resource Manager scopes consented: https://management.azure.com/user_impersonation", table.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("Key Vault scopes consented", table.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(0, json.ExitCode);
        Assert.Equal(["https://management.azure.com/user_impersonation"], json.Json["resourceManagerScopes"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Empty(json.Json["keyVaultScopes"]!.AsArray());
    }

    [Fact]
    public async Task Login_Plain_ReportsNoResourceManagerScopes()
    {
        using var sandbox = new CliSandbox(nameof(Login_Plain_ReportsNoResourceManagerScopes));
        var factory = new RecordingLoginFactory(sandbox.Overrides.RecordStore!);
        using var scope = Use(sandbox, factory);

        var table = await CliRunner.RunAsync("auth", "login");
        var json = await CliRunner.RunAsync("auth", "login", "-o", "json");

        Assert.Equal(0, table.ExitCode);
        Assert.DoesNotContain("Azure Resource Manager", table.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(0, json.ExitCode);
        Assert.Empty(json.Json["resourceManagerScopes"]!.AsArray());
        Assert.All(factory.Requests, r => Assert.Equal("https://graph.microsoft.com", r.Scopes.Resource));
    }

    [Fact]
    public async Task Login_ScopesWithTheVaultAndResourceManagerScopes_RequestsGraphThenKeyVaultThenResourceManager()
    {
        using var sandbox = new CliSandbox(nameof(Login_ScopesWithTheVaultAndResourceManagerScopes_RequestsGraphThenKeyVaultThenResourceManager));
        var factory = new RecordingLoginFactory(sandbox.Overrides.RecordStore!);
        using var scope = Use(sandbox, factory);

        var result = await CliRunner.RunAsync("auth", "login", "--scopes", "https://management.azure.com/user_impersonation,https://vault.azure.net/user_impersonation", "-o", "json");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["https://graph.microsoft.com", "https://vault.azure.net", "https://management.azure.com"], factory.Requests.Select(r => r.Scopes.Resource));
        Assert.Equal(["https://vault.azure.net/user_impersonation"], result.Json["keyVaultScopes"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(["https://management.azure.com/user_impersonation"], result.Json["resourceManagerScopes"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public async Task Login_AnotherHttpsScope_ExitsOne_AndTheMessageNamesTheResourceManagerScope()
    {
        using var sandbox = new CliSandbox(nameof(Login_AnotherHttpsScope_ExitsOne_AndTheMessageNamesTheResourceManagerScope));
        var factory = new RecordingLoginFactory(sandbox.Overrides.RecordStore!);
        using var scope = Use(sandbox, factory);

        var result = await CliRunner.RunAsync("auth", "login", "--scopes", "https://management.usgovcloudapi.net/user_impersonation");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("https://management.azure.com/user_impersonation", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(factory.Requests);
    }

    [Fact]
    public async Task Login_InChina_TheResourceManagerScopeIsChinas()
    {
        using var sandbox = new CliSandbox(nameof(Login_InChina_TheResourceManagerScopeIsChinas));
        var factory = new RecordingLoginFactory(sandbox.Overrides.RecordStore!);
        using var scope = Use(sandbox, factory, GraphCloud.China);

        var china = await CliRunner.RunAsync("auth", "login", "--client-id", "44444444-4444-4444-4444-444444444444", "--scopes", "https://management.chinacloudapi.cn/user_impersonation");
        var publicScope = await CliRunner.RunAsync("auth", "login", "--client-id", "44444444-4444-4444-4444-444444444444", "--scopes", "https://management.azure.com/user_impersonation");

        Assert.Equal(0, china.ExitCode);
        Assert.Equal(2, factory.Requests.Count);
        Assert.Equal(GraphCloud.China.ResourceManagerConsentScopes, factory.Requests[1].Scopes);
        Assert.Equal(1, publicScope.ExitCode);
        Assert.Contains("https://management.chinacloudapi.cn/user_impersonation", publicScope.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_Template_ConsentsTheRunScopes_AndTheVaultScopeWhenTheTemplateHasKeyVaultReferences()
    {
        using var sandbox = new CliSandbox(nameof(Login_Template_ConsentsTheRunScopes_AndTheVaultScopeWhenTheTemplateHasKeyVaultReferences));
        var factory = new RecordingLoginFactory(sandbox.Overrides.RecordStore!);
        using var scope = Use(sandbox, factory);

        var create = await CliRunner.RunAsync(Orders("auth", "login", "--scopes", "Mail.Send"));
        var createRequests = factory.Requests.ToList();
        factory.Requests.Clear();
        var whatIf = await CliRunner.RunAsync(Orders("auth", "login", "--verb", "whatif"));

        Assert.Equal(0, create.ExitCode);
        Assert.Equal(2, createRequests.Count);
        Assert.Contains("Policy.ReadWrite.ConditionalAccess", createRequests[0].Scopes.Names);
        Assert.Contains("Mail.Send", createRequests[0].Scopes.Names);
        Assert.Contains("User.Read", createRequests[0].Scopes.Names);
        Assert.DoesNotContain("Organization.Read.All", createRequests[0].Scopes.Names);
        Assert.Equal(GraphCloud.Public.KeyVaultConsentScopes, createRequests[1].Scopes);
        Assert.NotNull(createRequests[1].Account);
        Assert.Equal(0, whatIf.ExitCode);
        Assert.DoesNotContain(factory.Requests[0].Scopes.Names, n => n.Contains("ReadWrite", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Login_TemplateWithoutKeyVaultReferences_MakesOneRequest()
    {
        using var sandbox = new CliSandbox(nameof(Login_TemplateWithoutKeyVaultReferences_MakesOneRequest));
        var factory = new RecordingLoginFactory(sandbox.Overrides.RecordStore!);
        using var scope = Use(sandbox, factory);

        var result = await CliRunner.RunAsync("auth", "login", "-f", TestPaths.Sample("tenant-security.entra.json"));

        Assert.Equal(0, result.ExitCode);
        Assert.Single(factory.Requests);
    }

    [Fact]
    public async Task Login_ABrokenTemplate_ExitsOne_BeforeAnySignIn()
    {
        using var sandbox = new CliSandbox(nameof(Login_ABrokenTemplate_ExitsOne_BeforeAnySignIn));
        var factory = new RecordingLoginFactory(sandbox.Overrides.RecordStore!);
        using var scope = Use(sandbox, factory);

        var result = await CliRunner.RunAsync("auth", "login", "-f", Path.Combine(sandbox.TemplatesDir, "missing.entra.json"));

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(factory.Requests);
    }

    [Theory]
    [InlineData("https://evil.example/Group.ReadWrite.All")]
    [InlineData(".default")]
    [InlineData("offline_access")]
    public async Task Login_AScopeThatIsNotAGraphOrKeyVaultScope_ExitsOne(string bad)
    {
        using var sandbox = new CliSandbox(nameof(Login_AScopeThatIsNotAGraphOrKeyVaultScope_ExitsOne));
        var factory = new RecordingLoginFactory(sandbox.Overrides.RecordStore!);
        using var scope = Use(sandbox, factory);

        var result = await CliRunner.RunAsync("auth", "login", "--scopes", bad);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("--scopes", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(factory.Requests);
    }

    [Fact]
    public async Task Login_ScopesOrTemplate_WithAuthDefault_ExitOne_WithAClearMessage()
    {
        using var sandbox = new CliSandbox(nameof(Login_ScopesOrTemplate_WithAuthDefault_ExitOne_WithAClearMessage));
        var factory = new RecordingLoginFactory(sandbox.Overrides.RecordStore!);
        using var scope = Use(sandbox, factory);

        var scopes = await CliRunner.RunAsync("auth", "login", "--auth", "default", "--scopes", "Group.Read.All");
        var template = await CliRunner.RunAsync(Orders("auth", "login", "--auth", "default"));

        Assert.Equal(1, scopes.ExitCode);
        Assert.Contains("--scopes and -f consent delegated scopes", scopes.StandardError, StringComparison.Ordinal);
        Assert.Equal(1, template.ExitCode);
        Assert.Contains("--mode app", template.StandardError, StringComparison.Ordinal);
        Assert.Empty(factory.Requests);
    }

    [Fact]
    public async Task Login_InChina_WithoutClientId_ExitsOne_AskingForClientId_AndWithOneSignsIn()
    {
        using var sandbox = new CliSandbox(nameof(Login_InChina_WithoutClientId_ExitsOne_AskingForClientId_AndWithOneSignsIn));
        var factory = new RecordingLoginFactory(sandbox.Overrides.RecordStore!);
        using var scope = Use(sandbox, factory, GraphCloud.China);

        var without = await CliRunner.RunAsync("auth", "login");
        var with = await CliRunner.RunAsync("auth", "login", "--client-id", "44444444-4444-4444-4444-444444444444");

        Assert.Equal(1, without.ExitCode);
        Assert.Contains("--client-id", without.StandardError, StringComparison.Ordinal);
        Assert.Contains("21Vianet", without.StandardError, StringComparison.Ordinal);
        Assert.Equal(0, with.ExitCode);
        var request = Assert.Single(factory.Requests);
        Assert.Equal("https://microsoftgraph.chinacloudapi.cn", request.Scopes.Resource);
        Assert.Equal("44444444-4444-4444-4444-444444444444", request.Options.ClientId);
    }

    // ---- status ----

    [Fact]
    public async Task Status_ShowsTheClientWithItsName()
    {
        using var sandbox = new CliSandbox(nameof(Status_ShowsTheClientWithItsName));
        sandbox.Tokens.AppId = PublicClients.GraphCommandLineTools;

        var table = await CliRunner.RunAsync("auth", "status");
        var json = await CliRunner.RunAsync("auth", "status", "-o", "json");

        Assert.Equal(0, table.ExitCode);
        Assert.Contains("Client:        Microsoft Graph Command Line Tools (14d82eec-204b-4c2f-b7e8-296a70dab67e)", table.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(PublicClients.GraphCommandLineTools, json.Json["clientId"]!.GetValue<string>());
        Assert.Equal("Microsoft Graph Command Line Tools", json.Json["clientName"]!.GetValue<string>());
        Assert.Null(json.Json["missingScopes"]);
    }

    [Fact]
    public async Task Status_WithATemplate_ListsTheRequiredScopesTheTokenLacks()
    {
        using var sandbox = new CliSandbox(nameof(Status_WithATemplate_ListsTheRequiredScopesTheTokenLacks));
        sandbox.Tokens.AppId = PublicClients.GraphCommandLineTools;
        sandbox.Tokens.Scopes = ["User.Read", "organization.read.all"];

        var table = await CliRunner.RunAsync(Orders("auth", "status"));
        var json = await CliRunner.RunAsync(Orders("auth", "status", "--verb", "create", "-o", "json"));

        Assert.Equal(0, table.ExitCode);
        Assert.Contains("Missing:       ", table.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Policy.ReadWrite.ConditionalAccess", table.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("earm auth login --scopes", table.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Key Vault:", table.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(0, json.ExitCode);
        var missing = json.Json["missingScopes"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Contains("Policy.ReadWrite.ConditionalAccess", missing);
        Assert.DoesNotContain("User.Read", missing);
        Assert.DoesNotContain("Organization.Read.All", missing);
        Assert.True(json.Json["keyVaultNeeded"]!.GetValue<bool>());
        Assert.DoesNotContain("eyJ", table.StandardOutput + json.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_WithATemplate_ReportsThatResourceManagerIsNotNeeded()
    {
        using var sandbox = new CliSandbox(nameof(Status_WithATemplate_ReportsThatResourceManagerIsNotNeeded));
        sandbox.Tokens.AppId = PublicClients.GraphCommandLineTools;
        sandbox.Tokens.Scopes = ["User.Read"];

        var table = await CliRunner.RunAsync(Orders("auth", "status"));
        var json = await CliRunner.RunAsync(Orders("auth", "status", "-o", "json"));
        var plain = await CliRunner.RunAsync("auth", "status", "-o", "json");

        Assert.Equal(0, table.ExitCode);
        Assert.DoesNotContain("ARM:", table.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(0, json.ExitCode);
        Assert.False(json.Json["resourceManagerNeeded"]!.GetValue<bool>());
        Assert.True(json.Json["keyVaultNeeded"]!.GetValue<bool>());
        Assert.Null(plain.Json["resourceManagerNeeded"]);
    }

    [Fact]
    public async Task Status_WithATemplate_AndEveryScopeGranted_SaysNoneMissing()
    {
        using var sandbox = new CliSandbox(nameof(Status_WithATemplate_AndEveryScopeGranted_SaysNoneMissing));
        var required = await CliRunner.RunAsync("permissions", "-f", TestPaths.Sample("tenant-security.entra.json"), "--verb", "whatif", "-o", "json");
        sandbox.Tokens.Scopes = [.. required.Json["graphScopes"]!.AsArray().Select(n => n!.GetValue<string>())];

        var result = await CliRunner.RunAsync("auth", "status", "-f", TestPaths.Sample("tenant-security.entra.json"), "--verb", "whatif");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Missing:       (none)", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_WithATemplate_AndAnAppOnlyIdentity_ExitsOne()
    {
        using var sandbox = new CliSandbox(nameof(Status_WithATemplate_AndAnAppOnlyIdentity_ExitsOne));
        sandbox.Tokens.UserPrincipalName = null;
        sandbox.Tokens.AppId = "33333333-3333-3333-3333-333333333333";
        sandbox.Tokens.Scopes = [];
        sandbox.Tokens.Roles = ["Application.ReadWrite.All"];

        var appOnly = await CliRunner.RunAsync(Orders("auth", "status"));
        var authDefault = await CliRunner.RunAsync(Orders("auth", "status", "--auth", "default"));

        Assert.Equal(1, appOnly.ExitCode);
        Assert.Contains("delegated scopes do not apply", appOnly.StandardError, StringComparison.Ordinal);
        Assert.Equal(1, authDefault.ExitCode);
        Assert.Contains("--mode app", authDefault.StandardError, StringComparison.Ordinal);
    }

    // ---- LoginConsent.Build ----

    private static RunScopeSet TemplateScopes(bool needsKeyVault, bool needsResourceManager)
    {
        return new RunScopeSet(RunVerb.Create, [new RunScope("Group.ReadWrite.All", ["/g"], ["test"])], needsKeyVault, needsResourceManager);
    }

    [Fact]
    public void LoginConsent_TheResourceManagerScope_BecomesTheResourceManagerRequest()
    {
        var consent = LoginConsent.Build(GraphCloud.Public, ["https://management.azure.com/user_impersonation"], null);

        Assert.Null(consent.KeyVault);
        Assert.NotNull(consent.ResourceManager);
        Assert.Equal(GraphCloud.Public.ResourceManagerConsentScopes, consent.ResourceManager);
        Assert.Equal(["User.Read"], consent.Graph.Names);
        Assert.Equal([consent.Graph, consent.ResourceManager], consent.Requests);
    }

    [Fact]
    public void LoginConsent_TheResourceManagerScope_IsMatchedCaseInsensitively_AndOnce()
    {
        var consent = LoginConsent.Build(GraphCloud.Public, ["HTTPS://MANAGEMENT.AZURE.COM/user_impersonation https://management.azure.com/user_impersonation"], TemplateScopes(false, true));

        Assert.Single(consent.ResourceManager!.Scopes);
        Assert.Equal("https://management.azure.com", consent.ResourceManager.Resource);
    }

    [Theory]
    [InlineData("https://evil.example/user_impersonation")]
    [InlineData("https://management.azure.com.evil.example/user_impersonation")]
    [InlineData("https://management.chinacloudapi.cn/user_impersonation")]
    [InlineData("https://management.core.windows.net/user_impersonation")]
    public void LoginConsent_AnotherHttpsScope_Fails_AndTheMessageNamesTheResourceManagerScope(string bad)
    {
        var error = Assert.Throws<ArgumentException>(() => LoginConsent.Build(GraphCloud.Public, [bad], null));

        Assert.StartsWith("--scopes:", error.Message, StringComparison.Ordinal);
        Assert.Contains("https://management.azure.com/user_impersonation", error.Message, StringComparison.Ordinal);
        Assert.Contains("https://vault.azure.net/user_impersonation", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoginConsent_TheResourceManagerDotDefault_IsRefused()
    {
        var error = Assert.Throws<ArgumentException>(() => LoginConsent.Build(GraphCloud.Public, ["https://management.azure.com/.default"], null));

        Assert.StartsWith("--scopes:", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoginConsent_ATemplateThatNeedsResourceManager_AddsTheResourceManagerConsent()
    {
        var needs = LoginConsent.Build(GraphCloud.Public, [], TemplateScopes(false, true));
        var doesNot = LoginConsent.Build(GraphCloud.Public, [], TemplateScopes(false, false));

        Assert.Equal(GraphCloud.Public.ResourceManagerConsentScopes, needs.ResourceManager);
        Assert.Null(needs.KeyVault);
        Assert.Contains("Group.ReadWrite.All", needs.Graph.Names);
        Assert.Equal(2, needs.Requests.Count);
        Assert.Null(doesNot.ResourceManager);
        Assert.Equal([doesNot.Graph], doesNot.Requests);
    }

    [Fact]
    public void LoginConsent_Requests_AreOrderedGraphKeyVaultResourceManager()
    {
        var consent = LoginConsent.Build(GraphCloud.Public, ["https://management.azure.com/user_impersonation", "https://vault.azure.net/user_impersonation"], TemplateScopes(true, true));

        Assert.Equal(
            ["https://graph.microsoft.com", "https://vault.azure.net", "https://management.azure.com"],
            consent.Requests.Select(r => r.Resource));
        Assert.Equal(GraphCloud.Public.KeyVaultConsentScopes, consent.KeyVault);
        Assert.Equal(GraphCloud.Public.ResourceManagerConsentScopes, consent.ResourceManager);
    }

    [Theory]
    [InlineData("usgov", "https://management.usgovcloudapi.net")]
    [InlineData("usgovdod", "https://management.usgovcloudapi.net")]
    [InlineData("china", "https://management.chinacloudapi.cn")]
    public void LoginConsent_ASovereignCloud_UsesItsOwnResourceManagerOrigin(string key, string origin)
    {
        var cloud = GraphCloud.Parse(key);

        var fromTemplate = LoginConsent.Build(cloud, [], TemplateScopes(false, true));
        var fromScopes = LoginConsent.Build(cloud, [origin + "/user_impersonation"], null);
        var publicScope = Assert.Throws<ArgumentException>(() => LoginConsent.Build(cloud, ["https://management.azure.com/user_impersonation"], null));

        Assert.Equal(origin, fromTemplate.ResourceManager!.Resource);
        Assert.Equal([origin + "/user_impersonation"], fromTemplate.ResourceManager.Scopes);
        Assert.Equal(cloud.ResourceManagerConsentScopes, fromScopes.ResourceManager);
        Assert.Contains(origin + "/user_impersonation", publicScope.Message, StringComparison.Ordinal);
    }

    // ---- consent prompting decision (phase 4 seam) ----

    [Fact]
    public async Task ConsentPrompting_PromptsOnlyAtAnInteractiveTerminal_ForAStoredSignIn()
    {
        using var sandbox = new CliSandbox(nameof(ConsentPrompting_PromptsOnlyAtAnInteractiveTerminal_ForAStoredSignIn));
        var factory = new RecordingLoginFactory(sandbox.Overrides.RecordStore!);
        await factory.RecordStore.SaveAsync(Record(PublicClients.GraphCommandLineTools), TestContext.Current.CancellationToken);
        var saved = ConsentPrompting.TerminalIsInteractive;
        try
        {
            ConsentPrompting.TerminalIsInteractive = () => false;
            Assert.Null(ConsentPrompting.Create(factory, new GraphAuthOptions(), TextWriter.Null));

            ConsentPrompting.TerminalIsInteractive = () => true;
            Assert.Null(ConsentPrompting.Create(factory, new GraphAuthOptions { Mode = GraphAuthMode.Default }, TextWriter.Null));
            var stderr = new StringWriter();
            var prompter = ConsentPrompting.Create(factory, new GraphAuthOptions(), stderr);
            Assert.NotNull(prompter);

            await prompter.ConsentAsync(TokenScopes.Explicit("https://graph.microsoft.com", "Group.ReadWrite.All"), TestContext.Current.CancellationToken);

            var request = Assert.Single(factory.Requests);
            Assert.Equal("alice@contoso.example", request.Account!.Username);
            Assert.Contains("Group.ReadWrite.All", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            ConsentPrompting.TerminalIsInteractive = saved;
        }
    }
}
