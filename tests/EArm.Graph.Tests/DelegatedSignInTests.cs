using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Identity;
using EArm.Graph.Auth;
using EArm.Graph.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using MsalUiRequiredException = Microsoft.Identity.Client.MsalUiRequiredException;
using Xunit;

namespace EArm.Graph.Tests;

/// <summary>
/// DES-0002 phase 3: the default sign-in app per cloud, login consents (base set, extra sets as the same account), consent-needed vs expired
/// classification from the MSAL exception shapes observed in the spike, the opt-in interactive consent with one silent retry, and the warning for a
/// record of the Azure CLI app. No browser, no network.
/// </summary>
public sealed class DelegatedSignInTests
{
    private const string Alice = "{\"username\":\"alice@contoso.example\",\"authority\":\"login.microsoftonline.com\",\"homeAccountId\":\"oid.tid\",\"tenantId\":\"11111111-1111-1111-1111-111111111111\",\"clientId\":\"CLIENT\",\"version\":\"1.0\"}";

    private const string CustomClient = "44444444-4444-4444-4444-444444444444";

    private static readonly TokenScopes GraphWrite = TokenScopes.Explicit("https://graph.microsoft.com", "Group.ReadWrite.All", "Organization.Read.All");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal static AuthenticationRecord Record(string clientId = PublicClients.GraphCommandLineTools, string homeAccountId = "oid.tid", string username = "alice@contoso.example")
    {
        var json = Alice.Replace("CLIENT", clientId, StringComparison.Ordinal).Replace("oid.tid", homeAccountId, StringComparison.Ordinal).Replace("alice@contoso.example", username, StringComparison.Ordinal);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return AuthenticationRecord.Deserialize(stream);
    }

    /// <summary>The silent failure observed in the spike before consent: AuthenticationRequiredException wrapping MsalUiRequiredException (invalid_grant, AADSTS65001).</summary>
    internal static Exception ConsentFailure(params string[] scopes)
    {
        var msal = new MsalUiRequiredException(
            "invalid_grant",
            "AADSTS65001: The user or administrator has not consented to use the application with ID '14d82eec-204b-4c2f-b7e8-296a70dab67e' named 'Microsoft Graph Command Line Tools'. Send an interactive authorization request for this user and resource. Trace ID: 0000 Correlation ID: 0000 Timestamp: 2026-10-15 10:00:00Z");
        return new AuthenticationRequiredException("Interactive authentication is needed to acquire token. Call Authenticate to interactively authenticate.", new TokenRequestContext(scopes.Length == 0 ? ["https://graph.microsoft.com/Group.ReadWrite.All"] : scopes), msal);
    }

    /// <summary>An expired refresh token: the same wrapper, but AADSTS700082 (not consent).</summary>
    internal static Exception ExpiredFailure()
    {
        var msal = new MsalUiRequiredException("invalid_grant", "AADSTS700082: The refresh token has expired due to inactivity. The token was issued on 2026-01-01T00:00:00Z and was inactive for 90.00:00:00. Trace ID: 0000");
        return new AuthenticationRequiredException("Interactive authentication is needed to acquire token. Call Authenticate to interactively authenticate.", new TokenRequestContext(["https://graph.microsoft.com/.default"]), msal);
    }

    private static string NewRecordPath()
    {
        return Path.Combine(Path.GetTempPath(), "earm-p3-auth-" + Guid.NewGuid().ToString("N"), "auth", "record.json");
    }

