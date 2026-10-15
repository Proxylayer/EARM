using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Identity;
using EArm.Graph.Auth;
using EArm.Graph.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EArm.Graph.Tests;

/// <summary>
/// Review 3I, findings 7-9: the interactive credential remembers the client and tenant of the sign-in, a 401 refresh asks for a new token (with the
/// claims of a continuous-access-evaluation challenge) and ends the call when the credential hands the rejected token back, and a timeout that fires while
/// the connection is still being made is not reported as an ambiguous write.
/// </summary>
public sealed class Review3I_AuthAndTransportTests
{
    private const string CustomClient = "44444444-4444-4444-4444-444444444444";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AuthenticationRecord RecordWith(string? clientId, string tenantId = "22222222-2222-2222-2222-222222222222")
    {
        var client = clientId is null ? string.Empty : ",\"clientId\":\"" + clientId + "\"";
        var json = "{\"username\":\"alice@contoso.example\",\"authority\":\"login.microsoftonline.com\",\"homeAccountId\":\"oid.tid\",\"tenantId\":\"" + tenantId + "\"" + client + ",\"version\":\"1.0\"}";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return AuthenticationRecord.Deserialize(stream);
    }

    // ---- finding 7 ----

    [Fact]
    public void TheInteractiveCredentialDefaultsToTheClientAndTenantOfTheStoredSignIn()
    {
        var interactive = GraphAuthFactory.BuildInteractiveOptions(new GraphAuthOptions(), RecordWith(CustomClient));

        Assert.Equal(CustomClient, interactive.ClientId);
        Assert.Equal("22222222-2222-2222-2222-222222222222", interactive.TenantId);
    }

    [Fact]
    public void ExplicitClientAndTenantOptionsWinOverTheRecord()
    {
        var auth = new GraphAuthOptions { ClientId = "55555555-5555-5555-5555-555555555555", TenantId = "contoso.example" };

        var interactive = GraphAuthFactory.BuildInteractiveOptions(auth, RecordWith(CustomClient));

        Assert.Equal("55555555-5555-5555-5555-555555555555", interactive.ClientId);
        Assert.Equal("contoso.example", interactive.TenantId);
    }

    [Fact]
    public void ARecordWithoutAClientIdStillBuildsTheOptions()
    {
        var interactive = GraphAuthFactory.BuildInteractiveOptions(new GraphAuthOptions(), RecordWith(null));

        Assert.False(string.IsNullOrEmpty(interactive.ClientId));
    }

