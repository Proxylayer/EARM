using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Identity;
using EArm.Graph.Auth;
using EArm.Graph.Tests.Infrastructure;
using Xunit;

namespace EArm.Graph.Tests;

/// <summary>Token caching, token claims, sign-in options, the credential chain, the record store and the guidance text.</summary>
[Collection(Review1BEnvironmentCollection.Name)]
public sealed class AuthTests
{
    private const string Scope = "https://graph.microsoft.com/.default";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Jwt(string payloadJson)
    {
        static string B64(string text)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        return B64("{\"alg\":\"none\"}") + "." + B64(payloadJson) + ".signature";
    }

    // ---- AzureIdentityTokenProvider ---------------------------------------------------------------------------

    [Fact]
    public async Task ATokenIsCachedUntilFiveMinutesBeforeItExpires()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero));
        var credential = new FakeCredential()
            .Returns("first", clock.GetUtcNow().AddMinutes(30))
            .Returns("second", clock.GetUtcNow().AddMinutes(90));
        var provider = new AzureIdentityTokenProvider(credential, GraphAuthMode.Default, clock);

        Assert.Equal("first", (await provider.GetTokenAsync(Scope, Ct)).Value);
        clock.Advance(TimeSpan.FromMinutes(24));
        Assert.Equal("first", (await provider.GetTokenAsync(Scope, Ct)).Value);
        Assert.Equal(1, credential.Calls);

        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal("second", (await provider.GetTokenAsync(Scope, Ct)).Value);
        Assert.Equal(2, credential.Calls);
    }

    [Fact]
    public async Task TokensAreCachedPerScopeAndInvalidateDropsOnlyThatScope()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero));
        var credential = new FakeCredential()
            .Returns("graph-1", clock.GetUtcNow().AddHours(1))
            .Returns("vault-1", clock.GetUtcNow().AddHours(1))
            .Returns("graph-2", clock.GetUtcNow().AddHours(1));
        var provider = new AzureIdentityTokenProvider(credential, GraphAuthMode.Default, clock);

        Assert.Equal("graph-1", (await provider.GetTokenAsync(Scope, Ct)).Value);
        Assert.Equal("vault-1", (await provider.GetTokenAsync("https://vault.azure.net/.default", Ct)).Value);
        provider.Invalidate(Scope);

        Assert.Equal("graph-2", (await provider.GetTokenAsync(Scope, Ct)).Value);
        Assert.Equal("vault-1", (await provider.GetTokenAsync("https://vault.azure.net/.default", Ct)).Value);
        Assert.Equal(3, credential.Calls);
        Assert.Equal([Scope, "https://vault.azure.net/.default", Scope], credential.Scopes);
    }

    [Fact]
    public async Task ConcurrentCallersForOneScopeShareOneCredentialCall()
    {
        var credential = new FakeCredential().Returns("only", DateTimeOffset.UtcNow.AddHours(1));
        var provider = new AzureIdentityTokenProvider(credential);

        var tokens = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => provider.GetTokenAsync(Scope, Ct).AsTask()));

        Assert.All(tokens, static t => Assert.Equal("only", t.Value));
        Assert.Equal(1, credential.Calls);
    }

    [Fact]
    public async Task ACredentialFailureBecomesNotAuthenticatedWithGuidanceAndIsNotCached()
    {
        var credential = new FakeCredential()
            .Throws(new CredentialUnavailableException("DefaultAzureCredential failed to retrieve a token from the included credentials. See the troubleshooting guide for more information. https://aka.ms/x\n- EnvironmentCredential authentication unavailable. Environment variables are not fully configured. https://aka.ms/y\n- ManagedIdentityCredential authentication unavailable, no response from the IMDS endpoint."))
            .Returns("later", DateTimeOffset.UtcNow.AddHours(1));
        var provider = new AzureIdentityTokenProvider(credential);

        var error = await Assert.ThrowsAsync<GraphTransportException>(() => provider.GetTokenAsync(Scope, Ct).AsTask());

        Assert.Equal(GraphTransportFailure.NotAuthenticated, error.Failure);
        Assert.Contains("EnvironmentCredential authentication unavailable", error.Message, StringComparison.Ordinal);
        Assert.Contains("ManagedIdentityCredential", error.Message, StringComparison.Ordinal);
        Assert.Contains("AZURE_CLIENT_SECRET", error.Message, StringComparison.Ordinal);
        Assert.Contains("earm auth login", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("https://aka.ms", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("az login", error.Message, StringComparison.Ordinal);

        Assert.Equal("later", (await provider.GetTokenAsync(Scope, Ct)).Value);
    }

    [Fact]
    public async Task TheIdentityProvidersReasonIsKeptWithoutItsTraceIds()
    {
        var failure = new AuthenticationFailedException(
            "ClientSecretCredential authentication failed: ",
            new InvalidOperationException("AADSTS90002: Tenant 'abc' not found. Trace ID: 8ddcc45e-5439-4126-8dbc-7808b568ab00 Correlation ID: a9f62614 Timestamp: 2026-10-15 01:20:23Z"));
        var provider = new AzureIdentityTokenProvider(new FakeCredential().Throws(failure));

        var error = await Assert.ThrowsAsync<GraphTransportException>(() => provider.GetTokenAsync(Scope, Ct).AsTask());

        Assert.Contains("ClientSecretCredential authentication failed: AADSTS90002: Tenant 'abc' not found.", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Trace ID", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CredentialMessagesAreRedactedOfSecrets()
    {
        var credential = new FakeCredential().Throws(new AuthenticationFailedException("ClientSecretCredential authentication failed: client_secret=Sup3rS3cret-Value AADSTS7000215: Invalid client secret."));
        var provider = new AzureIdentityTokenProvider(credential);

        var error = await Assert.ThrowsAsync<GraphTransportException>(() => provider.GetTokenAsync(Scope, Ct).AsTask());

        Assert.DoesNotContain("Sup3rS3cret-Value", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallerCancellationIsNotWrappedAsAnAuthenticationFailure()
    {
        var provider = new AzureIdentityTokenProvider(new FakeCredential().Throws(new OperationCanceledException()));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetTokenAsync(Scope, cts.Token).AsTask());
    }

    [Fact]
    public void ATokenNeverPrintsItsValue()
    {
        var token = new GraphToken("eyJ.very.secret", DateTimeOffset.UtcNow);

        Assert.DoesNotContain("secret", token.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret", $"{token}", StringComparison.Ordinal);
    }

    [Fact]
    public void InteractiveGuidanceOnlyPointsAtAuthLogin()
    {
        var text = GraphAuthGuidance.NotSignedIn(GraphAuthMode.Interactive, Scope, new CredentialUnavailableException("No interactive sign-in found."));

        Assert.Contains("earm auth login", text, StringComparison.Ordinal);
        Assert.DoesNotContain("AZURE_CLIENT_SECRET", text, StringComparison.Ordinal);
        Assert.Contains("No interactive sign-in found.", text, StringComparison.Ordinal);
    }

    // ---- token claims -------------------------------------------------------------------------------------

    [Fact]
    public void AUserTokenYieldsTenantObjectIdUpnAndScopes()
    {
        var jwt = Jwt("{\"tid\":\"11111111-1111-1111-1111-111111111111\",\"oid\":\"22222222-2222-2222-2222-222222222222\",\"upn\":\"alice@contoso.example\",\"name\":\"Alice\",\"appid\":\"33333333-3333-3333-3333-333333333333\",\"scp\":\"Application.ReadWrite.All Group.ReadWrite.All\",\"aud\":\"https://graph.microsoft.com\",\"exp\":1790000000}");

        Assert.True(TokenClaims.TryDecode(jwt, out var claims));

        Assert.Equal("11111111-1111-1111-1111-111111111111", claims.TenantId);
        Assert.Equal("22222222-2222-2222-2222-222222222222", claims.ObjectId);
        Assert.Equal("alice@contoso.example", claims.UserPrincipalName);
        Assert.Equal("alice@contoso.example", claims.PrincipalName);
        Assert.Equal("33333333-3333-3333-3333-333333333333", claims.AppId);
        Assert.Equal(["Application.ReadWrite.All", "Group.ReadWrite.All"], claims.Scopes);
        Assert.Empty(claims.Roles);
        Assert.False(claims.IsApplication);
        Assert.Equal("user", claims.PrincipalType);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1790000000), claims.ExpiresOn);
    }

    [Theory]
    [InlineData("{\"tid\":\"t\",\"oid\":\"o\",\"appid\":\"a\",\"idtyp\":\"app\",\"roles\":[\"Application.ReadWrite.All\",\"Group.Read.All\"]}")]
    [InlineData("{\"tid\":\"t\",\"oid\":\"o\",\"azp\":\"a\",\"roles\":[\"Application.ReadWrite.All\",\"Group.Read.All\"]}")]
    public void AnAppOnlyTokenIsRecognisedAndNamedByItsAppId(string payload)
    {
        Assert.True(TokenClaims.TryDecode(Jwt(payload), out var claims));

        Assert.True(claims.IsApplication);
        Assert.Equal("application", claims.PrincipalType);
        Assert.Equal("a", claims.PrincipalName);
        Assert.Equal(["Application.ReadWrite.All", "Group.Read.All"], claims.Roles);
        Assert.Empty(claims.Scopes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("opaque-token-without-dots")]
    [InlineData("a.b")]
    [InlineData("a.!!!.c")]
    [InlineData("a..c")]
    public void NonJwtTokensAreNotDecoded(string? token)
    {
        Assert.False(TokenClaims.TryDecode(token, out var claims));
        Assert.Null(claims.TenantId);
    }

    [Fact]
    public void ANonObjectPayloadIsNotDecoded()
    {
        Assert.False(TokenClaims.TryDecode(Jwt("[1,2,3]"), out _));
        Assert.False(TokenClaims.TryDecode(Jwt("not json"), out _));
    }

    // ---- options ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null, GraphAuthMode.Auto)]
    [InlineData("auto", GraphAuthMode.Auto)]
    [InlineData("DEFAULT", GraphAuthMode.Default)]
    [InlineData(" interactive ", GraphAuthMode.Interactive)]
    public void ModesParseCaseInsensitively(string? text, GraphAuthMode expected)
    {
        Assert.Equal(expected, GraphAuthOptions.ParseMode(text));
        Assert.Equal(expected, GraphAuthOptions.ParseMode(GraphAuthOptions.ModeName(expected)));
    }

    [Fact]
    public void AnUnknownModeIsRejected()
    {
        Assert.Throws<GraphConfigurationException>(() => GraphAuthOptions.ParseMode("cli"));
    }

    [Fact]
    public void ArgumentsWinOverTheEnvironmentAndAreValidated()
    {
        var saved = (Environment.GetEnvironmentVariable(GraphAuthOptions.AuthVariable), Environment.GetEnvironmentVariable(GraphAuthOptions.TenantVariable), Environment.GetEnvironmentVariable(GraphAuthOptions.ClientIdVariable));
        try
        {
            Environment.SetEnvironmentVariable(GraphAuthOptions.AuthVariable, "interactive");
            Environment.SetEnvironmentVariable(GraphAuthOptions.TenantVariable, "contoso.example");
            Environment.SetEnvironmentVariable(GraphAuthOptions.ClientIdVariable, "11111111-1111-1111-1111-111111111111");

            var fromEnvironment = GraphAuthOptions.Resolve(null, null, null, GraphCloud.UsGov);
            Assert.Equal(GraphAuthMode.Interactive, fromEnvironment.Mode);
            Assert.Equal("contoso.example", fromEnvironment.TenantId);
            Assert.Equal("11111111-1111-1111-1111-111111111111", fromEnvironment.ClientId);
            Assert.Equal(GraphCloud.UsGov, fromEnvironment.Cloud);

            var fromArguments = GraphAuthOptions.Resolve("default", "22222222-2222-2222-2222-222222222222", "33333333-3333-3333-3333-333333333333", GraphCloud.Public);
            Assert.Equal(GraphAuthMode.Default, fromArguments.Mode);
            Assert.Equal("22222222-2222-2222-2222-222222222222", fromArguments.TenantId);
            Assert.Equal("33333333-3333-3333-3333-333333333333", fromArguments.ClientId);

            Assert.Throws<GraphConfigurationException>(() => GraphAuthOptions.Resolve(null, "bad tenant/../x", null, GraphCloud.Public));
            Assert.Throws<GraphConfigurationException>(() => GraphAuthOptions.Resolve(null, null, "not-a-guid", GraphCloud.Public));
        }
        finally
        {
            Environment.SetEnvironmentVariable(GraphAuthOptions.AuthVariable, saved.Item1);
            Environment.SetEnvironmentVariable(GraphAuthOptions.TenantVariable, saved.Item2);
            Environment.SetEnvironmentVariable(GraphAuthOptions.ClientIdVariable, saved.Item3);
        }
    }

    // ---- record store -------------------------------------------------------------------------------------

    private static string NewTempFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "earm-auth-tests-" + Guid.NewGuid().ToString("N"));
        return Path.Combine(directory, "auth", "record.json");
    }

    private const string RecordJson = "{\"username\":\"alice@contoso.example\",\"authority\":\"login.microsoftonline.com\",\"homeAccountId\":\"oid.tid\",\"tenantId\":\"11111111-1111-1111-1111-111111111111\",\"clientId\":\"04b07795-8ddb-461a-bbee-02f9e1bf7b46\",\"version\":\"1.0\"}";

    internal static AuthenticationRecord Record(string authority = "login.microsoftonline.com")
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(RecordJson.Replace("login.microsoftonline.com", authority, StringComparison.Ordinal)));
        return AuthenticationRecord.Deserialize(stream);
    }

    [Fact]
    public async Task TheRecordRoundTripsThroughTheStoreAndCanBeDeleted()
    {
        var path = NewTempFile();
        try
        {
            var store = new AuthRecordStore(path);
            Assert.False(store.Exists);
            Assert.Null(store.TryLoad());

            await store.SaveAsync(Record(), Ct);

            Assert.True(store.Exists);
            var loaded = store.TryLoad();
            Assert.NotNull(loaded);
            Assert.Equal("alice@contoso.example", loaded.Username);
            Assert.Equal("11111111-1111-1111-1111-111111111111", loaded.TenantId);
            Assert.Equal("login.microsoftonline.com", loaded.Authority);

            Assert.True(store.Delete());
            Assert.False(store.Delete());
            Assert.Null(store.TryLoad());
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task ADamagedRecordIsTreatedAsNotSignedIn()
    {
        var path = NewTempFile();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "{ this is not a record", Ct);

            Assert.Null(new AuthRecordStore(path).TryLoad());
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task TheRecordHoldsNoTokenAndIsWrittenOwnerOnlyOnWindows()
    {
        var path = NewTempFile();
        try
        {
            var store = new AuthRecordStore(path);
            await store.SaveAsync(Record(), Ct);

            var text = await File.ReadAllTextAsync(path, Ct);
            Assert.DoesNotContain("token", text, StringComparison.OrdinalIgnoreCase);

            if (OperatingSystem.IsWindows())
            {
                var rules = new FileInfo(path).GetAccessControl().GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier));
                Assert.Single(rules.Cast<System.Security.AccessControl.AuthorizationRule>());
            }
            else
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            }
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void TheDefaultRecordPathFollowsThePlatformConvention()
    {
        var path = AuthRecordStore.DefaultPath();

        Assert.EndsWith(Path.Combine("auth", "record.json"), path, StringComparison.Ordinal);
        Assert.Contains(OperatingSystem.IsWindows() ? "EArm" : "earm", path, StringComparison.Ordinal);
    }

    private static void Cleanup(string recordPath)
    {
        var root = Path.GetDirectoryName(Path.GetDirectoryName(recordPath));
        if (root is not null && Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ---- factory ------------------------------------------------------------------------------------------

    private sealed class RecordingFactory : GraphAuthFactory
    {
        public RecordingFactory(AuthRecordStore store)
            : base(store)
        {
        }

        public List<string> Created { get; } = [];

        public TokenCredential? DefaultChainOverride { get; set; }

        public override TokenCredential CreateDefaultChain(GraphAuthOptions options)
        {
            Created.Add("default");
            return DefaultChainOverride ?? new FakeCredential().Returns("chain", DateTimeOffset.UtcNow.AddHours(1));
        }

        protected override TokenCredential CreateInteractive(GraphAuthOptions options, AuthenticationRecord record)
        {
            Created.Add("interactive");
            return new FakeCredential().Returns("interactive", DateTimeOffset.UtcNow.AddHours(1));
        }
    }

    [Fact]
    public async Task AutoUsesTheDefaultChainWithoutARecordAndOnlyTheInteractiveSignInWithOne()
    {
        var path = NewTempFile();
        try
        {
            var store = new AuthRecordStore(path);
            var factory = new RecordingFactory(store);
            var options = new GraphAuthOptions { Mode = GraphAuthMode.Auto };

            Assert.Equal("chain", (await factory.Create(options).GetTokenAsync(Scope, Ct)).Value);
            Assert.Equal(["default"], factory.Created);

            await store.SaveAsync(Record(), Ct);
            factory.Created.Clear();
            var credential = factory.CreateCredential(options);

            Assert.IsNotType<ChainedTokenCredential>(credential);
            Assert.Equal(["interactive"], factory.Created);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task InteractiveWithoutARecordFailsWithAPointerToAuthLogin()
    {
        var path = NewTempFile();
        var factory = new RecordingFactory(new AuthRecordStore(path));

        var error = await Assert.ThrowsAsync<GraphTransportException>(() => factory.Create(new GraphAuthOptions { Mode = GraphAuthMode.Interactive }).GetTokenAsync(Scope, Ct).AsTask());

        Assert.Equal(GraphTransportFailure.NotAuthenticated, error.Failure);
        Assert.Contains("earm auth login", error.Message, StringComparison.Ordinal);
        Assert.Empty(factory.Created);
    }

    [Fact]
    public async Task ARecordOfAnotherCloudIsNotUsed()
    {
        var path = NewTempFile();
        try
        {
            var store = new AuthRecordStore(path);
            await store.SaveAsync(Record("login.microsoftonline.us"), Ct);
            var factory = new RecordingFactory(store);

            await factory.Create(new GraphAuthOptions { Mode = GraphAuthMode.Auto, Cloud = GraphCloud.Public }).GetTokenAsync(Scope, Ct);
            Assert.Equal(["default"], factory.Created);

            var error = await Assert.ThrowsAsync<GraphTransportException>(() => factory.Create(new GraphAuthOptions { Mode = GraphAuthMode.Interactive, Cloud = GraphCloud.Public }).GetTokenAsync(Scope, Ct).AsTask());
            Assert.Contains("another cloud", error.Message, StringComparison.Ordinal);

            factory.Created.Clear();
            await factory.Create(new GraphAuthOptions { Mode = GraphAuthMode.Interactive, Cloud = GraphCloud.UsGov }).GetTokenAsync(Scope, Ct);
            Assert.Equal(["interactive"], factory.Created);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task LogoutDeletesTheRecordAndOnlyTheEarmCacheFiles()
    {
        var path = NewTempFile();
        var cache = Path.Combine(Path.GetTempPath(), "earm-cache-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cache);
        try
        {
            foreach (var name in new[] { "earm", "earm.nocae", "earm.cae", "msal.cache", "earmother", "other.earm" })
            {
                await File.WriteAllTextAsync(Path.Combine(cache, name), "x", Ct);
            }

            var store = new AuthRecordStore(path);
            await store.SaveAsync(Record(), Ct);
            var factory = new GraphAuthFactory(store, cacheDirectories: [cache]);

            var result = factory.Logout();

            Assert.True(result.RecordDeleted);
            Assert.Equal(3, result.CacheFilesDeleted);
            Assert.False(store.Exists);
            Assert.Equal(["earmother", "msal.cache", "other.earm"], Directory.GetFiles(cache).Select(Path.GetFileName).Order(StringComparer.Ordinal));
            var again = factory.Logout();
            Assert.False(again.RecordDeleted);
            Assert.Equal(0, again.CacheFilesDeleted);
        }
        finally
        {
            Cleanup(path);
            Directory.Delete(cache, recursive: true);
        }
    }
    [Theory]
    [InlineData("dev")]
    [InlineData("AzureCliCredential")]
    [InlineData("AzurePowerShellCredential")]
    [InlineData("AzureDeveloperCliCredential")]
    [InlineData("VisualStudioCredential")]
    [InlineData("anything-else")]
    public void ATokenCredentialsVariableThatCouldStartAProcessIsRefused(string value)
    {
        var saved = Environment.GetEnvironmentVariable("AZURE_TOKEN_CREDENTIALS");
        try
        {
            Environment.SetEnvironmentVariable("AZURE_TOKEN_CREDENTIALS", value);

            var error = Assert.Throws<InvalidOperationException>(() => new GraphAuthFactory(new AuthRecordStore(NewTempFile())).CreateDefaultChain(new GraphAuthOptions()));

            Assert.Contains("external process", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AZURE_TOKEN_CREDENTIALS", saved);
        }
    }

    [Fact]
    public async Task TheDefaultChainContainsNoCredentialThatStartsAProcess()
    {
        var saved = Environment.GetEnvironmentVariable("AZURE_TOKEN_CREDENTIALS");
        var savedVariables = new Dictionary<string, string?>();
        try
        {
            Environment.SetEnvironmentVariable("AZURE_TOKEN_CREDENTIALS", null);
            foreach (var name in new[] { "AZURE_TENANT_ID", "AZURE_CLIENT_ID", "AZURE_CLIENT_SECRET", "AZURE_CLIENT_CERTIFICATE_PATH", "AZURE_FEDERATED_TOKEN_FILE", "AZURE_USERNAME", "AZURE_PASSWORD", "IDENTITY_ENDPOINT", "MSI_ENDPOINT", "IMDS_ENDPOINT" })
            {
                savedVariables[name] = Environment.GetEnvironmentVariable(name);
                Environment.SetEnvironmentVariable(name, null);
            }

            var factory = new GraphAuthFactory(new AuthRecordStore(NewTempFile()));
            var provider = factory.Create(new GraphAuthOptions { Mode = GraphAuthMode.Default });

            var error = await Assert.ThrowsAsync<GraphTransportException>(() => provider.GetTokenAsync(Scope, Ct).AsTask());

            Assert.Contains("EnvironmentCredential", error.Message, StringComparison.Ordinal);
            Assert.Contains("WorkloadIdentityCredential", error.Message, StringComparison.Ordinal);
            Assert.Contains("ManagedIdentityCredential", error.Message, StringComparison.Ordinal);
            foreach (var forbidden in new[] { "AzureCliCredential", "AzureDeveloperCliCredential", "AzurePowerShellCredential", "VisualStudioCredential", "VisualStudioCodeCredential", "InteractiveBrowserCredential" })
            {
                Assert.DoesNotContain(forbidden, error.Message, StringComparison.Ordinal);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("AZURE_TOKEN_CREDENTIALS", saved);
            foreach (var pair in savedVariables)
            {
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }
        }
    }
}
