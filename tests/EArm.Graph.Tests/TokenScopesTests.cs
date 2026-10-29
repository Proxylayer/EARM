using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph.Auth;
using EArm.Graph.Tests.Infrastructure;
using Xunit;

namespace EArm.Graph.Tests;

/// <summary>
/// <see cref="TokenScopes"/>: one resource per set, <c>.default</c> never mixed with explicit scopes, order- and case-insensitive identity; the Azure provider
/// caches one token per set; the clients request the cloud's <c>.default</c> unless given explicit scopes of the right resource.
/// </summary>
public sealed class TokenScopesTests
{
    private const string Graph = "https://graph.microsoft.com";
    private const string Vault = "https://vault.azure.net";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- the value type ----

    [Fact]
    public void TheDefaultSetIsTheResourcesDotDefaultScope()
    {
        var scopes = TokenScopes.Default(Graph + "/");

        Assert.True(scopes.IsDefault);
        Assert.Equal(Graph, scopes.Resource);
        Assert.Equal([".default"], scopes.Names);
        Assert.Equal([Graph + "/.default"], scopes.Scopes);
        Assert.Equal(Graph + "/.default", scopes.ToString());
    }

    [Fact]
    public void ExplicitScopesAreQualifiedWithTheResourceAndKeepTheirOrderWithoutDuplicates()
    {
        var scopes = TokenScopes.Explicit(Graph, "User.Read", Graph + "/Organization.Read.All", "user.read");

        Assert.False(scopes.IsDefault);
        Assert.Equal(["User.Read", "Organization.Read.All"], scopes.Names);
        Assert.Equal([Graph + "/User.Read", Graph + "/Organization.Read.All"], scopes.Scopes);
    }

    [Fact]
    public void SetsWithTheSameScopesInAnyOrderOrCaseAreEqualAndShareACacheKey()
    {
        var a = TokenScopes.Explicit(Graph, "User.Read", "Organization.Read.All");
        var b = TokenScopes.Explicit("HTTPS://Graph.Microsoft.com", "organization.read.all", "USER.READ");

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.Equal(a.CacheKey, b.CacheKey);
    }