    [Fact]
    public async Task TheClientIdOfALoginSurvivesTheRecordFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "earm-r3i-" + Guid.NewGuid().ToString("N"), "record.json");
        try
        {
            var store = new AuthRecordStore(path);
            await store.SaveAsync(RecordWith(CustomClient), Ct);

            var loaded = store.TryLoad();

            Assert.NotNull(loaded);
            Assert.Equal(CustomClient, GraphAuthFactory.BuildInteractiveOptions(new GraphAuthOptions(), loaded).ClientId);
        }
        finally
        {
            var directory = Path.GetDirectoryName(path)!;
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    // ---- finding 8 ----

    private sealed class FixedTokenProvider : IGraphTokenProvider
    {
        public int Refreshes { get; private set; }

        public string? LastClaims { get; private set; }

        public string Next { get; set; } = "same-token";

        public ValueTask<GraphToken> GetTokenAsync(string scope, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(new GraphToken(Next, DateTimeOffset.UtcNow.AddHours(1)));
        }

        public void Invalidate(string scope)
        {
        }

        public ValueTask<GraphToken> RefreshTokenAsync(string scope, string? claims, CancellationToken cancellationToken)
        {
            Refreshes++;
            LastClaims = claims;
            return GetTokenAsync(scope, cancellationToken);
        }
    }

    private static HttpGraphClient ClientOver(ScriptedHttpHandler handler, IGraphTokenProvider tokens, Action<GraphOptions>? configure = null)
    {
        var options = new GraphOptions();
        configure?.Invoke(options);
        var http = new HttpClient(handler, disposeHandler: false) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        return new HttpGraphClient(http, tokens, new RetryPolicy(new RetryOptions(), new RecordingDelayProvider(), NullLogger<RetryPolicy>.Instance), options);
    }

    [Fact]
    public async Task ARefreshThatReturnsTheRejectedTokenEndsTheCallInsteadOfSendingItAgain()
    {
        var handler = new ScriptedHttpHandler(ScriptedResponse.Error(HttpStatusCode.Unauthorized, "InvalidAuthenticationToken", "Access token validation failure.", "req-9"));
        var tokens = new FixedTokenProvider();
        var client = ClientOver(handler, tokens);

        var error = await Assert.ThrowsAsync<GraphTransportException>(() => client.GetAsync(new GraphRequest("/users"), Ct));

        Assert.Equal(GraphTransportFailure.NotAuthenticated, error.Failure);
        Assert.Contains("req-9", error.Message, StringComparison.Ordinal);
        Assert.Contains("InvalidAuthenticationToken", error.Message, StringComparison.Ordinal);
        Assert.Contains("same token", error.Message, StringComparison.Ordinal);
        Assert.IsType<GraphException>(error.InnerException);
        Assert.Single(handler.Requests);
        Assert.Equal(1, tokens.Refreshes);
    }

    [Fact]
    public async Task ARefreshThatReturnsANewTokenRetriesWithIt()
    {
        var handler = new ScriptedHttpHandler(
            ScriptedResponse.Error(HttpStatusCode.Unauthorized, "InvalidAuthenticationToken", "expired"),
            ScriptedResponse.Json("{\"id\":\"u1\"}"));
        var client = ClientOver(handler, new RotatingTokenProvider());

        var body = await client.GetAsync(new GraphRequest("/users/u1"), Ct);

        Assert.NotNull(body);
        Assert.Equal(["Bearer token-1", "Bearer token-2"], handler.Requests.Select(static r => r.Header("Authorization")).ToArray());
    }
    private sealed class RotatingTokenProvider : IGraphTokenProvider
    {
        private int _generation = 1;

        public ValueTask<GraphToken> GetTokenAsync(string scope, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(new GraphToken("token-" + _generation, DateTimeOffset.UtcNow.AddHours(1)));
        }

        public void Invalidate(string scope)
        {
            _generation++;
        }
    }

    [Fact]
    public async Task AClaimsChallengeIsPassedToTheTokenProviderAsDecodedJson()
    {
        const string claimsJson = "{\"access_token\":{\"nbf\":{\"essential\":true,\"value\":\"1704489447\"}}}";
        var challenge = "Bearer realm=\"\", authorization_uri=\"https://login.windows.net/common/oauth2/authorize\", error=\"insufficient_claims\", claims=\""
            + Convert.ToBase64String(Encoding.UTF8.GetBytes(claimsJson)) + "\"";
        var handler = new ScriptedHttpHandler(
            ScriptedResponse.Error(HttpStatusCode.Unauthorized, "InvalidAuthenticationToken", "CAE").WithHeader("WWW-Authenticate", challenge),
            ScriptedResponse.Json("{\"id\":\"u1\"}"));
        var tokens = new ClaimsAwareProvider();
        var client = ClientOver(handler, tokens);

        var body = await client.GetAsync(new GraphRequest("/users/u1"), Ct);

        Assert.NotNull(body);
        Assert.Equal(claimsJson, tokens.Claims);
        Assert.Equal(["Bearer plain", "Bearer with-claims"], handler.Requests.Select(static r => r.Header("Authorization")).ToArray());
    }

    private sealed class ClaimsAwareProvider : IGraphTokenProvider
    {
        private string _current = "plain";

        public string? Claims { get; private set; }

        public ValueTask<GraphToken> GetTokenAsync(string scope, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(new GraphToken(_current, DateTimeOffset.UtcNow.AddHours(1)));
        }

        public void Invalidate(string scope)
        {
        }

        public ValueTask<GraphToken> RefreshTokenAsync(string scope, string? claims, CancellationToken cancellationToken)
        {
            Claims = claims;
            _current = claims is null ? _current : "with-claims";
            return GetTokenAsync(scope, cancellationToken);
        }
    }

    [Theory]
    [InlineData("Bearer realm=\"\", error=\"insufficient_claims\", claims=\"eyJhIjoxfQ==\"", "{\"a\":1}")]
    [InlineData("Bearer claims=\"eyJhIjoxfQ\"", "{\"a\":1}")]
    [InlineData("Bearer claims=eyJhIjoxfQ==", "{\"a\":1}")]
    [InlineData("Bearer realm=\"\", error=\"invalid_token\"", null)]
    [InlineData("Bearer claims=\"!!not-base64!!\"", null)]
    [InlineData("Bearer claims=\"bm90LWpzb24=\"", null)]
    [InlineData("Basic realm=\"x\"", null)]
    public void TheClaimsParameterOfABearerChallengeIsDecoded(string header, string? expected)
    {
        using var message = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        message.Headers.TryAddWithoutValidation("WWW-Authenticate", header);

        Assert.Equal(expected, HttpGraphClient.ParseClaimsChallenge(message.Headers));
    }

    [Fact]
    public void AResponseWithoutAChallengeHasNoClaims()
    {
        using var message = new HttpResponseMessage(HttpStatusCode.Unauthorized);

        Assert.Null(HttpGraphClient.ParseClaimsChallenge(message.Headers));
    }

    private sealed class CapturingCredential : TokenCredential
    {
        private int _calls;

        public List<(string[] Scopes, string? Claims)> Contexts { get; } = [];

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Contexts.Add(([.. requestContext.Scopes], requestContext.Claims));
            return new AccessToken("t" + Interlocked.Increment(ref _calls), DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(GetToken(requestContext, cancellationToken));
        }
    }

    [Fact]
    public async Task TheAzureProviderRequestsAClaimsTokenAroundItsCacheAndCachesTheResult()
    {
        var credential = new CapturingCredential();
        var provider = new AzureIdentityTokenProvider(credential);
        const string scope = "https://graph.microsoft.com/.default";

        var first = await provider.GetTokenAsync(scope, Ct);
        var challenged = await provider.RefreshTokenAsync(scope, "{\"access_token\":{}}", Ct);
        var cached = await provider.GetTokenAsync(scope, Ct);

        Assert.Equal("t1", first.Value);
        Assert.Equal("t2", challenged.Value);
        Assert.Equal("t2", cached.Value);
        Assert.Equal(2, credential.Contexts.Count);
        Assert.Null(credential.Contexts[0].Claims);
        Assert.Equal("{\"access_token\":{}}", credential.Contexts[1].Claims);
    }

    [Fact]
    public async Task TheAzureProviderRefreshWithoutClaimsDropsTheCachedToken()
    {
        var credential = new CapturingCredential();
        var provider = new AzureIdentityTokenProvider(credential);
        const string scope = "https://graph.microsoft.com/.default";
        await provider.GetTokenAsync(scope, Ct);

        var refreshed = await provider.RefreshTokenAsync(scope, null, Ct);

        Assert.Equal("t2", refreshed.Value);
        Assert.All(credential.Contexts, static c => Assert.Null(c.Claims));
    }

    [Fact]
    public async Task ACredentialThatKeepsReturningTheSameTokenMakesTheClientStopAfterOneRefresh()
    {
        var credential = new FakeCredential().Returns("stuck", DateTimeOffset.UtcNow.AddHours(1)).Returns("stuck", DateTimeOffset.UtcNow.AddHours(1));
        var provider = new AzureIdentityTokenProvider(credential);
        var handler = new ScriptedHttpHandler(ScriptedResponse.Error(HttpStatusCode.Unauthorized, "InvalidAuthenticationToken", "revoked"));
        var client = ClientOver(handler, provider);

        var error = await Assert.ThrowsAsync<GraphTransportException>(() => client.GetAsync(new GraphRequest("/users"), Ct));

        Assert.Equal(GraphTransportFailure.NotAuthenticated, error.Failure);
        Assert.Single(handler.Requests);
        Assert.Equal(2, credential.Calls);
    }

    // ---- finding 9 ----

    private sealed class ProbingHandler : HttpMessageHandler
    {
        private readonly bool _markStarted;
        private readonly bool _markConnected;
        private int _calls;

        public ProbingHandler(bool markStarted, bool markConnected)
        {
            _markStarted = markStarted;
            _markConnected = markConnected;
        }

        public int Calls => Volatile.Read(ref _calls);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            if (request.Options.TryGetValue(HttpGraphClient.ConnectProbeKey, out var probe))
            {
                if (_markStarted)
                {
                    probe.MarkStarted();
                }

                if (_markConnected)
                {
                    probe.MarkConnected();
                }
            }

            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("unreachable");
        }
    }

    private static async Task<(GraphTransportException Error, int Calls)> PostThatTimesOutAsync(bool markStarted, bool markConnected)
    {
        var handler = new ProbingHandler(markStarted, markConnected);
        using var http = new HttpClient(handler, disposeHandler: false) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        var client = new HttpGraphClient(http, new RotatingTokenProvider(), new RetryPolicy(new RetryOptions(), new RecordingDelayProvider(), NullLogger<RetryPolicy>.Instance), new GraphOptions { Timeout = TimeSpan.FromMilliseconds(150) });

        var error = await Assert.ThrowsAsync<GraphTransportException>(() => client.PostAsync(new GraphRequest("/groups"), new System.Text.Json.Nodes.JsonObject { ["displayName"] = "g" }, Ct));
        return (error, handler.Calls);
    }

    [Fact]
    public async Task ATimeoutWhileStillConnectingIsNotAmbiguousAndTheWriteIsRetried()
    {
        var (error, calls) = await PostThatTimesOutAsync(markStarted: true, markConnected: false);

        Assert.Equal(GraphTransportFailure.Timeout, error.Failure);
        Assert.False(error.Ambiguous, "nothing was sent: the connection was never made");
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task ATimeoutAfterTheConnectionWasMadeIsAmbiguousAndTheWriteIsNotRepeated()
    {
        var (error, calls) = await PostThatTimesOutAsync(markStarted: true, markConnected: true);

        Assert.True(error.Ambiguous);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ATimeoutWithNoConnectionInformationStaysAmbiguous()
    {
        var (error, calls) = await PostThatTimesOutAsync(markStarted: false, markConnected: false);

        Assert.True(error.Ambiguous, "unsure: the request may have gone out over a pooled connection");
        Assert.Equal(1, calls);
    }
}
