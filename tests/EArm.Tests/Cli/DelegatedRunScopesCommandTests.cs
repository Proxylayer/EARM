using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Identity;
using EArm.Cli.Commands;
using EArm.Cli.Hosting;
using EArm.Engine.Permissions;
using EArm.Graph;
using EArm.Graph.Auth;
using EArm.TestSupport;
using Xunit;
using MsalUiRequiredException = Microsoft.Identity.Client.MsalUiRequiredException;

namespace EArm.Tests.Cli;

/// <summary>
/// DES-0002 phase 4 at the command line: a user sign-in (a stored record) makes <c>what-if</c>, <c>create</c> and <c>delete</c> request exactly the delegated
/// scopes of their verb (one set for the whole command, Graph client and preflight alike); app-only and default-chain runs keep <c>.default</c>. A missing
/// consent is prompted for on an interactive terminal (then the run continues, on a new credential) and otherwise stops the run with exit 3 before any write;
/// the preflight's <c>scp</c> check and the Key Vault consent take the same two paths.
/// </summary>
public sealed class DelegatedRunScopesCommandTests
{
    private const string GraphOrigin = "https://graph.microsoft.com";

    private const string RecordJson = "{\"username\":\"alice@contoso.example\",\"authority\":\"login.microsoftonline.com\",\"homeAccountId\":\"oid.tid\",\"tenantId\":\"11111111-1111-1111-1111-111111111111\",\"clientId\":\"14d82eec-204b-4c2f-b7e8-296a70dab67e\",\"version\":\"1.0\"}";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AuthenticationRecord Record()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(RecordJson));
        return AuthenticationRecord.Deserialize(stream);
    }

    private static (CliSandbox Sandbox, FakeDirectory Directory) Setup(string test)
    {
        var sandbox = new CliSandbox(test);
        var directory = new FakeDirectory();
        sandbox.Handler.Directory = directory;
        sandbox.Tokens.TenantId = CliSandbox.Tenant;
        return (sandbox, directory);
    }

    private static Task SignInAsUserAsync(CliSandbox sandbox)
    {
        return new AuthRecordStore(sandbox.RecordPath).SaveAsync(Record(), Ct);
    }

    private static string GroupTemplate(CliSandbox sandbox)
    {
        return sandbox.WriteTemplate("g.entra.json", "\"resources\": [ { \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"g\", \"properties\": { \"displayName\": \"G\", \"mailEnabled\": false, \"securityEnabled\": true } } ]");
    }

    private static async Task<RunScopeSet> ScopesOf(string template, RunVerb verb, params string[] parameterTokens)
    {
        var result = await TemplateRunScopes.ComputeAsync(template, parameterTokens, verb, Ct);
        return result.Scopes!;
    }

    private static string Requested(RunScopeSet set)
    {
        return TokenScopes.Explicit(GraphOrigin, set.GraphScopes).ToString();
    }

    private static int Writes(CliSandbox sandbox)
    {
        return sandbox.RecordedCommands().Count(c => c.StartsWith("DELETE ", StringComparison.Ordinal) || c.StartsWith("POST ", StringComparison.Ordinal) || c.StartsWith("PATCH ", StringComparison.Ordinal) || c.StartsWith("PUT ", StringComparison.Ordinal));
    }

    private static string FirstCode(CliResult result)
    {
        return result.Diagnostics[0]!["code"]!.GetValue<string>();
    }

    /// <summary>Runs <paramref name="body"/> with <see cref="ConsentPrompting.TerminalIsInteractive"/> forced, restoring it afterwards.</summary>
    private static async Task WithTerminal(bool interactive, Func<Task> body)
    {
        var saved = ConsentPrompting.TerminalIsInteractive;
        ConsentPrompting.TerminalIsInteractive = () => interactive;
        try
        {
            await body();
        }
        finally
        {
            ConsentPrompting.TerminalIsInteractive = saved;
        }
    }

    // ---- unsigned JWTs and the AADSTS65001 shape -----------------------------------------------------------------------

    private static string UserJwt(IEnumerable<string> scp)
    {
        static string B64(string text)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        var payload = "{\"tid\":\"" + CliSandbox.Tenant + "\",\"oid\":\"99999999-0000-4000-8000-000000000001\",\"upn\":\"alice@contoso.example\",\"appid\":\"" + PublicClients.GraphCommandLineTools + "\",\"scp\":\"" + string.Join(' ', scp) + "\",\"exp\":" + DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() + "}";
        return B64("{\"alg\":\"none\",\"typ\":\"JWT\"}") + "." + B64(payload) + ".sig";
    }

    private static Exception ConsentFailure(TokenRequestContext context)
    {
        var msal = new MsalUiRequiredException("invalid_grant", "AADSTS65001: The user or administrator has not consented to use the application with ID '14d82eec-204b-4c2f-b7e8-296a70dab67e'. Trace ID: 0000");
        return new AuthenticationRequiredException("Interactive authentication is needed to acquire token.", context, msal);
    }

    /// <summary>
    /// The real token provider and consent prompter over fake credentials: each built interactive credential gets a generation number (1 = the first, 2 = the
    /// one renewed after a consent) and answers through <see cref="Respond"/>; the consent prompt (the identity provider) is recorded.
    /// </summary>
    private sealed class WiredFactory : GraphAuthFactory
    {
        public WiredFactory(AuthRecordStore store)
            : base(store, cacheDirectories: [Path.Combine(Path.GetTempPath(), "earm-test-token-cache-" + Guid.NewGuid().ToString("N"))]) // a private, never-created directory: a logout here has nothing to delete
        {
        }

        public Func<int, TokenRequestContext, AccessToken> Respond { get; set; } = (_, _) => throw new InvalidOperationException("no response");

        public int Credentials { get; private set; }

        public List<TokenScopes> Consents { get; } = [];

        public Action<TokenScopes>? OnConsent { get; set; }

        protected override TokenCredential CreateInteractive(GraphAuthOptions options, AuthenticationRecord record)
        {
            return new GenerationCredential(this, ++Credentials);
        }

        internal override Task<AuthenticationRecord> AuthenticateAsync(GraphAuthOptions options, TokenScopes scopes, AuthenticationRecord? account, bool deviceCode, Func<DeviceCodeInfo, CancellationToken, Task>? deviceCodeCallback, CancellationToken cancellationToken)
        {
            Consents.Add(scopes);
            OnConsent?.Invoke(scopes);
            return Task.FromResult(account ?? Record());
        }

        private sealed class GenerationCredential : TokenCredential
        {
            private readonly WiredFactory _owner;
            private readonly int _generation;

            public GenerationCredential(WiredFactory owner, int generation)
            {
                _owner = owner;
                _generation = generation;
            }

            public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            {
                return _owner.Respond(_generation, requestContext);
            }

            public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            {
                return ValueTask.FromResult(GetToken(requestContext, cancellationToken));
            }
        }
    }

    private static IDisposable UseWired(CliSandbox sandbox, WiredFactory factory, bool fakeTokens)
    {
        return ServiceOverrides.UseAmbient(new ServiceOverrides
        {
            Handler = sandbox.Handler,
            Tokens = fakeTokens ? sandbox.Tokens : null,
            Delay = new NoDelay(),
            RecordStore = sandbox.Overrides.RecordStore,
            AuthFactory = factory,
        });
    }

    // ---- the scopes of each verb ----------------------------------------------------------------------------------------

    [Fact]
    public async Task WhatIf_SignedInAsUser_RequestsOnlyTheReadScopesOfItsVerb_ForEveryToken()
    {
        var (sandbox, _) = Setup(nameof(WhatIf_SignedInAsUser_RequestsOnlyTheReadScopesOfItsVerb_ForEveryToken));
        using var disposeSandbox = sandbox;
        await SignInAsUserAsync(sandbox);
        string template = GroupTemplate(sandbox);
        var set = await ScopesOf(template, RunVerb.WhatIf);
        sandbox.Tokens.Scopes = [.. set.GraphScopes];

        var result = await CliRunner.RunAsync("deployment", "what-if", "-f", template, "-o", "json");

        Assert.True(result.ExitCode == 0, result.StandardOutput + result.StandardError);
        Assert.NotEmpty(sandbox.Tokens.RequestedScopes);
        Assert.All(sandbox.Tokens.RequestedScopes, s => Assert.Equal(Requested(set), s));
        Assert.DoesNotContain("ReadWrite", Requested(set), StringComparison.Ordinal);
        Assert.DoesNotContain("Organization.Read.All", set.GraphScopes);
        Assert.Equal(0, Writes(sandbox));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Create_SignedInAsUser_RequestsTheWriteScopes_ForTheWholeCommand_IncludingItsWhatIfPass(bool confirmWithWhatIf)
    {
        var (sandbox, _) = Setup(nameof(Create_SignedInAsUser_RequestsTheWriteScopes_ForTheWholeCommand_IncludingItsWhatIfPass) + confirmWithWhatIf);
        using var disposeSandbox = sandbox;
        await SignInAsUserAsync(sandbox);
        string template = GroupTemplate(sandbox);
        var set = await ScopesOf(template, RunVerb.Create);
        sandbox.Tokens.Scopes = [.. set.GraphScopes];
        string[] args = confirmWithWhatIf ? ["deployment", "create", "-f", template, "-c", "-y", "-o", "json"] : ["deployment", "create", "-f", template, "-y", "-o", "json"];

        var result = await CliRunner.RunAsync(args);

        Assert.True(result.ExitCode == 0, result.StandardOutput + result.StandardError);
        Assert.All(sandbox.Tokens.RequestedScopes, s => Assert.Equal(Requested(set), s));
        Assert.Contains("ReadWrite", Requested(set), StringComparison.Ordinal);
        Assert.True(Writes(sandbox) > 0);
    }

    [Fact]
    public async Task Delete_SignedInAsUser_RequestsTheDeleteScopeSet()
    {
        var (sandbox, _) = Setup(nameof(Delete_SignedInAsUser_RequestsTheDeleteScopeSet));
        using var disposeSandbox = sandbox;
        await SignInAsUserAsync(sandbox);
        string template = GroupTemplate(sandbox);
        var set = await ScopesOf(template, RunVerb.Delete);
        sandbox.Tokens.Scopes = [.. set.GraphScopes];

        var result = await CliRunner.RunAsync("deployment", "delete", "-f", template, "-y", "-o", "json");

        Assert.True(result.ExitCode == 0, result.StandardOutput + result.StandardError);
        Assert.Equal(RunVerb.Delete, set.Verb);
        Assert.NotEmpty(sandbox.Tokens.RequestedScopes);
        Assert.All(sandbox.Tokens.RequestedScopes, s => Assert.Equal(Requested(set), s));
    }

    [Fact]
    public async Task WithoutAStoredSignIn_OrWithAuthDefault_EveryTokenIsDefault()
    {
        var (sandbox, _) = Setup(nameof(WithoutAStoredSignIn_OrWithAuthDefault_EveryTokenIsDefault));
        using var disposeSandbox = sandbox;
        string template = GroupTemplate(sandbox);

        var noRecord = await CliRunner.RunAsync("deployment", "create", "-f", template, "-y", "-o", "json");
        var noRecordScopes = sandbox.Tokens.RequestedScopes.ToList();
        sandbox.Tokens.RequestedScopes.Clear();
        await SignInAsUserAsync(sandbox);
        var authDefault = await CliRunner.RunAsync("deployment", "what-if", "-f", template, "--auth", "default", "-o", "json");

        Assert.True(noRecord.ExitCode == 0, noRecord.StandardOutput + noRecord.StandardError);
        Assert.True(authDefault.ExitCode == 0, authDefault.StandardOutput + authDefault.StandardError);
        Assert.All(noRecordScopes, s => Assert.Equal(GraphOrigin + "/.default", s));
        Assert.NotEmpty(sandbox.Tokens.RequestedScopes);
        Assert.All(sandbox.Tokens.RequestedScopes, s => Assert.Equal(GraphOrigin + "/.default", s));
    }

    [Fact]
    public async Task AnAppOnlyToken_UnderAUserSignInsScopes_IsNotScpChecked()
    {
        var (sandbox, _) = Setup(nameof(AnAppOnlyToken_UnderAUserSignInsScopes_IsNotScpChecked));
        using var disposeSandbox = sandbox;
        await SignInAsUserAsync(sandbox);
        sandbox.Tokens.AppId = "app-1";
        sandbox.Tokens.UserPrincipalName = null;
        sandbox.Tokens.Scopes = [];
        sandbox.Tokens.Roles = ["Group.ReadWrite.All"];

        var result = await CliRunner.RunAsync("deployment", "what-if", "-f", GroupTemplate(sandbox), "-o", "json");

        Assert.True(result.ExitCode == 0, result.StandardOutput + result.StandardError);
    }

    // ---- consent at run time (the real provider and prompter over fake credentials) --------------------------------------

    [Fact]
    public async Task NonInteractive_AMissingConsent_StopsTheRunWithExit3_BeforeAnyGraphRequest()
    {
        var (sandbox, _) = Setup(nameof(NonInteractive_AMissingConsent_StopsTheRunWithExit3_BeforeAnyGraphRequest));
        using var disposeSandbox = sandbox;
        await SignInAsUserAsync(sandbox);
        var factory = new WiredFactory(sandbox.Overrides.RecordStore!) { Respond = (_, context) => throw ConsentFailure(context) };
        using var wired = UseWired(sandbox, factory, fakeTokens: false);
        string template = GroupTemplate(sandbox);
        var set = await ScopesOf(template, RunVerb.Create);

        CliResult result = null!;
        await WithTerminal(false, async () => result = await CliRunner.RunAsync("deployment", "create", "-f", template, "-y", "-o", "json"));

        Assert.Equal(3, result.ExitCode);
        Assert.Equal("ConsentRequired", FirstCode(result));
        var message = result.Diagnostics[0]!["message"]!.GetValue<string>();
        Assert.Contains("Consent needed", message, StringComparison.Ordinal);
        Assert.Contains("earm auth login -f <template>", message, StringComparison.Ordinal);
        Assert.Contains(set.GraphScopes.First(s => s.Contains("ReadWrite", StringComparison.Ordinal)), message, StringComparison.Ordinal);
        Assert.Empty(factory.Consents);
        Assert.Empty(sandbox.RecordedCommands());
        Assert.Equal(0, Writes(sandbox));
    }

    [Fact]
    public async Task Interactive_AMissingConsent_IsPromptedForOnce_ThenTheRunSucceedsOnANewCredential()
    {
        var (sandbox, _) = Setup(nameof(Interactive_AMissingConsent_IsPromptedForOnce_ThenTheRunSucceedsOnANewCredential));
        using var disposeSandbox = sandbox;
        await SignInAsUserAsync(sandbox);
        string template = GroupTemplate(sandbox);
        var set = await ScopesOf(template, RunVerb.Create);
        var generations = new List<int>();
        var factory = new WiredFactory(sandbox.Overrides.RecordStore!)
        {
            Respond = (generation, context) =>
            {
                generations.Add(generation);
                return generation == 1 ? throw ConsentFailure(context) : new AccessToken(UserJwt(set.GraphScopes), DateTimeOffset.UtcNow.AddHours(1));
            },
        };
        using var wired = UseWired(sandbox, factory, fakeTokens: false);

        CliResult result = null!;
        await WithTerminal(true, async () => result = await CliRunner.RunAsync("deployment", "create", "-f", template, "-y", "--device-code", "-o", "json"));

        Assert.True(result.ExitCode == 0, result.StandardOutput + result.StandardError);
        var consent = Assert.Single(factory.Consents);
        Assert.Equal(Requested(set), consent.ToString());
        Assert.Equal(2, factory.Credentials);
        Assert.Equal(1, generations.Count(g => g == 1));
        Assert.All(generations.Skip(1), g => Assert.Equal(2, g));
        Assert.Contains("needs consent", result.StandardError, StringComparison.Ordinal);
        Assert.True(Writes(sandbox) > 0);
    }

    [Fact]
    public async Task ScpCheck_NonInteractive_MissingScopes_FailWithExit3_AndNoWrite()
    {
        var (sandbox, _) = Setup(nameof(ScpCheck_NonInteractive_MissingScopes_FailWithExit3_AndNoWrite));
        using var disposeSandbox = sandbox;
        await SignInAsUserAsync(sandbox);
        string template = GroupTemplate(sandbox);
        var set = await ScopesOf(template, RunVerb.Create);
        string missing = set.GraphScopes.First(s => s.Contains("ReadWrite", StringComparison.Ordinal));
        sandbox.Tokens.Scopes = [.. set.GraphScopes.Where(s => s != missing)];
        var factory = new WiredFactory(sandbox.Overrides.RecordStore!);
        using var wired = UseWired(sandbox, factory, fakeTokens: true);

        CliResult result = null!;
        await WithTerminal(false, async () => result = await CliRunner.RunAsync("deployment", "create", "-f", template, "-y", "-o", "json"));

        Assert.Equal(3, result.ExitCode);
        Assert.Equal("ConsentRequired", FirstCode(result));
        Assert.Contains("earm auth login --scopes " + missing, result.Diagnostics[0]!["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Empty(factory.Consents);
        Assert.Empty(sandbox.RecordedCommands());
    }

    [Fact]
    public async Task ScpCheck_Interactive_MissingScopes_ArePromptedForOnce_ThenTheRunContinues()
    {
        var (sandbox, _) = Setup(nameof(ScpCheck_Interactive_MissingScopes_ArePromptedForOnce_ThenTheRunContinues));
        using var disposeSandbox = sandbox;
        await SignInAsUserAsync(sandbox);
        string template = GroupTemplate(sandbox);
        var set = await ScopesOf(template, RunVerb.Create);
        string missing = set.GraphScopes.First(s => s.Contains("ReadWrite", StringComparison.Ordinal));
        sandbox.Tokens.Scopes = [.. set.GraphScopes.Where(s => s != missing)];
        var factory = new WiredFactory(sandbox.Overrides.RecordStore!) { OnConsent = _ => sandbox.Tokens.Scopes = [.. set.GraphScopes] };
        using var wired = UseWired(sandbox, factory, fakeTokens: true);

        CliResult result = null!;
        await WithTerminal(true, async () => result = await CliRunner.RunAsync("deployment", "create", "-f", template, "-y", "-o", "json"));

        Assert.True(result.ExitCode == 0, result.StandardOutput + result.StandardError);
        Assert.Equal([missing], Assert.Single(factory.Consents).Names);
        Assert.True(Writes(sandbox) > 0);
    }

    // ---- Key Vault consent -----------------------------------------------------------------------------------------------

    private static string KeyVaultTemplate(CliSandbox sandbox, out string parameters)
    {
        string template = sandbox.WriteTemplate("kv.entra.json", "\"parameters\": { \"pw\": { \"type\": \"securestring\" } }, \"resources\": [], \"outputs\": { \"echo\": { \"type\": \"securestring\", \"value\": \"[parameters('pw')]\" } }");
        parameters = Path.Combine(sandbox.TemplatesDir, "kv.parameters.json");
        File.WriteAllText(parameters, "{ \"parameters\": { \"pw\": { \"reference\": { \"keyVault\": { \"id\": \"contoso-kv\" }, \"secretName\": \"orders-secret\" } } } }");
        sandbox.UsePreflightOk(extraResponses: "{ \"match\": { \"method\": \"GET\", \"urlContains\": \"/secrets/orders-secret\" }, \"status\": 200, \"body\": { \"value\": \"V4ult-S3cret-Value\" } }");
        return template;
    }

    /// <summary>Graph tokens always; the vault's .default only from the credential renewed after the vault consent.</summary>
    private static Func<int, TokenRequestContext, AccessToken> VaultNeedsConsent(RunScopeSet set)
    {
        return (generation, context) =>
        {
            bool vault = context.Scopes.Any(s => s.StartsWith("https://vault.azure.net/", StringComparison.Ordinal));
            if (vault && generation == 1)
            {
                throw ConsentFailure(context);
            }

            return new AccessToken(vault ? "vault-token" : UserJwt(set.GraphScopes), DateTimeOffset.UtcNow.AddHours(1));
        };
    }

    [Fact]
    public async Task KeyVault_Interactive_TheVaultScopeIsConsentedOnce_ThenTheSecretIsRead()
    {
        using var sandbox = new CliSandbox(nameof(KeyVault_Interactive_TheVaultScopeIsConsentedOnce_ThenTheSecretIsRead));
        await SignInAsUserAsync(sandbox);
        string template = KeyVaultTemplate(sandbox, out string parameters);
        var set = await ScopesOf(template, RunVerb.Create, "@" + parameters);
        var factory = new WiredFactory(sandbox.Overrides.RecordStore!) { Respond = VaultNeedsConsent(set) };
        using var wired = UseWired(sandbox, factory, fakeTokens: false);

        CliResult result = null!;
        await WithTerminal(true, async () => result = await CliRunner.RunAsync("deployment", "create", "-f", template, "-p", "@" + parameters, "-y", "-o", "json"));

        Assert.True(result.ExitCode == 0, result.StandardOutput + result.StandardError);
        Assert.True(set.NeedsKeyVault);
        Assert.Equal(GraphCloud.Public.KeyVaultConsentScopes, Assert.Single(factory.Consents));
        Assert.Single(sandbox.RecordedRequests(), static r => r.Uri.Host.StartsWith("contoso-kv.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task KeyVault_NonInteractive_AMissingVaultConsent_StopsWithExit3_AndTheGuidance()
    {
        using var sandbox = new CliSandbox(nameof(KeyVault_NonInteractive_AMissingVaultConsent_StopsWithExit3_AndTheGuidance));
        await SignInAsUserAsync(sandbox);
        string template = KeyVaultTemplate(sandbox, out string parameters);
        var set = await ScopesOf(template, RunVerb.Create, "@" + parameters);
        var factory = new WiredFactory(sandbox.Overrides.RecordStore!) { Respond = VaultNeedsConsent(set) };
        using var wired = UseWired(sandbox, factory, fakeTokens: false);

        CliResult result = null!;
        await WithTerminal(false, async () => result = await CliRunner.RunAsync("deployment", "create", "-f", template, "-p", "@" + parameters, "-y", "-o", "json"));

        Assert.Equal(3, result.ExitCode);
        var message = result.Diagnostics[0]!["message"]!.GetValue<string>();
        Assert.Contains("https://vault.azure.net/user_impersonation", message, StringComparison.Ordinal);
        Assert.Contains("earm auth login -f <template>", message, StringComparison.Ordinal);
        Assert.Empty(factory.Consents);
        Assert.DoesNotContain(sandbox.RecordedRequests(), static r => r.Uri.Host.StartsWith("contoso-kv.", StringComparison.Ordinal));
        Assert.Equal(0, Writes(sandbox));
    }
}