    [Fact]
    public void DifferentSetsHaveDifferentKeys()
    {
        var keys = new[]
        {
            TokenScopes.Default(Graph),
            TokenScopes.Default(Vault),
            TokenScopes.Explicit(Graph, "User.Read"),
            TokenScopes.Explicit(Graph, "User.Read", "Organization.Read.All"),
            TokenScopes.Explicit(Vault, "user_impersonation"),
        }.Select(s => s.CacheKey).ToArray();

        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData(".default")]
    [InlineData("https://graph.microsoft.com/.default")]
    [InlineData("https://graph.microsoft.com/.DEFAULT")]
    public void DotDefaultCannotBeMixedWithExplicitScopes(string scope)
    {
        var error = Assert.Throws<ArgumentException>(() => TokenScopes.Explicit(Graph, "User.Read", scope));

        Assert.Contains(".default", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AScopeOfAnotherResourceIsRefused()
    {
        var error = Assert.Throws<ArgumentException>(() => TokenScopes.Explicit(Graph, "User.Read", Vault + "/user_impersonation"));

        Assert.Contains("one resource", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("User.Read Mail.Read")]
    [InlineData("a/b")]
    [InlineData("https://graph.microsoft.com/")]
    [InlineData("openid")]
    [InlineData("offline_access")]
    [InlineData("profile")]
    public void AMalformedOrReservedScopeIsRefused(string scope)
    {
        Assert.Throws<ArgumentException>(() => TokenScopes.Explicit(Graph, scope));
    }

    [Fact]
    public void AnEmptyExplicitListIsRefused()
    {
        Assert.Throws<ArgumentException>(() => TokenScopes.Explicit(Graph, Array.Empty<string>()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("graph.microsoft.com")]
    [InlineData("https://graph.microsoft.com/v1.0")]
    [InlineData("https://graph.microsoft.com/?x=1")]
    [InlineData("https://graph.microsoft.com/#f")]
    public void AResourceMustBeAnOrigin(string resource)
    {
        Assert.ThrowsAny<ArgumentException>(() => TokenScopes.Default(resource));
    }

    [Fact]
    public void TheCloudsDefaultSetsMatchTheirScopeStrings()
    {
        foreach (var cloud in GraphCloud.All)
        {
            Assert.Equal(cloud.GraphScope, Assert.Single(cloud.GraphScopes.Scopes));
            Assert.Equal(cloud.KeyVaultScope, Assert.Single(cloud.KeyVaultScopes.Scopes));
            Assert.Equal(cloud.ResourceManagerOrigin + "/.default", Assert.Single(cloud.ResourceManagerScopes.Scopes));
        }
    }

    // ---- the Azure provider cache ----

    [Fact]
    public async Task TheProviderCachesOneTokenPerSetRegardlessOfOrderAndCase()
    {
        var expires = DateTimeOffset.UtcNow.AddHours(1);
        var credential = new FakeCredential().Returns("ab", expires).Returns("a", expires);
        var provider = new AzureIdentityTokenProvider(credential);

        var first = await provider.GetTokenAsync(TokenScopes.Explicit(Graph, "User.Read", "Organization.Read.All"), Ct);
        var reordered = await provider.GetTokenAsync(TokenScopes.Explicit(Graph, "organization.read.all", "user.read"), Ct);
        var other = await provider.GetTokenAsync(TokenScopes.Explicit(Graph, "User.Read"), Ct);
        var otherAgain = await provider.GetTokenAsync(TokenScopes.Explicit(Graph, "USER.READ"), Ct);

        Assert.Equal("ab", first.Value);
        Assert.Equal("ab", reordered.Value);
        Assert.Equal("a", other.Value);
        Assert.Equal("a", otherAgain.Value);
        Assert.Equal(2, credential.Calls);
        Assert.Equal([Graph + "/User.Read", Graph + "/Organization.Read.All", Graph + "/User.Read"], credential.Scopes);
    }

    [Fact]
    public async Task InvalidatingOneSetLeavesTheOthersCached()
    {
        var expires = DateTimeOffset.UtcNow.AddHours(1);
        var credential = new FakeCredential().Returns("default-1", expires).Returns("explicit-1", expires).Returns("explicit-2", expires);
        var provider = new AzureIdentityTokenProvider(credential);
        var defaults = TokenScopes.Default(Graph);
        var explicitSet = TokenScopes.Explicit(Graph, "User.Read");

        await provider.GetTokenAsync(defaults, Ct);
        await provider.GetTokenAsync(explicitSet, Ct);
        provider.Invalidate(TokenScopes.Explicit(Graph, "user.read"));

        Assert.Equal("default-1", (await provider.GetTokenAsync(defaults, Ct)).Value);
        Assert.Equal("explicit-2", (await provider.GetTokenAsync(explicitSet, Ct)).Value);
        Assert.Equal(3, credential.Calls);
    }

    [Fact]
    public async Task AClaimsRefreshReplacesOnlyThatSetsToken()
    {
        var expires = DateTimeOffset.UtcNow.AddHours(1);
        var credential = new FakeCredential().Returns("default-1", expires).Returns("explicit-1", expires).Returns("explicit-cae", expires);
        var provider = new AzureIdentityTokenProvider(credential);
        var defaults = TokenScopes.Default(Graph);
        var explicitSet = TokenScopes.Explicit(Graph, "User.Read", "Organization.Read.All");

        await provider.GetTokenAsync(defaults, Ct);
        await provider.GetTokenAsync(explicitSet, Ct);
        var refreshed = await provider.RefreshTokenAsync(TokenScopes.Explicit(Graph, "Organization.Read.All", "User.Read"), "{\"access_token\":{}}", Ct);

        Assert.Equal("explicit-cae", refreshed.Value);
        Assert.Equal("explicit-cae", (await provider.GetTokenAsync(explicitSet, Ct)).Value);
        Assert.Equal("default-1", (await provider.GetTokenAsync(defaults, Ct)).Value);
        Assert.Equal(3, credential.Calls);
        Assert.Equal([Graph + "/Organization.Read.All", Graph + "/User.Read"], credential.Scopes.Skip(3));
    }

    // ---- the clients ----

    private static RetryPolicy Retry()
    {
        return new RetryPolicy(new RetryOptions(), new RecordingDelayProvider(), new TestLogger<RetryPolicy>(new LogSink()));
    }

    [Fact]
    public async Task TheGraphClientRequestsTheCloudsDotDefaultByDefault()
    {
        var handler = new ScriptedHttpHandler(ScriptedResponse.Json("{\"id\":\"u1\"}"));
        using var http = new HttpClient(handler, disposeHandler: false);
        var tokens = new FakeTokenProvider();
        var client = new HttpGraphClient(http, tokens, Retry(), new GraphOptions { Cloud = GraphCloud.UsGov });

        await client.GetAsync(new GraphRequest("/users/u1"), Ct);

        Assert.Equal(GraphCloud.UsGov.GraphScopes, client.Scopes);
        Assert.Equal([GraphCloud.UsGov.GraphScope], tokens.Scopes);
    }

    [Fact]
    public async Task TheGraphClientRequestsTheConfiguredExplicitScopes()
    {
        var handler = new ScriptedHttpHandler(ScriptedResponse.Json("{\"id\":\"u1\"}"));
        using var http = new HttpClient(handler, disposeHandler: false);
        var tokens = new FakeTokenProvider();
        var scopes = TokenScopes.Explicit(Graph, "User.Read", "Organization.Read.All");
        var client = new HttpGraphClient(http, tokens, Retry(), new GraphOptions { Scopes = scopes });

        await client.GetAsync(new GraphRequest("/users/u1"), Ct);

        Assert.Equal([scopes.ToString()], tokens.Scopes);
    }

    [Fact]
    public void TheGraphClientRefusesScopesOfAnotherResource()
    {
        using var http = new HttpClient(new ScriptedHttpHandler(), disposeHandler: false);

        Assert.Throws<ArgumentException>(() => new HttpGraphClient(http, new FakeTokenProvider(), Retry(), new GraphOptions { Cloud = GraphCloud.UsGov, Scopes = TokenScopes.Default(Graph) }));
        Assert.Throws<ArgumentException>(() => new HttpGraphClient(http, new FakeTokenProvider(), Retry(), new GraphOptions { Scopes = TokenScopes.Default(Vault) }));
    }

    [Fact]
    public async Task ThePreflightRequestsTheCloudsDotDefaultByDefault()
    {
        var handler = new ScriptedHttpHandler(ScriptedResponse.Json("{\"value\":[{\"id\":\"t1\",\"displayName\":\"Contoso\"}]}"));
        using var http = new HttpClient(handler, disposeHandler: false);
        var tokens = new FakeTokenProvider();
        var client = new HttpGraphClient(http, tokens, Retry(), new GraphOptions());
        var preflight = new GraphPreflight(tokens, client, GraphCloud.Public, GraphAuthMode.Auto);

        await preflight.RunAsync(Ct);

        Assert.Equal(GraphCloud.Public.GraphScopes, preflight.Scopes);
        Assert.All(tokens.Scopes, scope => Assert.Equal(GraphCloud.Public.GraphScope, scope));
    }

    [Fact]
    public void ThePreflightAcceptsExplicitGraphScopesAndRefusesOthers()
    {
        using var http = new HttpClient(new ScriptedHttpHandler(), disposeHandler: false);
        var tokens = new FakeTokenProvider();
        var client = new HttpGraphClient(http, tokens, Retry(), new GraphOptions());
        var scopes = TokenScopes.Explicit(Graph, "Organization.Read.All");

        Assert.Equal(scopes, new GraphPreflight(tokens, client, GraphCloud.Public, GraphAuthMode.Auto, scopes: scopes).Scopes);
        Assert.Throws<ArgumentException>(() => new GraphPreflight(tokens, client, GraphCloud.Public, GraphAuthMode.Auto, scopes: TokenScopes.Default(Vault)));
    }

    [Fact]
    public void TheKeyVaultClientDefaultsToTheVaultDotDefaultAndRefusesOtherResources()
    {
        using var http = new HttpClient(new ScriptedHttpHandler(), disposeHandler: false);
        var tokens = new FakeTokenProvider();
        var impersonation = TokenScopes.Explicit(Vault, "user_impersonation");

        Assert.Equal(GraphCloud.Public.KeyVaultScopes, new KeyVaultClient(http, tokens, Retry(), GraphCloud.Public).Scopes);
        Assert.Equal(impersonation, new KeyVaultClient(http, tokens, Retry(), GraphCloud.Public, scopes: impersonation).Scopes);
        Assert.Throws<ArgumentException>(() => new KeyVaultClient(http, tokens, Retry(), GraphCloud.Public, scopes: TokenScopes.Default(Graph)));
        Assert.Throws<ArgumentException>(() => new KeyVaultClient(http, tokens, Retry(), GraphCloud.China, scopes: impersonation));
    }

    [Fact]
    public void TheResourceManagerClientDefaultsToTheArmDotDefaultAndRefusesOtherResources()
    {
        using var http = new HttpClient(new ScriptedHttpHandler(), disposeHandler: false);
        var tokens = new FakeTokenProvider();
        var impersonation = GraphCloud.Public.ResourceManagerConsentScopes;

        Assert.Equal(GraphCloud.Public.ResourceManagerScopes, new ArmClient(http, tokens, Retry(), GraphCloud.Public).Scopes);
        Assert.Equal(GraphCloud.UsGov.ResourceManagerScopes, new ArmClient(http, tokens, Retry(), GraphCloud.UsGov).Scopes);
        Assert.Equal(impersonation, new ArmClient(http, tokens, Retry(), GraphCloud.Public, scopes: impersonation).Scopes);
        Assert.Throws<ArgumentException>(() => new ArmClient(http, tokens, Retry(), GraphCloud.Public, scopes: TokenScopes.Default(Graph)));
        Assert.Throws<ArgumentException>(() => new ArmClient(http, tokens, Retry(), GraphCloud.Public, scopes: TokenScopes.Default(Vault)));
        Assert.Throws<ArgumentException>(() => new ArmClient(http, tokens, Retry(), GraphCloud.China, scopes: impersonation));
        Assert.Throws<ArgumentException>(() => new KeyVaultClient(http, tokens, Retry(), GraphCloud.Public, scopes: GraphCloud.Public.ResourceManagerScopes));
    }
}
