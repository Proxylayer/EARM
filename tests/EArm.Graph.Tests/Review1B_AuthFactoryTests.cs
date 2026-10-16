using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Identity;
using EArm.Graph.Auth;
using Microsoft.Extensions.Logging;
using EArm.Graph.Tests.Infrastructure;
using Xunit;

namespace EArm.Graph.Tests;

/// <summary>
/// Review findings S-08 and Q-15: no silent switch of identity in <c>auto</c> mode, the identity that was used is logged, and the credential factory
/// (mode selection, the process-credential allow-list, tenant / client id / authority plumbing, login and logout) is covered without any network or browser.
/// </summary>
[Collection(Review1BEnvironmentCollection.Name)]
public sealed class Review1B_AuthFactoryTests
{
    private static readonly TokenScopes Scope = TokenScopes.Default("https://graph.microsoft.com");

    private static readonly string[] AllowedChain = ["EnvironmentCredential", "WorkloadIdentityCredential", "ManagedIdentityCredential"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewRecordPath()
    {
        return Path.Combine(Path.GetTempPath(), "earm-r1b-auth-" + Guid.NewGuid().ToString("N"), "auth", "record.json");
    }

    private static void Cleanup(string recordPath)
    {
        var root = Path.GetDirectoryName(Path.GetDirectoryName(recordPath));
        if (root is not null && Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>A factory whose credentials are fakes; records which ones were built. Everything else (mode selection, options) is the real code.</summary>
    private sealed class Factory : GraphAuthFactory
    {
        public Factory(AuthRecordStore store, ILogger? logger = null)
            : base(store, logger)
        {
        }

        public List<string> Created { get; } = [];

        public TokenCredential? Interactive { get; set; }

        public TokenCredential? Chain { get; set; }

        public Func<bool, Func<DeviceCodeInfo, CancellationToken, Task>?, Task<AuthenticationRecord>>? Authenticator { get; set; }

        public bool? LastDeviceCode { get; private set; }

        public override TokenCredential CreateDefaultChain(GraphAuthOptions options)
        {
            Created.Add("default");
            return Chain ?? new FakeCredential().Returns("chain-token", DateTimeOffset.UtcNow.AddHours(1));
        }

        protected override TokenCredential CreateInteractive(GraphAuthOptions options, AuthenticationRecord record)
        {
            Created.Add("interactive");
            return Interactive ?? new FakeCredential().Returns("interactive-token", DateTimeOffset.UtcNow.AddHours(1));
        }

        internal override Task<AuthenticationRecord> AuthenticateAsync(GraphAuthOptions options, bool deviceCode, Func<DeviceCodeInfo, CancellationToken, Task>? deviceCodeCallback, CancellationToken cancellationToken)
        {
            LastDeviceCode = deviceCode;
            return Authenticator is null ? Task.FromResult(AuthTests.Record()) : Authenticator(deviceCode, deviceCodeCallback);
        }
    }

    private static string Jwt(string payloadJson)
    {
        static string B64(string text)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        return B64("{\"alg\":\"none\"}") + "." + B64(payloadJson) + ".signature";
    }

    // ---- S-08: no silent fall back from a stored sign-in -----------------------------------------------------------

    [Fact]
    public async Task AutoWithARecordThatCannotRefreshFailsWithGuidanceAndNeverTriesTheDefaultChain()
    {
        var path = NewRecordPath();
        try
        {
            var store = new AuthRecordStore(path);
            await store.SaveAsync(AuthTests.Record(), Ct);
            var factory = new Factory(store)
            {
                Interactive = new FakeCredential().Throws(new CredentialUnavailableException("The refresh token has expired due to inactivity. AADSTS700082")),
                Chain = new FakeCredential().Returns("a-different-identity", DateTimeOffset.UtcNow.AddHours(1)),
            };

            var provider = factory.Create(new GraphAuthOptions { Mode = GraphAuthMode.Auto });
            var error = await Assert.ThrowsAsync<GraphTransportException>(() => provider.GetTokenAsync(Scope, Ct).AsTask());

            Assert.Equal(GraphTransportFailure.NotAuthenticated, error.Failure);
            Assert.Contains("interactive sign-in", error.Message, StringComparison.Ordinal);
            Assert.Contains("alice@contoso.example", error.Message, StringComparison.Ordinal);
            Assert.Contains("earm auth login", error.Message, StringComparison.Ordinal);
            Assert.Contains("--auth default", error.Message, StringComparison.Ordinal);
            Assert.Contains("EARM_AUTH=default", error.Message, StringComparison.Ordinal);
            Assert.Contains("AADSTS700082", error.Message, StringComparison.Ordinal);
            Assert.Contains("does not fall back", error.Message, StringComparison.Ordinal);
            Assert.Equal(["interactive"], factory.Created);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Theory]
    [InlineData(typeof(CredentialUnavailableException))]
    [InlineData(typeof(AuthenticationFailedException))]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(IOException))]
    public async Task EveryKindOfSilentFailureOfTheStoredSignInIsTreatedTheSame(Type exceptionType)
    {
        var path = NewRecordPath();
        try
        {
            var store = new AuthRecordStore(path);
            await store.SaveAsync(AuthTests.Record(), Ct);
            var factory = new Factory(store) { Interactive = new FakeCredential().Throws((Exception)Activator.CreateInstance(exceptionType, "boom")!) };

            var error = await Assert.ThrowsAsync<GraphTransportException>(() => factory.Create(new GraphAuthOptions()).GetTokenAsync(Scope, Ct).AsTask());

            Assert.Contains("earm auth login", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("default", factory.Created);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task CancellingTheSilentSignInIsStillACancellationAndNotAnExpiredSignIn()
    {
        var path = NewRecordPath();
        try
        {
            var store = new AuthRecordStore(path);
            await store.SaveAsync(AuthTests.Record(), Ct);
            var factory = new Factory(store) { Interactive = new FakeCredential().Throws(new OperationCanceledException()) };
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => factory.Create(new GraphAuthOptions()).GetTokenAsync(Scope, cts.Token).AsTask());
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task AutoWithAWorkingRecordUsesOnlyTheInteractiveSignIn()
    {
        var path = NewRecordPath();
        try
        {
            var store = new AuthRecordStore(path);
            await store.SaveAsync(AuthTests.Record(), Ct);
            var factory = new Factory(store);

            var token = await factory.Create(new GraphAuthOptions()).GetTokenAsync(Scope, Ct);

            Assert.Equal("interactive-token", token.Value);
            Assert.Equal(["interactive"], factory.Created);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task DefaultModeIgnoresTheRecordAndTheEscapeHatchWorksWhenTheSignInIsBroken()
    {
        var path = NewRecordPath();
        try
        {
            var store = new AuthRecordStore(path);
            await store.SaveAsync(AuthTests.Record(), Ct);
            var factory = new Factory(store) { Interactive = new FakeCredential().Throws(new CredentialUnavailableException("expired")) };

            var token = await factory.Create(new GraphAuthOptions { Mode = GraphAuthMode.Default }).GetTokenAsync(Scope, Ct);

            Assert.Equal("chain-token", token.Value);
            Assert.Equal(["default"], factory.Created);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task InteractiveModeKeepsItsOwnGuidanceWhenTheSignInFails()
    {
        var path = NewRecordPath();
        try
        {
            var store = new AuthRecordStore(path);
            await store.SaveAsync(AuthTests.Record(), Ct);
            var factory = new Factory(store) { Interactive = new FakeCredential().Throws(new CredentialUnavailableException("expired")) };

            var error = await Assert.ThrowsAsync<GraphTransportException>(() => factory.Create(new GraphAuthOptions { Mode = GraphAuthMode.Interactive }).GetTokenAsync(Scope, Ct).AsTask());

            Assert.Contains("Run 'earm auth login'", error.Message, StringComparison.Ordinal);
            Assert.Equal(["interactive"], factory.Created);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task ARecordOfAnotherCloudInAutoModeFallsBackToTheDefaultChainAndSaysSo()
    {
        var path = NewRecordPath();
        try
        {
            var store = new AuthRecordStore(path);
            await store.SaveAsync(AuthTests.Record("login.microsoftonline.us"), Ct);
            var log = new LogSink();
            var factory = new Factory(store, new TestLogger<GraphAuthFactory>(log));

            var token = await factory.Create(new GraphAuthOptions { Cloud = GraphCloud.Public }).GetTokenAsync(Scope, Ct);

            Assert.Equal("chain-token", token.Value);
            var line = Assert.Single(log.Entries, static e => e.EventId.Id == 1045);
            Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Information, line.Level);
            Assert.Contains("login.microsoftonline.us", line.Message, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ---- the identity is logged ------------------------------------------------------------------------------------

    [Fact]
    public async Task TheIdentityOfTheFirstTokenIsLoggedOnceAtInformationLevelWithoutTheToken()
    {
        var log = new LogSink();
        var token = Jwt("{\"tid\":\"11111111-1111-1111-1111-111111111111\",\"oid\":\"22222222-2222-2222-2222-222222222222\",\"upn\":\"alice@contoso.example\",\"appid\":\"33333333-3333-3333-3333-333333333333\",\"scp\":\"Directory.Read.All\"}");
        var credential = new FakeCredential().Returns(token, DateTimeOffset.UtcNow.AddHours(1)).Returns(token, DateTimeOffset.UtcNow.AddHours(1));
        using var provider = new AzureIdentityTokenProvider(credential, GraphAuthMode.Default, logger: new TestLogger<AzureIdentityTokenProvider>(log));

        await provider.GetTokenAsync(Scope, Ct);
        await provider.GetTokenAsync(TokenScopes.Default("https://vault.azure.net"), Ct);

        var line = Assert.Single(log.Entries, static e => e.EventId.Id == 1043);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Information, line.Level);
        Assert.Contains("alice@contoso.example", line.Message, StringComparison.Ordinal);
        Assert.Contains("11111111-1111-1111-1111-111111111111", line.Message, StringComparison.Ordinal);
        Assert.Contains("22222222-2222-2222-2222-222222222222", line.Message, StringComparison.Ordinal);
        Assert.Contains("33333333-3333-3333-3333-333333333333", line.Message, StringComparison.Ordinal);
        Assert.Contains("FakeCredential", line.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(token, log.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("signature", log.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnApplicationTokenIsLoggedAsAnApplication()
    {
        var log = new LogSink();
        var token = Jwt("{\"tid\":\"t\",\"oid\":\"o\",\"appid\":\"app-guid\",\"idtyp\":\"app\",\"roles\":[\"Directory.ReadWrite.All\"]}");
        using var provider = new AzureIdentityTokenProvider(new FakeCredential().Returns(token, DateTimeOffset.UtcNow.AddHours(1)), GraphAuthMode.Default, logger: new TestLogger<AzureIdentityTokenProvider>(log));

        await provider.GetTokenAsync(Scope, Ct);

        var line = Assert.Single(log.Entries, static e => e.EventId.Id == 1043);
        Assert.Contains("app-guid (application)", line.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATokenWithoutReadableClaimsIsLoggedWithoutAnIdentity()
    {
        var log = new LogSink();
        using var provider = new AzureIdentityTokenProvider(new FakeCredential().Returns("opaque-token-value", DateTimeOffset.UtcNow.AddHours(1)), GraphAuthMode.Default, logger: new TestLogger<AzureIdentityTokenProvider>(log));

        await provider.GetTokenAsync(Scope, Ct);

        Assert.DoesNotContain(log.Entries, static e => e.EventId.Id == 1043);
        var line = Assert.Single(log.Entries, static e => e.EventId.Id == 1044);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Information, line.Level);
        Assert.DoesNotContain("opaque-token-value", log.AllText, StringComparison.Ordinal);
    }

    // ---- the default chain ---------------------------------------------------------------------------------------

    private static string[] ChainSources(TokenCredential chain)
    {
        var field = typeof(DefaultAzureCredential).GetField("_sources", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return [.. ((TokenCredential[])field.GetValue(chain)!).Select(static c => c.GetType().Name)];
    }

    [Fact]
    public void TheDefaultChainContainsExactlyEnvironmentWorkloadIdentityAndManagedIdentity()
    {
        using var env = new EnvironmentScope().Set("AZURE_TOKEN_CREDENTIALS", null);
        var factory = new GraphAuthFactory(new AuthRecordStore(NewRecordPath()));

        var sources = ChainSources(factory.CreateDefaultChain(new GraphAuthOptions()));

        Assert.Equal(AllowedChain, sources);
        foreach (var forbidden in new[] { "AzureCliCredential", "AzureDeveloperCliCredential", "AzurePowerShellCredential", "VisualStudioCredential", "VisualStudioCodeCredential", "BrokerCredential", "SharedTokenCacheCredential", "InteractiveBrowserCredential" })
        {
            Assert.DoesNotContain(forbidden, sources);
        }
    }

    [Fact]
    public void EveryExcludeSwitchOfTheChainOptionsIsSetExceptForTheThreeAllowedCredentials()
    {
        var options = GraphAuthFactory.BuildDefaultChainOptions(new GraphAuthOptions());
        var switches = typeof(DefaultAzureCredentialOptions).GetProperties()
            .Where(static p => p.PropertyType == typeof(bool) && p.Name.StartsWith("Exclude", StringComparison.Ordinal) && p.Name.EndsWith("Credential", StringComparison.Ordinal))
            .ToList();

        Assert.True(switches.Count >= 10, "Azure.Identity offers the Exclude* switches this test is guarding");
        foreach (var property in switches)
        {
            var allowed = AllowedChain.Select(static n => "Exclude" + n).Contains(property.Name);
            Assert.Equal(!allowed, (bool)property.GetValue(options)!);
        }
    }

    [Theory]
    [InlineData("public", "https://login.microsoftonline.com/")]
    [InlineData("usgov", "https://login.microsoftonline.us/")]
    [InlineData("usgovdod", "https://login.microsoftonline.us/")]
    [InlineData("china", "https://login.chinacloudapi.cn/")]
    public void TheAuthorityHostComesFromTheCloudTableAndNotFromTheEnvironment(string cloudKey, string expectedAuthority)
    {
        using var env = new EnvironmentScope().Set("AZURE_AUTHORITY_HOST", "https://evil.example/");
        var cloud = GraphCloud.Parse(cloudKey);
        var auth = new GraphAuthOptions { Cloud = cloud };

        Assert.Equal(expectedAuthority, GraphAuthFactory.BuildDefaultChainOptions(auth).AuthorityHost.AbsoluteUri);
        Assert.Equal(expectedAuthority, GraphAuthFactory.BuildInteractiveOptions(auth, AuthTests.Record()).AuthorityHost.AbsoluteUri);
        Assert.Equal(expectedAuthority, GraphAuthFactory.BuildBrowserLoginOptions(auth).AuthorityHost.AbsoluteUri);
        Assert.Equal(expectedAuthority, GraphAuthFactory.BuildDeviceCodeLoginOptions(auth, null).AuthorityHost.AbsoluteUri);
    }

    [Fact]
    public void TenantAndClientIdArePlumbedIntoEveryCredentialOptionsObject()
    {
        var auth = new GraphAuthOptions { TenantId = "contoso.example", ClientId = "44444444-4444-4444-4444-444444444444" };
        var record = AuthTests.Record();

        var chain = GraphAuthFactory.BuildDefaultChainOptions(auth);
        var interactive = GraphAuthFactory.BuildInteractiveOptions(auth, record);
        var browser = GraphAuthFactory.BuildBrowserLoginOptions(auth);
        var device = GraphAuthFactory.BuildDeviceCodeLoginOptions(auth, null);

        Assert.Equal("contoso.example", chain.TenantId);
        Assert.Equal("contoso.example", interactive.TenantId);
        Assert.Equal("contoso.example", browser.TenantId);
        Assert.Equal("contoso.example", device.TenantId);
        Assert.Equal("44444444-4444-4444-4444-444444444444", interactive.ClientId);
        Assert.Equal("44444444-4444-4444-4444-444444444444", browser.ClientId);
        Assert.Equal("44444444-4444-4444-4444-444444444444", device.ClientId);
    }

    [Fact]
    public void WithoutTenantOrClientIdTheCredentialsKeepTheirOwnDefaults()
    {
        var auth = new GraphAuthOptions();
        var record = AuthTests.Record();

        Assert.Null(GraphAuthFactory.BuildDefaultChainOptions(auth).TenantId);
        // Review 3I (intentional change): the interactive credential defaults client and tenant to the ones the stored sign-in record names.
        Assert.Equal(record.TenantId, GraphAuthFactory.BuildInteractiveOptions(auth, record).TenantId);
        Assert.Equal(record.ClientId, GraphAuthFactory.BuildInteractiveOptions(auth, record).ClientId);
        Assert.Null(GraphAuthFactory.BuildBrowserLoginOptions(auth).TenantId);
        Assert.Null(GraphAuthFactory.BuildDeviceCodeLoginOptions(auth, null).TenantId);
    }

    [Fact]
    public void TheCachedSignInNeverOpensABrowserAndUsesTheEncryptedEarmCache()
    {
        var record = AuthTests.Record();
        var interactive = GraphAuthFactory.BuildInteractiveOptions(new GraphAuthOptions(), record);

        Assert.True(interactive.DisableAutomaticAuthentication);
        Assert.Same(record, interactive.AuthenticationRecord);
        Assert.Equal(GraphAuthFactory.CacheName, interactive.TokenCachePersistenceOptions!.Name);
        Assert.False(interactive.TokenCachePersistenceOptions.UnsafeAllowUnencryptedStorage);

        var browser = GraphAuthFactory.BuildBrowserLoginOptions(new GraphAuthOptions());
        Assert.Equal(GraphAuthFactory.CacheName, browser.TokenCachePersistenceOptions!.Name);
        Assert.False(browser.TokenCachePersistenceOptions.UnsafeAllowUnencryptedStorage);
        Assert.False(browser.DisableAutomaticAuthentication);

        var device = GraphAuthFactory.BuildDeviceCodeLoginOptions(new GraphAuthOptions(), null);
        Assert.Equal(GraphAuthFactory.CacheName, device.TokenCachePersistenceOptions!.Name);
        Assert.False(device.TokenCachePersistenceOptions.UnsafeAllowUnencryptedStorage);
    }

    [Fact]
    public void TheDeviceCodeCallbackIsPassedThroughOrDefaultsToTheConsole()
    {
        Func<DeviceCodeInfo, CancellationToken, Task> show = static (_, _) => Task.CompletedTask;

        Assert.Same(show, GraphAuthFactory.BuildDeviceCodeLoginOptions(new GraphAuthOptions(), show).DeviceCodeCallback);
        Assert.NotNull(GraphAuthFactory.BuildDeviceCodeLoginOptions(new GraphAuthOptions(), null).DeviceCodeCallback);
    }
    // ---- AZURE_TOKEN_CREDENTIALS allow-list ----------------------------------------------------------------------

    [Theory]
    [InlineData("prod")]
    [InlineData("PROD")]
    [InlineData(" prod ")]
    [InlineData("EnvironmentCredential")]
    [InlineData("workloadidentitycredential")]
    [InlineData("ManagedIdentityCredential")]
    [InlineData("")]
    public void ASafeTokenCredentialsValueIsAccepted(string value)
    {
        using var env = new EnvironmentScope().Set("AZURE_TOKEN_CREDENTIALS", value);
        var factory = new GraphAuthFactory(new AuthRecordStore(NewRecordPath()));

        var chain = factory.CreateDefaultChain(new GraphAuthOptions());

        Assert.IsType<DefaultAzureCredential>(chain);
    }

    [Fact]
    public void ABlankTokenCredentialsValueIsRefusedWithAClearMessage()
    {
        using var env = new EnvironmentScope().Set("AZURE_TOKEN_CREDENTIALS", "   ");
        var factory = new GraphAuthFactory(new AuthRecordStore(NewRecordPath()));

        var error = Assert.Throws<InvalidOperationException>(() => factory.CreateDefaultChain(new GraphAuthOptions()));

        Assert.Contains("blank value", error.Message, StringComparison.Ordinal);
        Assert.Contains("Unset it", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("dev")]
    [InlineData("DEV")]
    [InlineData("AzureCliCredential")]
    [InlineData("AzureDeveloperCliCredential")]
    [InlineData("AzurePowerShellCredential")]
    [InlineData("VisualStudioCredential")]
    [InlineData("VisualStudioCodeCredential")]
    [InlineData("BrokerCredential")]
    [InlineData("InteractiveBrowserCredential")]
    [InlineData("SharedTokenCacheCredential")]
    [InlineData("EnvironmentCredential,AzureCliCredential")]
    [InlineData("prod,dev")]
    [InlineData("0")]
    [InlineData("true")]
    public void AnythingOutsideTheAllowListIsRefusedWithTheOffendingValueInTheMessage(string value)
    {
        using var env = new EnvironmentScope().Set("AZURE_TOKEN_CREDENTIALS", value);
        var factory = new GraphAuthFactory(new AuthRecordStore(NewRecordPath()));

        var error = Assert.Throws<InvalidOperationException>(() => factory.CreateDefaultChain(new GraphAuthOptions()));

        Assert.Contains("AZURE_TOKEN_CREDENTIALS", error.Message, StringComparison.Ordinal);
        Assert.Contains("'" + value + "'", error.Message, StringComparison.Ordinal);
        Assert.Contains("external process", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGuardAppliesToEveryModeThatBuildsTheDefaultChain()
    {
        using var env = new EnvironmentScope().Set("AZURE_TOKEN_CREDENTIALS", "dev");
        var factory = new GraphAuthFactory(new AuthRecordStore(NewRecordPath()));

        Assert.Throws<InvalidOperationException>(() => factory.Create(new GraphAuthOptions { Mode = GraphAuthMode.Default }));
        Assert.Throws<InvalidOperationException>(() => factory.Create(new GraphAuthOptions { Mode = GraphAuthMode.Auto }));
        // Interactive mode never builds the chain, so the variable is irrelevant there.
        Assert.NotNull(factory.Create(new GraphAuthOptions { Mode = GraphAuthMode.Interactive }));
    }

    // ---- mode selection through the real CreateCredential --------------------------------------------------------

    [Fact]
    public async Task TheRealCredentialSelectionPicksTheRightKindForEveryMode()
    {
        var path = NewRecordPath();
        try
        {
            using var env = new EnvironmentScope().Set("AZURE_TOKEN_CREDENTIALS", null);
            var store = new AuthRecordStore(path);
            var factory = new GraphAuthFactory(store);

            Assert.IsType<DefaultAzureCredential>(factory.CreateCredential(new GraphAuthOptions { Mode = GraphAuthMode.Default }));
            Assert.IsType<DefaultAzureCredential>(factory.CreateCredential(new GraphAuthOptions { Mode = GraphAuthMode.Auto }));
            Assert.Equal("UnavailableCredential", factory.CreateCredential(new GraphAuthOptions { Mode = GraphAuthMode.Interactive }).GetType().Name);

            await store.SaveAsync(AuthTests.Record(), Ct);

            Assert.IsType<DefaultAzureCredential>(factory.CreateCredential(new GraphAuthOptions { Mode = GraphAuthMode.Default }));
            Assert.IsType<InteractiveBrowserCredential>(factory.CreateCredential(new GraphAuthOptions { Mode = GraphAuthMode.Interactive }));
            Assert.Equal("InteractiveOnlyCredential", factory.CreateCredential(new GraphAuthOptions { Mode = GraphAuthMode.Auto }).GetType().Name);
            Assert.Equal("UnavailableCredential", factory.CreateCredential(new GraphAuthOptions { Mode = GraphAuthMode.Interactive, Cloud = GraphCloud.UsGov }).GetType().Name);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void ARecordStoreWithoutAPathUsesTheDefaultLocationAndAnExplicitFactoryKeepsItsStore()
    {
        var store = new AuthRecordStore(NewRecordPath());

        Assert.Same(store, new GraphAuthFactory(store).RecordStore);
        Assert.Equal(AuthRecordStore.DefaultPath(), new GraphAuthFactory().RecordStore.FilePath);
    }

    // ---- login ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ASuccessfulLoginStoresTheRecordAndPassesTheFlowThrough(bool deviceCode)
    {
        var path = NewRecordPath();
        try
        {
            var store = new AuthRecordStore(path);
            var factory = new Factory(store);

            var record = await factory.LoginAsync(new GraphAuthOptions(), deviceCode, null, Ct);

            Assert.Equal("alice@contoso.example", record.Username);
            Assert.Equal(deviceCode, factory.LastDeviceCode);
            Assert.True(store.Exists);
            Assert.Equal("alice@contoso.example", store.TryLoad()!.Username);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task AFailedLoginIsAnAuthenticationFailureWithTheReasonAndNoRecord()
    {
        var path = NewRecordPath();
        try
        {
            var store = new AuthRecordStore(path);
            var factory = new Factory(store) { Authenticator = (_, _) => throw new AuthenticationFailedException("AADSTS50126: Invalid username or password.") };

            var error = await Assert.ThrowsAsync<GraphTransportException>(() => factory.LoginAsync(new GraphAuthOptions(), false, null, Ct));

            Assert.Equal(GraphTransportFailure.NotAuthenticated, error.Failure);
            Assert.StartsWith("Sign-in failed:", error.Message, StringComparison.Ordinal);
            Assert.Contains("AADSTS50126", error.Message, StringComparison.Ordinal);
            Assert.False(store.Exists);
            Assert.IsType<AuthenticationFailedException>(error.InnerException);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Theory]
    [InlineData("The persistence layer could not be initialised")]
    [InlineData("libsecret is not available")]
    [InlineData("DPAPI failure")]
    [InlineData("could not encrypt the cache")]
    [InlineData("keychain denied")]
    public async Task ALoginThatCannotGetEncryptedStorageSaysSoAndNeverSuggestsUnencryptedTokens(string reason)
    {
        var path = NewRecordPath();
        try
        {
            var factory = new Factory(new AuthRecordStore(path)) { Authenticator = (_, _) => throw new InvalidOperationException(reason) };

            var error = await Assert.ThrowsAsync<GraphTransportException>(() => factory.LoginAsync(new GraphAuthOptions(), true, null, Ct));

            Assert.Contains("secure (encrypted) token storage", error.Message, StringComparison.Ordinal);
            Assert.Contains("never stores tokens unencrypted", error.Message, StringComparison.Ordinal);
            Assert.Contains("service principal", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task ACancelledLoginStaysACancellation()
    {
        var path = NewRecordPath();
        try
        {
            var factory = new Factory(new AuthRecordStore(path)) { Authenticator = (_, _) => throw new OperationCanceledException() };
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => factory.LoginAsync(new GraphAuthOptions(), false, null, cts.Token));
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task ALoginFailureNeverEchoesASecretFromTheProvider()
    {
        var path = NewRecordPath();
        try
        {
            var factory = new Factory(new AuthRecordStore(path)) { Authenticator = (_, _) => throw new AuthenticationFailedException("AADSTS7000215: Invalid client secret provided. client_secret=Sup3rS3cret") };

            var error = await Assert.ThrowsAsync<GraphTransportException>(() => factory.LoginAsync(new GraphAuthOptions(), false, null, Ct));

            Assert.DoesNotContain("Sup3rS3cret", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ---- guidance text ---------------------------------------------------------------------------------------------

    [Fact]
    public void TheDefaultGuidanceListsEveryWayToSignIn()
    {
        var text = GraphAuthGuidance.NotSignedIn(GraphAuthMode.Default, Scope.ToString(), null);

        Assert.Contains(Scope.ToString(), text, StringComparison.Ordinal);
        Assert.DoesNotContain("Credentials tried", text, StringComparison.Ordinal);
        foreach (var fragment in new[] { "AZURE_TENANT_ID", "AZURE_CLIENT_ID", "AZURE_CLIENT_SECRET", "AZURE_CLIENT_CERTIFICATE_PATH", "AZURE_FEDERATED_TOKEN_FILE", "managed identity", "earm auth login", "--device-code" })
        {
            Assert.Contains(fragment, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheInteractiveGuidanceOnlyPointsAtAuthLogin()
    {
        var text = GraphAuthGuidance.NotSignedIn(GraphAuthMode.Interactive, Scope.ToString(), null);

        Assert.Contains("Run 'earm auth login'", text, StringComparison.Ordinal);
        Assert.DoesNotContain("service principal", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCredentialLinesAreRedactedShortenedAndFreeOfUrls()
    {
        var failure = new CredentialUnavailableException(
            "DefaultAzureCredential failed to retrieve a token from the included credentials. See the troubleshooting guide for more information. https://aka.ms/azsdk/net/identity/defaultazurecredential/troubleshoot\n"
            + "- EnvironmentCredential authentication unavailable. Environment variables are not fully configured. client_secret=Sup3rS3cret https://aka.ms/x\n"
            + "- ManagedIdentityCredential authentication unavailable, no response from the IMDS endpoint. " + new string('z', 400));

        var lines = GraphAuthGuidance.CredentialLines(failure);

        Assert.Equal(2, lines.Count);
        Assert.StartsWith("EnvironmentCredential authentication unavailable", lines[0], StringComparison.Ordinal);
        Assert.DoesNotContain("Sup3rS3cret", lines[0], StringComparison.Ordinal);
        Assert.DoesNotContain("https://", lines[0], StringComparison.Ordinal);
        Assert.True(lines[1].Length <= 243, "long lines are cut at 240 characters plus an ellipsis");
        Assert.EndsWith("...", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnstructuredFailureFallsBackToItsFirstLineAndAtMostTwelveLinesAreKept()
    {
        Assert.Equal(["plain failure"], GraphAuthGuidance.CredentialLines(new InvalidOperationException("plain failure\nsecond line")));

        var many = new InvalidOperationException(string.Join('\n', Enumerable.Range(0, 30).Select(static i => "- Credential" + i + "Credential authentication unavailable")));
        Assert.True(GraphAuthGuidance.CredentialLines(many).Count <= 12);
    }

    [Fact]
    public void AFailedCredentialReasonOnTheInnerExceptionIsAttached()
    {
        var failure = new AuthenticationFailedException("ClientSecretCredential authentication failed:", new InvalidOperationException("AADSTS90002: Tenant 'x' not found. Trace ID: abc"));

        var line = Assert.Single(GraphAuthGuidance.CredentialLines(failure));

        Assert.Contains("AADSTS90002", line, StringComparison.Ordinal);
        Assert.DoesNotContain("Trace ID", line, StringComparison.Ordinal);
    }

    [Fact]
    public void TheExpiredSignInGuidanceNamesBothWaysOutAndHidesNothingButSecrets()
    {
        var text = GraphAuthGuidance.NotSignedIn(GraphAuthMode.Auto, Scope.ToString(), new InteractiveSignInExpiredException("bob@contoso.example", new CredentialUnavailableException("refresh token expired. client_secret=Sup3rS3cret")));

        Assert.Contains("bob@contoso.example", text, StringComparison.Ordinal);
        Assert.Contains("earm auth login", text, StringComparison.Ordinal);
        Assert.Contains("--auth default", text, StringComparison.Ordinal);
        Assert.Contains("refresh token expired", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Sup3rS3cret", text, StringComparison.Ordinal);
    }

    // ---- logout ------------------------------------------------------------------------------------------------

    [Fact]
    public void LogoutWithoutAnythingStoredDeletesNothing()
    {
        var cache = Path.Combine(Path.GetTempPath(), "earm-r1b-cache-" + Guid.NewGuid().ToString("N"));
        var factory = new GraphAuthFactory(new AuthRecordStore(NewRecordPath()), cacheDirectories: [cache, Path.Combine(cache, "missing")]);

        var result = factory.Logout();

        Assert.False(result.RecordDeleted);
        Assert.Equal(0, result.CacheFilesDeleted);
    }

    [Fact]
    public void LogoutKeepsGoingWhenACacheFileIsLocked()
    {
        var cache = Path.Combine(Path.GetTempPath(), "earm-r1b-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cache);
        try
        {
            var locked = Path.Combine(cache, "earm");
            File.WriteAllText(locked, "x");
            File.WriteAllText(Path.Combine(cache, "earm.cae"), "x");
            var factory = new GraphAuthFactory(new AuthRecordStore(NewRecordPath()), cacheDirectories: [cache]);

            using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var result = factory.Logout();

                // The locked file survives (best effort), the other one is removed, nothing throws.
                Assert.Equal(OperatingSystem.IsWindows() ? 1 : 2, result.CacheFilesDeleted);
            }
        }
        finally
        {
            Directory.Delete(cache, recursive: true);
        }
    }
}
