using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Identity;
using EArm.Cli.Hosting;
using EArm.Graph;
using EArm.Graph.Auth;
using EArm.TestSupport;
using Xunit;

namespace EArm.Tests.Cli;

/// <summary><c>earm auth login | logout | status</c> and the global sign-in options, driven in-process with fake credentials (no browser, no network).</summary>
[Collection(ProcessStateCollection.Name)]
public sealed class AuthCommandTests
{
    private const string RecordJson = "{\"username\":\"alice@contoso.example\",\"authority\":\"login.microsoftonline.com\",\"homeAccountId\":\"oid.tid\",\"tenantId\":\"11111111-1111-1111-1111-111111111111\",\"clientId\":\"04b07795-8ddb-461a-bbee-02f9e1bf7b46\",\"version\":\"1.0\"}";

    private static AuthenticationRecord Record(string? clientId = null)
    {
        var json = clientId is null ? RecordJson : RecordJson.Replace(PublicClients.AzureCli, clientId, StringComparison.Ordinal);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return AuthenticationRecord.Deserialize(stream);
    }

    /// <summary>A factory whose login "succeeds" without a browser and records how it was called.</summary>
    private sealed class FakeLoginFactory : GraphAuthFactory
    {
        public FakeLoginFactory(AuthRecordStore store, string cacheDirectory)
            : base(store, cacheDirectories: [cacheDirectory])
        {
        }

        public bool? DeviceCode { get; private set; }

        public GraphAuthOptions? Options { get; private set; }

        public Exception? Failure { get; set; }

        public bool? CallbackSupplied { get; private set; }

        /// <summary>Every interactive request: the scopes and, for a consent after the sign-in, the account it was made as.</summary>
        public System.Collections.Generic.List<(TokenScopes Scopes, AuthenticationRecord? Account)> Requests { get; } = [];

        internal override Task<AuthenticationRecord> AuthenticateAsync(GraphAuthOptions options, TokenScopes scopes, AuthenticationRecord? account, bool deviceCode, Func<DeviceCodeInfo, CancellationToken, Task>? deviceCodeCallback, CancellationToken cancellationToken)
        {
            DeviceCode = deviceCode;
            Options ??= options;
            Requests.Add((scopes, account));
            if (Failure is not null)
            {
                throw Failure;
            }

            CallbackSupplied = deviceCodeCallback is not null;
            return Task.FromResult(account ?? Record(options.ClientId));
        }
    }

    private static CliSandbox Sandbox(string name, out FakeLoginFactory factory, out string cacheDirectory)
    {
        var sandbox = new CliSandbox(name);
        cacheDirectory = Path.Combine(sandbox.Root, "cache");
        Directory.CreateDirectory(cacheDirectory);
        factory = new FakeLoginFactory(sandbox.Overrides.RecordStore!, cacheDirectory);
        return sandbox;
    }

    private static IDisposable UseFactory(CliSandbox sandbox, FakeLoginFactory factory)
    {
        return ServiceOverrides.UseAmbient(new ServiceOverrides
        {
            Handler = sandbox.Handler,
            Tokens = sandbox.Tokens,
            Delay = new NoDelay(),
            RecordStore = sandbox.Overrides.RecordStore,
            AuthFactory = factory,
        });
    }

    // ---- status ----

