using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Identity;
using EArm.Graph.Auth;
using EArm.Graph.Tests.Infrastructure;
using Xunit;

namespace EArm.Graph.Tests;

/// <summary>
/// DES-0002 phase 4 in the Graph layer: the silent retry after a consent prompt runs on a NEW credential over the same stored sign-in, a Key Vault
/// <c>.default</c> request consents the vault's <c>user_impersonation</c>, and the preflight's <c>scp</c> check (missing scopes prompted for once, or a
/// consent failure before any Graph request; skipped for <c>.default</c> and app-only tokens). No browser, no network.
/// </summary>
public sealed class RunScopeConsentTests
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";

    private static readonly TokenScopes GraphRead = TokenScopes.Explicit("https://graph.microsoft.com", "User.Read", "Organization.Read.All", "Group.Read.All");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Jwt(string payloadJson)
    {
        static string B64(string text)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        return B64("{\"alg\":\"none\"}") + "." + B64(payloadJson) + ".sig";
    }

    private static string UserToken(string scp)
    {
        return Jwt("{\"tid\":\"" + Tenant + "\",\"oid\":\"o-1\",\"upn\":\"alice@contoso.example\",\"appid\":\"" + PublicClients.GraphCommandLineTools + "\",\"scp\":\"" + scp + "\"}");
    }

    private static ScriptedResponse Organization()
    {
        return ScriptedResponse.Json("{\"value\":[{\"id\":\"" + Tenant + "\",\"displayName\":\"Contoso\"}]}");
    }

    /// <summary>Hands out the queued tokens in order (the last one repeats); records every requested set and invalidation.</summary>
    private sealed class QueuedTokens : IGraphTokenProvider
    {
        private readonly Queue<string> _tokens;
        private string _last;

        public QueuedTokens(params string[] tokens)
        {
            _tokens = new Queue<string>(tokens);
            _last = tokens[0];
        }

        public List<TokenScopes> Requested { get; } = [];

        public int Invalidations { get; private set; }

        public ValueTask<GraphToken> GetTokenAsync(TokenScopes scopes, CancellationToken cancellationToken)
        {
            Requested.Add(scopes);
            if (_tokens.Count > 0)
            {
                _last = _tokens.Dequeue();
            }

            return ValueTask.FromResult(new GraphToken(_last, DateTimeOffset.UtcNow.AddHours(1)));
        }

        public void Invalidate(TokenScopes scopes)
        {
            Invalidations++;
        }
    }

    private sealed class FakePrompter : IConsentPrompter
    {
        public List<TokenScopes> Calls { get; } = [];

        public Exception? Failure { get; set; }

        public Task ConsentAsync(TokenScopes scopes, CancellationToken cancellationToken)
        {
            Calls.Add(scopes);
            return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
        }
    }

    /// <summary>A factory whose interactive credential is whatever <see cref="Interactive"/> holds when the credential is built; counts the builds.</summary>
    private sealed class Factory : GraphAuthFactory
    {
        public Factory(AuthRecordStore store)
            : base(store)
        {
        }

        public TokenCredential? Interactive { get; set; }

        public int InteractiveBuilds { get; private set; }

        protected override TokenCredential CreateInteractive(GraphAuthOptions options, AuthenticationRecord record)
        {
            InteractiveBuilds++;
            return Interactive!;
        }
    }

    private static (GraphPreflight Preflight, ScriptedHttpHandler Handler, HttpClient Http) Preflight(IGraphTokenProvider tokens, TokenScopes? scopes, IConsentPrompter? prompter, params ScriptedResponse[] responses)
    {
        var handler = new ScriptedHttpHandler(responses);
        var http = new HttpClient(handler, disposeHandler: false);
        var options = new GraphOptions { Scopes = scopes };
        var client = new HttpGraphClient(http, tokens, new RetryPolicy(new RetryOptions(), new RecordingDelayProvider(), new TestLogger<RetryPolicy>(new LogSink())), options);
        return (new GraphPreflight(tokens, client, GraphCloud.Public, GraphAuthMode.Auto, scopes: scopes, consentPrompter: prompter), handler, http);
    }

    // ---- retry on a new credential ----------------------------------------------------------------------------------

    [Fact]
    public async Task AfterConsent_TheSilentRetryRunsOnANewCredential_AndLaterRequestsKeepIt()
    {
        var original = new FakeCredential().Throws(DelegatedSignInTests.ConsentFailure());
        var renewed = new FakeCredential().Returns("after-consent", DateTimeOffset.UtcNow.AddHours(1)).Returns("later", DateTimeOffset.UtcNow.AddHours(1));
        var builds = 0;
        var prompter = new FakePrompter();
        using var provider = new AzureIdentityTokenProvider(original, GraphAuthMode.Auto, consentPrompter: prompter, renewCredential: () =>
        {
            builds++;
            return renewed;
        });

        var token = await provider.GetTokenAsync(GraphRead, Ct);
        await provider.GetTokenAsync(TokenScopes.Explicit("https://graph.microsoft.com", "User.Read"), Ct);

        Assert.Equal("after-consent", token.Value);
        Assert.Equal(1, original.Calls);
        Assert.Equal(2, renewed.Calls);
        Assert.Equal(1, builds);
        Assert.Same(renewed, provider.Credential);
    }

    [Fact]
    public async Task TheFactory_RenewsTheCredentialFromTheStoredRecord_ForTheRetry()
    {
        var path = Path.Combine(Path.GetTempPath(), "earm-p4-" + Guid.NewGuid().ToString("N"), "auth", "record.json");
        try
        {
            var store = new AuthRecordStore(path);
            await store.SaveAsync(DelegatedSignInTests.Record(), Ct);
            var before = new FakeCredential().Throws(DelegatedSignInTests.ConsentFailure());
            var after = new FakeCredential().Returns("after-consent", DateTimeOffset.UtcNow.AddHours(1));
            var factory = new Factory(store) { Interactive = before };
            var prompter = new FakePrompter();
            using var provider = (AzureIdentityTokenProvider)factory.Create(new GraphAuthOptions { Mode = GraphAuthMode.Interactive }, prompter);
            factory.Interactive = after;

            var token = await provider.GetTokenAsync(GraphRead, Ct);

            Assert.Equal("after-consent", token.Value);
            Assert.Equal(2, factory.InteractiveBuilds);
            Assert.Equal(1, before.Calls);
            Assert.Equal(1, after.Calls);
            Assert.Equal([GraphRead], prompter.Calls);
        }
        finally
        {
            var root = Path.GetDirectoryName(Path.GetDirectoryName(path));
            if (root is not null && Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task WithoutARenewCallback_TheRetryKeepsTheSameCredential()
    {
        var credential = new FakeCredential().Throws(DelegatedSignInTests.ConsentFailure()).Returns("after-consent", DateTimeOffset.UtcNow.AddHours(1));
        using var provider = new AzureIdentityTokenProvider(credential, GraphAuthMode.Auto, consentPrompter: new FakePrompter());

        await provider.GetTokenAsync(GraphRead, Ct);

        Assert.Same(credential, provider.Credential);
        Assert.Equal(2, credential.Calls);
    }

    [Fact]
    public async Task TheProvidersConsentPrompter_PromptsOnce_RenewsTheCredential_AndDropsCachedTokens()
    {
        var original = new FakeCredential().Returns("before", DateTimeOffset.UtcNow.AddHours(1));
        var renewed = new FakeCredential().Returns("after", DateTimeOffset.UtcNow.AddHours(1));
        var prompter = new FakePrompter();
        using var provider = new AzureIdentityTokenProvider(original, GraphAuthMode.Auto, consentPrompter: prompter, renewCredential: () => renewed);
        Assert.Equal("before", (await provider.GetTokenAsync(GraphRead, Ct)).Value);
        var missing = TokenScopes.Explicit("https://graph.microsoft.com", "Group.Read.All");

        await provider.ConsentPrompter!.ConsentAsync(missing, Ct);

        Assert.Equal([missing], prompter.Calls);
        Assert.Equal("after", (await provider.GetTokenAsync(GraphRead, Ct)).Value);
        Assert.Null(new AzureIdentityTokenProvider(original).ConsentPrompter);
    }

    // ---- Key Vault consent ------------------------------------------------------------------------------------------

    [Fact]
    public async Task AKeyVaultDefaultRequestThatNeedsConsent_PromptsForTheVaultsUserImpersonation_ThenGetsDefaultSilently()
    {
        var original = new FakeCredential().Throws(DelegatedSignInTests.ConsentFailure("https://vault.azure.net/.default"));
        var renewed = new FakeCredential().Returns("vault-token", DateTimeOffset.UtcNow.AddHours(1));
        var prompter = new FakePrompter();
        using var provider = new AzureIdentityTokenProvider(original, GraphAuthMode.Auto, consentPrompter: prompter, renewCredential: () => renewed);

        var token = await provider.GetTokenAsync(GraphCloud.Public.KeyVaultScopes, Ct);

        Assert.Equal("vault-token", token.Value);
        Assert.Equal([GraphCloud.Public.KeyVaultConsentScopes], prompter.Calls);
        Assert.Equal(["https://vault.azure.net/.default"], renewed.Scopes);
    }

    [Fact]
    public async Task AKeyVaultConsentFailure_WithoutAPrompter_NamesUserImpersonation_AndTheTemplateLogin()
    {
        var credential = new FakeCredential().Throws(DelegatedSignInTests.ConsentFailure("https://vault.azure.net/.default"));
        using var provider = new AzureIdentityTokenProvider(credential, GraphAuthMode.Auto, signIn: new SignInAccount("alice@contoso.example", PublicClients.GraphCommandLineTools));

        var error = await Assert.ThrowsAsync<GraphTransportException>(() => provider.GetTokenAsync(GraphCloud.UsGov.KeyVaultScopes, Ct).AsTask());

        Assert.Equal(GraphCloud.UsGov.KeyVaultConsentScopes, ConsentRequiredException.FindIn(error)!.Scopes);
        Assert.Contains("earm auth login --scopes https://vault.usgovcloudapi.net/user_impersonation", error.Message, StringComparison.Ordinal);
        Assert.Contains("earm auth login -f <template>", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyKeyVaultDefault_IsMappedToAnotherConsentSet()
    {
        Assert.Equal(GraphCloud.China.KeyVaultConsentScopes, AzureIdentityTokenProvider.ConsentSetFor(GraphCloud.China.KeyVaultScopes));
        Assert.Equal(GraphCloud.Public.GraphScopes, AzureIdentityTokenProvider.ConsentSetFor(GraphCloud.Public.GraphScopes));
        Assert.Equal(GraphRead, AzureIdentityTokenProvider.ConsentSetFor(GraphRead));
        Assert.Equal(GraphCloud.Public.KeyVaultConsentScopes, AzureIdentityTokenProvider.ConsentSetFor(GraphCloud.Public.KeyVaultConsentScopes));
    }

    // ---- preflight scp check ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Preflight_WhenTheTokenHasEveryRequestedScope_CaseInsensitively_ReadsTheOrganization()
    {
        var tokens = new QueuedTokens(UserToken("user.read ORGANIZATION.READ.ALL Group.Read.All Mail.Send"));
        var prompter = new FakePrompter();
        var (preflight, handler, http) = Preflight(tokens, GraphRead, prompter, Organization());
        using var closeHttp = http;

        var result = await preflight.RunAsync(Ct);

        Assert.Equal(Tenant, result.TenantId);
        Assert.Empty(prompter.Calls);
        Assert.Single(handler.Requests);
        Assert.All(tokens.Requested, s => Assert.Same(GraphRead, s));
    }

    [Fact]
    public async Task Preflight_MissingScopes_WithoutAPrompter_FailBeforeAnyGraphRequest_ListingThem()
    {
        var tokens = new QueuedTokens(UserToken("User.Read Organization.Read.All"));
        var (preflight, handler, http) = Preflight(tokens, GraphRead, null, Organization());
        using var closeHttp = http;

        var error = await Assert.ThrowsAsync<GraphTransportException>(() => preflight.RunAsync(Ct));

        Assert.Equal(GraphTransportFailure.NotAuthenticated, error.Failure);
        var consent = ConsentRequiredException.FindIn(error)!;
        Assert.Equal(["Group.Read.All"], consent.Scopes.Names);
        Assert.Contains("lacks requested delegated scopes", error.Message, StringComparison.Ordinal);
        Assert.Contains("earm auth login --scopes Group.Read.All", error.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Preflight_MissingScopes_WithAPrompter_PromptOnceForTheMissingOnes_ThenUseTheNewToken()
    {
        var tokens = new QueuedTokens(UserToken("User.Read Organization.Read.All"), UserToken("User.Read Organization.Read.All Group.Read.All"));
        var prompter = new FakePrompter();
        var (preflight, handler, http) = Preflight(tokens, GraphRead, prompter, Organization());
        using var closeHttp = http;

        var result = await preflight.RunAsync(Ct);

        Assert.Equal(Tenant, result.TenantId);
        var asked = Assert.Single(prompter.Calls);
        Assert.Equal(["Group.Read.All"], asked.Names);
        Assert.Equal(1, tokens.Invalidations);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Preflight_ScopesStillMissingAfterThePrompt_FailWithoutASecondPrompt_AndBeforeAnyGraphRequest()
    {
        var tokens = new QueuedTokens(UserToken("User.Read"));
        var prompter = new FakePrompter();
        var (preflight, handler, http) = Preflight(tokens, GraphRead, prompter, Organization());
        using var closeHttp = http;

        var error = await Assert.ThrowsAsync<GraphTransportException>(() => preflight.RunAsync(Ct));

        Assert.Single(prompter.Calls);
        Assert.Contains("still lacks", error.Message, StringComparison.Ordinal);
        Assert.Equal(["Organization.Read.All", "Group.Read.All"], ConsentRequiredException.FindIn(error)!.Scopes.Names);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Preflight_ADeclinedPrompt_FailsAsConsentNeeded_BeforeAnyGraphRequest()
    {
        var tokens = new QueuedTokens(UserToken("User.Read Organization.Read.All"));
        var prompter = new FakePrompter { Failure = new AuthenticationFailedException("AADSTS65004: User declined to consent to access the app.") };
        var (preflight, handler, http) = Preflight(tokens, GraphRead, prompter, Organization());
        using var closeHttp = http;

        var error = await Assert.ThrowsAsync<GraphTransportException>(() => preflight.RunAsync(Ct));

        Assert.Contains("was not completed", error.Message, StringComparison.Ordinal);
        Assert.NotNull(ConsentRequiredException.FindIn(error));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Preflight_SkipsTheScpCheck_ForAppOnlyTokens_AndForDefault()
    {
        var appOnly = new QueuedTokens(Jwt("{\"tid\":\"" + Tenant + "\",\"oid\":\"sp-1\",\"appid\":\"app-1\",\"idtyp\":\"app\",\"roles\":[\"Organization.Read.All\"]}"));
        var (appPreflight, appHandler, appHttp) = Preflight(appOnly, GraphRead, null, Organization());
        using var closeAppHttp = appHttp;
        var user = new QueuedTokens(UserToken("User.Read"));
        var (defaultPreflight, defaultHandler, defaultHttp) = Preflight(user, null, null, Organization());
        using var closeDefaultHttp = defaultHttp;

        Assert.Equal("application", (await appPreflight.RunAsync(Ct)).PrincipalType);
        Assert.Equal("user", (await defaultPreflight.RunAsync(Ct)).PrincipalType);

        Assert.Single(appHandler.Requests);
        Assert.Single(defaultHandler.Requests);
        Assert.True(defaultPreflight.Scopes.IsDefault);
        Assert.Empty(defaultPreflight.MissingScopes(new TokenClaims { Scopes = [] }));
    }
}
