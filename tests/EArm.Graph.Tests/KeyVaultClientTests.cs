using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph.Tests.Infrastructure;
using Xunit;

namespace EArm.Graph.Tests;

/// <summary><see cref="KeyVaultClient"/>: name validation before any URL exists, the vault host from the cloud table, status mapping, no secret in logs.</summary>
public sealed class KeyVaultClientTests
{
    private const string Version = "0123456789abcdef0123456789ABCDEF";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Harness : IDisposable
    {
        private readonly HttpClient _http;

        public Harness(ScriptedHttpHandler handler, GraphCloud? cloud = null)
        {
            Handler = handler;
            Tokens = new FakeTokenProvider();
            Delays = new RecordingDelayProvider();
            Log = new LogSink();
            _http = new HttpClient(handler, disposeHandler: false);
            Client = new KeyVaultClient(_http, Tokens, new RetryPolicy(new RetryOptions(), Delays, new TestLogger<RetryPolicy>(Log)), cloud ?? GraphCloud.Public, TimeSpan.FromSeconds(5), new TestLogger<KeyVaultClient>(Log));
        }

        public ScriptedHttpHandler Handler { get; }

        public FakeTokenProvider Tokens { get; }

        public RecordingDelayProvider Delays { get; }

        public LogSink Log { get; }

        public KeyVaultClient Client { get; }

        public void Dispose()
        {
            _http.Dispose();
        }
    }