    [Fact]
    public async Task Status_ShowsTenantPrincipalAndScopes_AndNeverThePrintedToken()
    {
        using var sandbox = new CliSandbox(nameof(Status_ShowsTenantPrincipalAndScopes_AndNeverThePrintedToken));
        sandbox.Tokens.Scopes = ["Application.ReadWrite.All", "Group.ReadWrite.All"];

        var table = await CliRunner.RunAsync("auth", "status");
        var json = await CliRunner.RunAsync("auth", "status", "-o", "json");

        Assert.Equal(0, table.ExitCode);
        Assert.Contains("Tenant:        " + CliSandbox.Tenant, table.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("alice@contoso.example (user)", table.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Scopes:        Application.ReadWrite.All Group.ReadWrite.All", table.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Mode:          auto", table.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("fake-signature", table.StandardOutput + json.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("eyJ", table.StandardOutput + json.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(0, json.ExitCode);
        Assert.Equal(CliSandbox.Tenant, json.Json["tenantId"]!.GetValue<string>());
        Assert.Equal("user", json.Json["principalType"]!.GetValue<string>());
        Assert.Equal("public", json.Json["cloud"]!.GetValue<string>());
        Assert.Equal(2, json.Json["scopes"]!.AsArray().Count);
        Assert.Empty(sandbox.RecordedCommands());
        Assert.Equal(["https://graph.microsoft.com/.default", "https://graph.microsoft.com/.default"], sandbox.Tokens.RequestedScopes);
    }

    [Fact]
    public async Task Status_OfAnAppOnlyToken_ShowsTheAppIdAndRoles()
    {
        using var sandbox = new CliSandbox(nameof(Status_OfAnAppOnlyToken_ShowsTheAppIdAndRoles));
        sandbox.Tokens.UserPrincipalName = null;
        sandbox.Tokens.AppId = "33333333-3333-3333-3333-333333333333";
        sandbox.Tokens.Scopes = [];
        sandbox.Tokens.Roles = ["Application.ReadWrite.All"];

        var result = await CliRunner.RunAsync("auth", "status", "-o", "json");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("application", result.Json["principalType"]!.GetValue<string>());
        Assert.Equal("33333333-3333-3333-3333-333333333333", result.Json["principal"]!.GetValue<string>());
        Assert.Equal("Application.ReadWrite.All", result.Json["roles"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task Status_WithoutAnyCredential_ExitsThree_WithTheGuidance_AndNoStdout()
    {
        using var sandbox = new CliSandbox(nameof(Status_WithoutAnyCredential_ExitsThree_WithTheGuidance_AndNoStdout));
        sandbox.UseNotLoggedIn();

        var result = await CliRunner.RunAsync("auth", "status");

        Assert.Equal(3, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Contains("Not signed in", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("AZURE_CLIENT_SECRET", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("earm auth login", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_WithTheRealDefaultChainAndNoCredentials_ExitsThree_AndNamesOnlyProcessFreeCredentials()
    {
        // No overrides at all: the genuine factory and chain. The environment of the test host may carry AZURE_* variables, so clear them for the run.
        string[] names = ["AZURE_TENANT_ID", "AZURE_CLIENT_ID", "AZURE_CLIENT_SECRET", "AZURE_CLIENT_CERTIFICATE_PATH", "AZURE_FEDERATED_TOKEN_FILE", "AZURE_USERNAME", "AZURE_PASSWORD", "AZURE_TOKEN_CREDENTIALS", "IDENTITY_ENDPOINT", "MSI_ENDPOINT", "EARM_AUTH"];
        var saved = names.ToDictionary(static n => n, static n => Environment.GetEnvironmentVariable(n));
        string root = Path.Combine(Path.GetTempPath(), "earm-auth-cli-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var name in names)
            {
                Environment.SetEnvironmentVariable(name, null);
            }

            using var scope = ServiceOverrides.UseAmbient(new ServiceOverrides { RecordStore = new AuthRecordStore(Path.Combine(root, "record.json")) });

            var result = await CliRunner.RunAsync("auth", "status", "--auth", "default");

            Assert.Equal(3, result.ExitCode);
            Assert.Contains("EnvironmentCredential", result.StandardError, StringComparison.Ordinal);
            Assert.DoesNotContain("AzureCliCredential", result.StandardError, StringComparison.Ordinal);
            Assert.DoesNotContain("AzurePowerShellCredential", result.StandardError, StringComparison.Ordinal);
            Assert.DoesNotContain("VisualStudioCredential", result.StandardError, StringComparison.Ordinal);
        }
        finally
        {
            foreach (var pair in saved)
            {
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task AnUnsafeTokenCredentialsVariable_ExitsThree_WithoutStartingAnything()
    {
        using var sandbox = new CliSandbox(nameof(AnUnsafeTokenCredentialsVariable_ExitsThree_WithoutStartingAnything));
        string? saved = Environment.GetEnvironmentVariable("AZURE_TOKEN_CREDENTIALS");
        try
        {
            Environment.SetEnvironmentVariable("AZURE_TOKEN_CREDENTIALS", "AzureCliCredential");
            using var scope = ServiceOverrides.UseAmbient(new ServiceOverrides { RecordStore = sandbox.Overrides.RecordStore });

            var result = await CliRunner.RunAsync("auth", "status", "--auth", "default");

            Assert.Equal(3, result.ExitCode);
            Assert.Contains("external process", result.StandardError, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AZURE_TOKEN_CREDENTIALS", saved);
        }
    }

    // ---- login ----

    [Fact]
    public async Task Login_Browser_StoresTheRecord_AndReportsTheAccount()
    {
        using var sandbox = Sandbox(nameof(Login_Browser_StoresTheRecord_AndReportsTheAccount), out var factory, out _);
        using var scope = UseFactory(sandbox, factory);

        var table = await CliRunner.RunAsync("auth", "login", "--tenant", "contoso.example", "--client-id", "44444444-4444-4444-4444-444444444444");

        Assert.Equal(0, table.ExitCode);
        Assert.Contains("Signed in as alice@contoso.example (tenant " + CliSandbox.Tenant + ")", table.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(sandbox.RecordPath, table.StandardOutput, StringComparison.Ordinal);
        Assert.False(factory.DeviceCode);
        Assert.Equal("contoso.example", factory.Options!.TenantId);
        Assert.Equal("44444444-4444-4444-4444-444444444444", factory.Options.ClientId);
        Assert.True(File.Exists(sandbox.RecordPath));
        Assert.DoesNotContain("token", await File.ReadAllTextAsync(sandbox.RecordPath, TestContext.Current.CancellationToken), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Login_DeviceCode_PassesTheFlag_AndAnInstructionsCallback()
    {
        using var sandbox = Sandbox(nameof(Login_DeviceCode_PassesTheFlag_AndAnInstructionsCallback), out var factory, out _);
        using var scope = UseFactory(sandbox, factory);

        var result = await CliRunner.RunAsync("auth", "login", "--device-code", "-o", "json");

        Assert.Equal(0, result.ExitCode);
        Assert.True(factory.DeviceCode);
        Assert.True(factory.CallbackSupplied);
        Assert.Equal("alice@contoso.example", result.Json["account"]!.GetValue<string>());
    }

    [Fact]
    public async Task Login_Failure_ExitsThree_WithTheMessage()
    {
        using var sandbox = Sandbox(nameof(Login_Failure_ExitsThree_WithTheMessage), out var factory, out _);
        factory.Failure = new GraphTransportException(GraphTransportFailure.NotAuthenticated, "Sign-in failed: this machine cannot provide secure (encrypted) token storage.");
        using var scope = UseFactory(sandbox, factory);

        var result = await CliRunner.RunAsync("auth", "login");

        Assert.Equal(3, result.ExitCode);
        Assert.Contains("secure (encrypted) token storage", result.StandardError, StringComparison.Ordinal);
        Assert.False(File.Exists(sandbox.RecordPath));
    }

    [Fact]
    public async Task Login_WithABadClientId_ExitsOne()
    {
        using var sandbox = Sandbox(nameof(Login_WithABadClientId_ExitsOne), out var factory, out _);
        using var scope = UseFactory(sandbox, factory);

        var result = await CliRunner.RunAsync("auth", "login", "--client-id", "not-a-guid");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("not a GUID", result.StandardError, StringComparison.Ordinal);
        Assert.Null(factory.DeviceCode);
    }

    // ---- logout ----

    [Fact]
    public async Task Logout_DeletesTheRecordAndTheEarmCacheFiles_OnlyThose()
    {
        using var sandbox = Sandbox(nameof(Logout_DeletesTheRecordAndTheEarmCacheFiles_OnlyThose), out var factory, out string cache);
        using var scope = UseFactory(sandbox, factory);
        await factory.RecordStore.SaveAsync(Record(), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(cache, "earm.nocae"), "x", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(cache, "msal.cache"), "x", TestContext.Current.CancellationToken);

        var first = await CliRunner.RunAsync("auth", "logout");
        var second = await CliRunner.RunAsync("auth", "logout", "-o", "json");

        Assert.Equal(0, first.ExitCode);
        Assert.Contains("removed the sign-in record and 1 token cache file(s)", first.StandardOutput, StringComparison.Ordinal);
        Assert.False(File.Exists(sandbox.RecordPath));
        Assert.False(File.Exists(Path.Combine(cache, "earm.nocae")));
        Assert.True(File.Exists(Path.Combine(cache, "msal.cache")));
        Assert.Equal(0, second.ExitCode);
        Assert.False(second.Json["recordDeleted"]!.GetValue<bool>());
        Assert.Equal(0, second.Json["cacheFilesDeleted"]!.GetValue<int>());
    }

    // ---- options and help ----

    [Fact]
    public async Task TheAuthOption_RejectsUnknownModes_AtParseTime()
    {
        var result = await CliRunner.RunAsync("auth", "status", "--auth", "cli");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("--auth", result.StandardError + result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnknownModeInTheEnvironment_ExitsOne_BeforeAnyRequest()
    {
        using var sandbox = new CliSandbox(nameof(AnUnknownModeInTheEnvironment_ExitsOne_BeforeAnyRequest));
        string template = sandbox.WriteEmptyTemplate();
        sandbox.UsePreflightOk();
        string? saved = Environment.GetEnvironmentVariable("EARM_AUTH");
        try
        {
            Environment.SetEnvironmentVariable("EARM_AUTH", "az");

            var result = await CliRunner.RunAsync("deployment", "what-if", "-f", template);

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("Unknown authentication mode 'az'", result.StandardError, StringComparison.Ordinal);
            Assert.Empty(sandbox.RecordedCommands());
        }
        finally
        {
            Environment.SetEnvironmentVariable("EARM_AUTH", saved);
        }
    }

    [Theory]
    [InlineData("auth")]
    public async Task Help_ListsTheAuthVerbsAndOptions(string verb)
    {
        var group = await CliRunner.RunAsync(verb, "--help");
        var login = await CliRunner.RunAsync(verb, "login", "--help");

        Assert.Equal(0, group.ExitCode);
        foreach (var word in new[] { "login", "logout", "status" })
        {
            Assert.Contains(word, group.StandardOutput, StringComparison.Ordinal);
        }

        Assert.Equal(0, login.ExitCode);
        foreach (var flag in new[] { "--device-code", "--tenant", "--client-id", "--auth" })
        {
            Assert.Contains(flag, login.StandardOutput, StringComparison.Ordinal);
        }
    }
}