    private static void Cleanup(string recordPath)
    {
        var root = Path.GetDirectoryName(Path.GetDirectoryName(recordPath));
        if (root is not null && Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>A factory with a fake interactive credential and a fake identity provider that records every interactive request.</summary>
    private sealed class Factory : GraphAuthFactory
    {
        public Factory(AuthRecordStore store, ILogger? logger = null)
            : base(store, logger)
        {
        }

        public TokenCredential? Interactive { get; set; }

        public Func<GraphAuthOptions, TokenScopes, AuthenticationRecord?, AuthenticationRecord>? Authenticator { get; set; }

        public List<(GraphAuthOptions Options, TokenScopes Scopes, AuthenticationRecord? Account, bool DeviceCode)> Requests { get; } = [];

        public override TokenCredential CreateDefaultChain(GraphAuthOptions options)
        {
            return new FakeCredential().Returns("chain-token", DateTimeOffset.UtcNow.AddHours(1));
        }

        protected override TokenCredential CreateInteractive(GraphAuthOptions options, AuthenticationRecord record)
        {
            return Interactive ?? new FakeCredential().Returns("interactive-token", DateTimeOffset.UtcNow.AddHours(1));
        }

        internal override Task<AuthenticationRecord> AuthenticateAsync(GraphAuthOptions options, TokenScopes scopes, AuthenticationRecord? account, bool deviceCode, Func<DeviceCodeInfo, CancellationToken, Task>? deviceCodeCallback, CancellationToken cancellationToken)
        {
            Requests.Add((options, scopes, account, deviceCode));
            return Task.FromResult(Authenticator is null ? account ?? Record(options.ClientId ?? PublicClients.AzureCli) : Authenticator(options, scopes, account));
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

    // ---- default client per cloud ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("public")]
    [InlineData("usgov")]
    [InlineData("usgovdod")]
    public void TheDefaultClientIsMicrosoftGraphCommandLineTools_InEveryCloudButChina(string cloudKey)
    {
        var auth = new GraphAuthOptions { Cloud = GraphCloud.Parse(cloudKey) };

        Assert.Equal("14d82eec-204b-4c2f-b7e8-296a70dab67e", PublicClients.DefaultFor(auth.Cloud));
        Assert.Equal(PublicClients.GraphCommandLineTools, GraphAuthFactory.LoginClientId(auth));
        Assert.Equal(PublicClients.GraphCommandLineTools, GraphAuthFactory.BuildBrowserLoginOptions(auth).ClientId);
        Assert.Equal(PublicClients.GraphCommandLineTools, GraphAuthFactory.BuildDeviceCodeLoginOptions(auth, null).ClientId);
    }

    [Fact]
    public void China_HasNoDefaultClient_AndANewSignInAsksForClientId()
    {
        var auth = new GraphAuthOptions { Cloud = GraphCloud.China };

        var error = Assert.Throws<GraphConfigurationException>(() => GraphAuthFactory.BuildBrowserLoginOptions(auth));
        Assert.Contains("--client-id", error.Message, StringComparison.Ordinal);
        Assert.Contains("21Vianet", error.Message, StringComparison.Ordinal);
        Assert.Throws<GraphConfigurationException>(() => GraphAuthFactory.BuildDeviceCodeLoginOptions(auth, null));
        Assert.Null(PublicClients.TryDefaultFor(GraphCloud.China));
        Assert.Equal(CustomClient, GraphAuthFactory.BuildBrowserLoginOptions(auth with { ClientId = CustomClient }).ClientId);
    }

    [Fact]
    public void AnExplicitClientId_WinsOverTheDefault_AndTheRecordedClientIsKeptForSilentRequests()
    {
        var auth = new GraphAuthOptions();

        Assert.Equal(CustomClient, GraphAuthFactory.BuildBrowserLoginOptions(auth with { ClientId = CustomClient }).ClientId);
        Assert.Equal(PublicClients.AzureCli, GraphAuthFactory.BuildInteractiveOptions(auth, Record(PublicClients.AzureCli)).ClientId);
        Assert.Equal(CustomClient, GraphAuthFactory.BuildInteractiveOptions(auth with { ClientId = CustomClient }, Record(PublicClients.AzureCli)).ClientId);
    }

    [Fact]
    public void KnownClientsHaveNames()
    {
        Assert.Equal("Microsoft Graph Command Line Tools", PublicClients.NameOf("14D82EEC-204B-4C2F-B7E8-296A70DAB67E"));
        Assert.Equal("Microsoft Azure CLI", PublicClients.NameOf(PublicClients.AzureCli));
        Assert.Null(PublicClients.NameOf(CustomClient));
        Assert.Null(PublicClients.NameOf(null));
    }

    // ---- login -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Login_RequestsTheGivenSet_WithTheDefaultClient_AndStoresARecordOfThatClient()
    {
        var path = NewRecordPath();
        try
        {
            var factory = new Factory(new AuthRecordStore(path));

            var record = await factory.LoginAsync(new GraphAuthOptions(), [GraphCloud.Public.SignInScopes], deviceCode: false, null, Ct);

            var request = Assert.Single(factory.Requests);
            Assert.Equal(["User.Read", "Organization.Read.All"], request.Scopes.Names);
            Assert.Null(request.Account);
            Assert.Equal(PublicClients.GraphCommandLineTools, request.Options.ClientId);
            Assert.Equal(PublicClients.GraphCommandLineTools, record.ClientId);
            Assert.Equal(PublicClients.GraphCommandLineTools, new AuthRecordStore(path).TryLoad()!.ClientId);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task Login_WithAKeyVaultSet_MakesASecondRequestAsTheSameAccount()
    {
        var path = NewRecordPath();
        try
        {
            var factory = new Factory(new AuthRecordStore(path));
            var vault = GraphCloud.Public.KeyVaultConsentScopes;

            await factory.LoginAsync(new GraphAuthOptions(), [GraphWrite, vault], deviceCode: true, null, Ct);

            Assert.Equal(2, factory.Requests.Count);
            Assert.Equal(GraphWrite, factory.Requests[0].Scopes);
            Assert.Null(factory.Requests[0].Account);
            Assert.Equal(["https://vault.azure.net/user_impersonation"], factory.Requests[1].Scopes.Scopes);
            Assert.Equal("alice@contoso.example", factory.Requests[1].Account!.Username);
            Assert.All(factory.Requests, r => Assert.True(r.DeviceCode));
            Assert.All(factory.Requests, r => Assert.Equal(PublicClients.GraphCommandLineTools, r.Options.ClientId));
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task Login_WhenTheSecondConsentIsAnsweredAsAnotherAccount_Fails_AndStoresNothing()
    {
        var path = NewRecordPath();
        try
        {
            var factory = new Factory(new AuthRecordStore(path))
            {
                Authenticator = (options, _, account) => account is null ? Record(options.ClientId!) : Record(options.ClientId!, "other.tid", "bob@contoso.example"),
            };

            var error = await Assert.ThrowsAsync<GraphTransportException>(() => factory.LoginAsync(new GraphAuthOptions(), [GraphWrite, GraphCloud.Public.KeyVaultConsentScopes], false, null, Ct));

            Assert.True(error.IsNotAuthenticated);
            Assert.Contains("bob@contoso.example", error.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(path));
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task Login_InChinaWithoutClientId_FailsWithConfigurationError_BeforeAnyRequest()
    {
        var path = NewRecordPath();
        try
        {
            var factory = new Factory(new AuthRecordStore(path));

            var error = await Assert.ThrowsAsync<GraphConfigurationException>(() => factory.LoginAsync(new GraphAuthOptions { Cloud = GraphCloud.China }, [GraphCloud.China.SignInScopes], false, null, Ct));

            Assert.Contains("--client-id", error.Message, StringComparison.Ordinal);
            Assert.Empty(factory.Requests);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ---- consent vs expired classification ---------------------------------------------------------------------------

    [Fact]
    public void Classification_AADSTS65001InsideAuthenticationRequired_IsConsent()
    {
        Assert.True(ConsentRequiredException.IsConsentFailure(ConsentFailure()));
        Assert.True(ConsentRequiredException.IsConsentFailure(new InvalidOperationException("outer", ConsentFailure())));
        Assert.True(ConsentRequiredException.IsConsentFailure(new AuthenticationRequiredException("x", new TokenRequestContext(["s"]), new MsalUiRequiredException("consent_required", "The user must consent."))));
    }

    [Fact]
    public void Classification_ExpiryAndOtherFailures_AreNotConsent()
    {
        Assert.False(ConsentRequiredException.IsConsentFailure(ExpiredFailure()));
        Assert.False(ConsentRequiredException.IsConsentFailure(new MsalUiRequiredException("invalid_grant", "AADSTS65001: not consented")));
        Assert.False(ConsentRequiredException.IsConsentFailure(new AuthenticationFailedException("AADSTS65001: not consented")));
        Assert.False(ConsentRequiredException.IsConsentFailure(new CredentialUnavailableException("no record")));
    }

    [Fact]
    public async Task Provider_ConsentFailure_IsConsentNeeded_WithTheMissingScopesAndTheCommand_NotExpired()
    {
        var credential = new FakeCredential().Throws(ConsentFailure());
        using var provider = new AzureIdentityTokenProvider(credential, GraphAuthMode.Interactive, signIn: new SignInAccount("alice@contoso.example", PublicClients.GraphCommandLineTools));

        var error = await Assert.ThrowsAsync<GraphTransportException>(() => provider.GetTokenAsync(GraphWrite, Ct).AsTask());

        Assert.True(error.IsNotAuthenticated);
        var consent = ConsentRequiredException.FindIn(error);
        Assert.NotNull(consent);
        Assert.Equal(GraphWrite, consent.Scopes);
        Assert.Equal("alice@contoso.example", consent.Account);
        Assert.StartsWith("Consent needed: your sign-in (alice@contoso.example) has not been granted Group.ReadWrite.All Organization.Read.All for https://graph.microsoft.com", error.Message, StringComparison.Ordinal);
        Assert.Contains("earm auth login --scopes Group.ReadWrite.All Organization.Read.All", error.Message, StringComparison.Ordinal);
        Assert.Contains("earm auth login -f <template>", error.Message, StringComparison.Ordinal);
        Assert.Contains("shared by every tool", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("--client-id", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("expired", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Guidance_ForKeyVault_NamesTheQualifiedScope_AndForACustomClient_KeepsTheClient()
    {
        var vault = new ConsentRequiredException(GraphCloud.Public.KeyVaultConsentScopes, "alice@contoso.example", CustomClient, null);

        var message = GraphAuthGuidance.ConsentNeeded(vault);

        Assert.Contains("earm auth login --scopes https://vault.azure.net/user_impersonation --client-id " + CustomClient, message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AutoMode_ConsentFailure_IsNotTurnedIntoExpired_ButExpiryStillIs()
    {
        var path = NewRecordPath();
        try
        {
            var store = new AuthRecordStore(path);
            await store.SaveAsync(Record(), Ct);
            var consentFactory = new Factory(store) { Interactive = new FakeCredential().Throws(ConsentFailure()) };
            var expiredFactory = new Factory(store) { Interactive = new FakeCredential().Throws(ExpiredFailure()) };

            var consent = await Assert.ThrowsAsync<GraphTransportException>(() => consentFactory.Create(new GraphAuthOptions()).GetTokenAsync(GraphWrite, Ct).AsTask());
            var expired = await Assert.ThrowsAsync<GraphTransportException>(() => expiredFactory.Create(new GraphAuthOptions()).GetTokenAsync(GraphWrite, Ct).AsTask());

            Assert.NotNull(ConsentRequiredException.FindIn(consent));
            Assert.Contains("Consent needed: your sign-in (alice@contoso.example)", consent.Message, StringComparison.Ordinal);
            Assert.Null(ConsentRequiredException.FindIn(expired));
            Assert.Contains("expired or can no longer be refreshed", expired.Message, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ---- interactive incremental consent -----------------------------------------------------------------------------

    [Fact]
    public async Task WithAPrompter_ConsentIsRequestedForExactlyTheMissingSet_ThenOneSilentRetrySucceeds()
    {
        var credential = new FakeCredential().Throws(ConsentFailure()).Returns("after-consent", DateTimeOffset.UtcNow.AddHours(1));
        var prompter = new FakePrompter();
        using var provider = new AzureIdentityTokenProvider(credential, GraphAuthMode.Auto, consentPrompter: prompter);

        var token = await provider.GetTokenAsync(GraphWrite, Ct);

        Assert.Equal("after-consent", token.Value);
        Assert.Equal([GraphWrite], prompter.Calls);
        Assert.Equal(2, credential.Calls);
        Assert.True(provider.PromptsForConsent);
    }

    [Fact]
    public async Task WithAPrompter_AStillMissingConsentAfterThePrompt_FailsWithoutASecondPrompt()
    {
        var credential = new FakeCredential().Throws(ConsentFailure()).Throws(ConsentFailure());
        var prompter = new FakePrompter();
        using var provider = new AzureIdentityTokenProvider(credential, GraphAuthMode.Auto, consentPrompter: prompter);

        var error = await Assert.ThrowsAsync<GraphTransportException>(() => provider.GetTokenAsync(GraphWrite, Ct).AsTask());

        Assert.Single(prompter.Calls);
        Assert.Equal(2, credential.Calls);
        Assert.NotNull(ConsentRequiredException.FindIn(error));
        Assert.Contains("still needs consent", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithAPrompter_ADeclinedPrompt_FailsAsConsentNeeded_WithoutRetrying()
    {
        var credential = new FakeCredential().Throws(ConsentFailure());
        var prompter = new FakePrompter { Failure = new AuthenticationFailedException("AADSTS65004: User declined to consent to access the app.") };
        using var provider = new AzureIdentityTokenProvider(credential, GraphAuthMode.Auto, consentPrompter: prompter);

        var error = await Assert.ThrowsAsync<GraphTransportException>(() => provider.GetTokenAsync(GraphWrite, Ct).AsTask());

        Assert.Equal(1, credential.Calls);
        Assert.Contains("was not completed", error.Message, StringComparison.Ordinal);
        Assert.Contains("AADSTS65004", error.Message, StringComparison.Ordinal);
        Assert.NotNull(ConsentRequiredException.FindIn(error));
    }

    [Fact]
    public async Task WithAPrompter_AFailureThatIsNotConsent_NeverPrompts()
    {
        var credential = new FakeCredential().Throws(ExpiredFailure());
        var prompter = new FakePrompter();
        using var provider = new AzureIdentityTokenProvider(credential, GraphAuthMode.Interactive, consentPrompter: prompter);

        await Assert.ThrowsAsync<GraphTransportException>(() => provider.GetTokenAsync(GraphWrite, Ct).AsTask());

        Assert.Empty(prompter.Calls);
    }

    [Fact]
    public async Task Factory_GivesThePrompterOnlyToAStoredInteractiveSignIn()
    {
        var path = NewRecordPath();
        try
        {
            var store = new AuthRecordStore(path);
            var factory = new Factory(store);
            var prompter = new FakePrompter();

            Assert.Null(factory.CreateConsentPrompter(new GraphAuthOptions(), false, null, null));
            Assert.False(((AzureIdentityTokenProvider)factory.Create(new GraphAuthOptions(), prompter)).PromptsForConsent);

            await store.SaveAsync(Record(), Ct);
            Assert.NotNull(factory.CreateConsentPrompter(new GraphAuthOptions(), false, null, null));
            Assert.Null(factory.CreateConsentPrompter(new GraphAuthOptions { Mode = GraphAuthMode.Default }, false, null, null));
            Assert.Null(factory.CreateConsentPrompter(new GraphAuthOptions { Cloud = GraphCloud.UsGov }, false, null, null));
            Assert.True(((AzureIdentityTokenProvider)factory.Create(new GraphAuthOptions(), prompter)).PromptsForConsent);
            Assert.False(((AzureIdentityTokenProvider)factory.Create(new GraphAuthOptions { Mode = GraphAuthMode.Default }, prompter)).PromptsForConsent);
            Assert.False(((AzureIdentityTokenProvider)factory.Create(new GraphAuthOptions())).PromptsForConsent);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task ThePrompter_AsksAsTheStoredAccount_ForExactlyTheScopes_AndRefusesAnotherAccount()
    {
        var path = NewRecordPath();
        try
        {
            var store = new AuthRecordStore(path);
            await store.SaveAsync(Record(), Ct);
            var factory = new Factory(store);
            var notices = new List<string>();
            var prompter = factory.CreateConsentPrompter(new GraphAuthOptions(), deviceCode: true, null, notices.Add)!;

            await prompter.ConsentAsync(GraphWrite, Ct);

            var request = Assert.Single(factory.Requests);
            Assert.Equal(GraphWrite, request.Scopes);
            Assert.Equal("oid.tid", request.Account!.HomeAccountId);
            Assert.True(request.DeviceCode);
            Assert.Contains("Group.ReadWrite.All", Assert.Single(notices), StringComparison.Ordinal);

            factory.Authenticator = (options, _, _) => Record(PublicClients.GraphCommandLineTools, "other.tid", "bob@contoso.example");
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => prompter.ConsentAsync(GraphWrite, Ct));
            Assert.Contains("bob@contoso.example", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void TheConsentOptions_UseTheRecordsClientAndTenant_AndTheAccountAsLoginHint()
    {
        var record = Record(CustomClient);

        var browser = GraphAuthFactory.BuildBrowserConsentOptions(new GraphAuthOptions(), record);
        var device = GraphAuthFactory.BuildDeviceCodeConsentOptions(new GraphAuthOptions(), record, null);

        Assert.Equal(CustomClient, browser.ClientId);
        Assert.Equal("11111111-1111-1111-1111-111111111111", browser.TenantId);
        Assert.Equal("alice@contoso.example", browser.LoginHint);
        Assert.False(browser.DisableAutomaticAuthentication);
        Assert.Equal(GraphAuthFactory.CacheName, browser.TokenCachePersistenceOptions!.Name);
        Assert.Equal(CustomClient, device.ClientId);
        Assert.Same(record, device.AuthenticationRecord);
    }

    // ---- migration warning -------------------------------------------------------------------------------------------

    [Fact]
    public async Task ARecordOfTheAzureCliApp_KeepsWorking_WithOneWarningRecommendingLogin()
    {
        var path = NewRecordPath();
        try
        {
            var store = new AuthRecordStore(path);
            await store.SaveAsync(Record(PublicClients.AzureCli), Ct);
            var sink = new LogSink();
            var factory = new Factory(store, new TestLogger<GraphAuthFactory>(sink));

            var token = await factory.Create(new GraphAuthOptions()).GetTokenAsync(GraphWrite, Ct);
            factory.Create(new GraphAuthOptions { Mode = GraphAuthMode.Interactive });

            Assert.Equal("interactive-token", token.Value);
            var warning = Assert.Single(sink.Entries, e => e.EventId.Id == 1047);
            Assert.Equal(LogLevel.Warning, warning.Level);
            Assert.Contains("earm auth login", warning.Message, StringComparison.Ordinal);
            Assert.Contains(PublicClients.AzureCli, warning.Message, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Theory]
    [InlineData(PublicClients.GraphCommandLineTools, null)]
    [InlineData(PublicClients.AzureCli, CustomClient)]
    public async Task NoWarning_ForTheDefaultClient_OrAnExplicitClientId(string recordClient, string? explicitClient)
    {
        var path = NewRecordPath();
        try
        {
            var store = new AuthRecordStore(path);
            await store.SaveAsync(Record(recordClient), Ct);
            var sink = new LogSink();
            var factory = new Factory(store, new TestLogger<GraphAuthFactory>(sink));

            factory.Create(new GraphAuthOptions { ClientId = explicitClient });

            Assert.DoesNotContain(sink.Entries, e => e.EventId.Id == 1047);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ---- status ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Status_WithRequired_ListsTheScopesTheTokenLacks_CaseInsensitively()
    {
        var status = new GraphAuthStatus { Mode = "auto", Credential = "interactive sign-in", Cloud = "public", ClientId = PublicClients.GraphCommandLineTools, Scopes = ["user.read", "Organization.Read.All"] };

        var checkedStatus = status.WithRequired(["Group.ReadWrite.All", "Organization.Read.All", "User.Read"], keyVaultNeeded: true);

        Assert.Equal(["Group.ReadWrite.All"], checkedStatus.MissingScopes);
        Assert.True(checkedStatus.KeyVaultNeeded);
        Assert.Equal("Microsoft Graph Command Line Tools", checkedStatus.ClientName);
        Assert.Null(status.RequiredScopes);
    }
}