    [Theory]
    [InlineData("contoso-kv", true)]
    [InlineData("abc", true)]
    [InlineData("a1b", true)]
    [InlineData("Contoso-KV-01", true)]
    [InlineData("ab", false)]
    [InlineData("a234567890123456789012345", false)]
    [InlineData("1abc", false)]
    [InlineData("abc-", false)]
    [InlineData("a--b", false)]
    [InlineData("a_b", false)]
    [InlineData("a.b", false)]
    [InlineData("a/b", false)]
    [InlineData("a b", false)]
    [InlineData("evil.example#", false)]
    [InlineData("kv@evil", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void VaultNamesFollowTheDocumentedRules(string? name, bool valid)
    {
        Assert.Equal(valid, KeyVaultClient.IsValidVaultName(name));
    }

    [Theory]
    [InlineData("s", true)]
    [InlineData("bg-initial", true)]
    [InlineData("Secret-01", true)]
    [InlineData("a", true)]
    [InlineData("a_b", false)]
    [InlineData("a.b", false)]
    [InlineData("a/b", false)]
    [InlineData("../x", false)]
    [InlineData("a?b", false)]
    [InlineData("a b", false)]
    [InlineData("a%2Fb", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void SecretNamesFollowTheDocumentedRules(string? name, bool valid)
    {
        Assert.Equal(valid, KeyVaultClient.IsValidSecretName(name));
    }

    [Fact]
    public void SecretNamesAreLimitedTo127Characters()
    {
        Assert.True(KeyVaultClient.IsValidSecretName(new string('a', 127)));
        Assert.False(KeyVaultClient.IsValidSecretName(new string('a', 128)));
    }

    [Theory]
    [InlineData(Version, true)]
    [InlineData("v2", false)]
    [InlineData("0123456789abcdef0123456789abcdeg", false)]
    [InlineData("0123456789abcdef0123456789abcde", false)]
    [InlineData("0123456789abcdef0123456789abcdef/../x", false)]
    [InlineData(null, false)]
    public void VersionsAre32HexCharacters(string? version, bool valid)
    {
        Assert.Equal(valid, KeyVaultClient.IsValidVersion(version));
    }

    [Theory]
    [InlineData("public", "contoso-kv", "bg", null, "https://contoso-kv.vault.azure.net/secrets/bg?api-version=7.4")]
    [InlineData("public", "Contoso-KV", "bg", Version, "https://contoso-kv.vault.azure.net/secrets/bg/" + Version + "?api-version=7.4")]
    [InlineData("usgov", "contoso-kv", "bg", null, "https://contoso-kv.vault.usgovcloudapi.net/secrets/bg?api-version=7.4")]
    [InlineData("china", "contoso-kv", "bg", null, "https://contoso-kv.vault.azure.cn/secrets/bg?api-version=7.4")]
    public void TheUrlUsesTheVaultHostOfTheCloudAndApiVersion74(string cloud, string vault, string secret, string? version, string expected)
    {
        using var harness = new Harness(new ScriptedHttpHandler(), GraphCloud.Parse(cloud));

        Assert.Equal(expected, harness.Client.BuildSecretUri(vault, secret, version).AbsoluteUri);
    }

    [Theory]
    [InlineData("evil.example/x#", "s", null)]
    [InlineData("kv@evil.example", "s", null)]
    [InlineData("contoso-kv", "../../x", null)]
    [InlineData("contoso-kv", "s?x=1", null)]
    [InlineData("contoso-kv", "s/other", null)]
    [InlineData("contoso-kv", "s", "../x")]
    [InlineData("a", "s", null)]
    [InlineData("", "s", null)]
    public async Task InvalidNamesNeverReachTheNetworkOrTheTokenProvider(string vault, string secret, string? version)
    {
        var handler = new ScriptedHttpHandler();
        using var harness = new Harness(handler);

        await Assert.ThrowsAsync<ArgumentException>(() => harness.Client.GetSecretAsync(vault, secret, version, Ct));

        Assert.Empty(handler.Requests);
        Assert.Empty(harness.Tokens.Scopes);
    }

    [Fact]
    public async Task ASecretIsReadWithAKeyVaultScopedBearerToken()
    {
        var handler = new ScriptedHttpHandler(ScriptedResponse.Json("{\"value\":\"s3cret-value\",\"id\":\"https://contoso-kv.vault.azure.net/secrets/bg/" + Version + "\"}"));
        using var harness = new Harness(handler);

        var value = await harness.Client.GetSecretAsync("contoso-kv", "bg", null, Ct);

        Assert.Equal("s3cret-value", value);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://contoso-kv.vault.azure.net/secrets/bg?api-version=7.4", request.Uri.AbsoluteUri);
        Assert.Equal("Bearer token-1", request.Header("Authorization"));
        Assert.Null(request.Body);
        Assert.Equal(["https://vault.azure.net/.default"], harness.Tokens.Scopes);
    }

    [Fact]
    public async Task SovereignCloudsAskForTheirOwnVaultScope()
    {
        var handler = new ScriptedHttpHandler(ScriptedResponse.Json("{\"value\":\"x\"}"));
        using var harness = new Harness(handler, GraphCloud.China);

        await harness.Client.GetSecretAsync("contoso-kv", "bg", null, Ct);

        Assert.Equal(["https://vault.azure.cn/.default"], harness.Tokens.Scopes);
        Assert.Equal("contoso-kv.vault.azure.cn", Assert.Single(handler.Requests).Uri.Host);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "Forbidden")]
    [InlineData(HttpStatusCode.NotFound, "SecretNotFound")]
    [InlineData(HttpStatusCode.BadRequest, "BadParameter")]
    public async Task ErrorStatusesSurfaceAsGraphExceptionsWithoutRetry(HttpStatusCode status, string code)
    {
        var handler = new ScriptedHttpHandler(ScriptedResponse.Error(status, code, "Operation denied for 'bg'."));
        using var harness = new Harness(handler);

        var error = await Assert.ThrowsAsync<GraphException>(() => harness.Client.GetSecretAsync("contoso-kv", "bg", null, Ct));

        Assert.Equal(status, error.Status);
        Assert.Equal(code, error.Code);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ThrottlingAndAnExpiredTokenAreRetriedLikeGraph()
    {
        var handler = new ScriptedHttpHandler(
            ScriptedResponse.Error(HttpStatusCode.TooManyRequests, "Throttled", "slow").WithHeader("Retry-After", "4"),
            ScriptedResponse.Error(HttpStatusCode.Unauthorized, "Unauthorized", "expired"),
            ScriptedResponse.Json("{\"value\":\"ok\"}"));
        using var harness = new Harness(handler);

        var value = await harness.Client.GetSecretAsync("contoso-kv", "bg", null, Ct);

        Assert.Equal("ok", value);
        Assert.Equal([TimeSpan.FromSeconds(4)], harness.Delays.Delays);
        Assert.Equal(1, harness.Tokens.Invalidations);
        Assert.Equal("Bearer token-2", handler.Requests[2].Header("Authorization"));
    }

    [Fact]
    public async Task ARedirectIsAnErrorAndIsNotFollowed()
    {
        var handler = new ScriptedHttpHandler(new ScriptedResponse { Status = HttpStatusCode.Found }.WithHeader("Location", "https://evil.example/"));
        using var harness = new Harness(handler);

        var error = await Assert.ThrowsAsync<GraphException>(() => harness.Client.GetSecretAsync("contoso-kv", "bg", null, Ct));

        Assert.Equal("UnexpectedRedirect", error.Code);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task AVaultThatDoesNotResolveIsANetworkFailureThatNamesTheHostNotTheSecret()
    {
        var handler = new ScriptedHttpHandler();
        handler.Fallback = _ => new ScriptedResponse { Throw = new HttpRequestException("No such host is known") };
        using var harness = new Harness(handler);

        var error = await Assert.ThrowsAsync<GraphTransportException>(() => harness.Client.GetSecretAsync("contoso-kv", "bg", null, Ct));

        Assert.Equal(GraphTransportFailure.Network, error.Failure);
        Assert.Contains("contoso-kv.vault.azure.net", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"value\":null}")]
    [InlineData("{\"value\":5}")]
    [InlineData("not json")]
    public async Task AnAnswerWithoutAStringValueIsInvalidAndEchoesNothing(string body)
    {
        var handler = new ScriptedHttpHandler(ScriptedResponse.Json(body));
        using var harness = new Harness(handler);

        var error = await Assert.ThrowsAsync<GraphTransportException>(() => harness.Client.GetSecretAsync("contoso-kv", "bg", null, Ct));

        Assert.Equal(GraphTransportFailure.InvalidResponse, error.Failure);
        Assert.DoesNotContain(body, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSecretValueNeverReachesTheLog()
    {
        const string secret = "Sup3r-S3cret-Value-1234";
        var handler = new ScriptedHttpHandler(ScriptedResponse.Json("{\"value\":\"" + secret + "\"}"));
        using var harness = new Harness(handler);

        await harness.Client.GetSecretAsync("contoso-kv", "bg", null, Ct);

        Assert.NotEmpty(harness.Log.Entries);
        Assert.DoesNotContain(secret, harness.Log.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("token-1", harness.Log.AllText, StringComparison.Ordinal);
    }
}
