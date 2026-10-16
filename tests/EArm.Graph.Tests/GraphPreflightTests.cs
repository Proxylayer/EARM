using System;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph.Auth;
using EArm.Graph.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit;

namespace EArm.Graph.Tests;

/// <summary><see cref="GraphPreflight"/>: token claims first, one /organization read, actionable failures.</summary>
public sealed class GraphPreflightTests
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string Oid = "22222222-2222-2222-2222-222222222222";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Jwt(string payloadJson)
    {
        static string B64(string text)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        return B64("{\"alg\":\"none\"}") + "." + B64(payloadJson) + ".sig";
    }

    private sealed class JwtTokens : IGraphTokenProvider
    {
        private readonly string _token;

        public JwtTokens(string token)
        {
            _token = token;
        }

        public int Invalidations { get; private set; }

        public ValueTask<GraphToken> GetTokenAsync(TokenScopes scope, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(new GraphToken(_token, DateTimeOffset.UtcNow.AddHours(1)));
        }

        public void Invalidate(TokenScopes scope)
        {
            Invalidations++;
        }
    }

    private static (GraphPreflight Preflight, ScriptedHttpHandler Handler, JwtTokens Tokens, LogSink Log, System.Net.Http.HttpClient Http) Create(string token, params ScriptedResponse[] responses)
    {
        var handler = new ScriptedHttpHandler(responses);
        var http = new System.Net.Http.HttpClient(handler, disposeHandler: false);
        var log = new LogSink();
        var tokens = new JwtTokens(token);
        var client = new HttpGraphClient(http, tokens, new RetryPolicy(new RetryOptions(), new RecordingDelayProvider(), new TestLogger<RetryPolicy>(log)), new GraphOptions(), new TestLogger<HttpGraphClient>(log));
        return (new GraphPreflight(tokens, client, GraphCloud.Public, GraphAuthMode.Default, new TestLogger<GraphPreflight>(log)), handler, tokens, log, http);
    }

    private static ScriptedResponse Organization(string id = Tenant)
    {
        return ScriptedResponse.Json("{\"value\":[{\"id\":\"" + id + "\",\"displayName\":\"Contoso\",\"verifiedDomains\":[{\"name\":\"contoso.onmicrosoft.com\",\"isDefault\":false},{\"name\":\"contoso.example\",\"isDefault\":true}]}]}");
    }

    [Fact]
    public async Task AUserTokenYieldsTheTenantThePrincipalAndOneOrganizationRead()
    {
        var (preflight, handler, _, _, http) = Create(Jwt("{\"tid\":\"" + Tenant + "\",\"oid\":\"" + Oid + "\",\"upn\":\"alice@contoso.example\",\"scp\":\"Group.ReadWrite.All\"}"), Organization());
        using var closeHttp = http;

        var result = await preflight.RunAsync(Ct);

        Assert.Equal(Tenant, result.TenantId);
        Assert.Equal(Oid, result.PrincipalObjectId);
        Assert.Equal("alice@contoso.example", result.PrincipalName);
        Assert.Equal("user", result.PrincipalType);
        Assert.Equal(Tenant, result.OrganizationId);
        Assert.Equal("Contoso", result.Organization!["displayName"]!.GetValue<string>());
        Assert.Equal("AzureCloud", result.CloudName);
        Assert.Equal(GraphAuthMode.Default, result.AuthMode);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/v1.0/organization", request.Uri.AbsolutePath);
        Assert.Equal("id,displayName,verifiedDomains", request.QueryValue("$select"));
    }

    [Fact]
    public async Task AnAppOnlyTokenNeedsNoMeEndpointAndNamesTheServicePrincipal()
    {
        var (preflight, handler, _, _, http) = Create(Jwt("{\"tid\":\"" + Tenant + "\",\"oid\":\"" + Oid + "\",\"appid\":\"app-1\",\"idtyp\":\"app\",\"roles\":[\"Organization.Read.All\"]}"), Organization());
        using var closeHttp = http;

        var result = await preflight.RunAsync(Ct);

        Assert.Equal("application", result.PrincipalType);
        Assert.Equal(Oid, result.PrincipalObjectId);
        Assert.Equal("app-1", result.PrincipalName);
        Assert.DoesNotContain(handler.Requests, static r => r.Uri.AbsolutePath.EndsWith("/me", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AForbiddenOrganizationReadKeepsTheTenantFromTheTokenAndLeavesTheOrganizationUnknown()
    {
        var (preflight, handler, _, log, http) = Create(Jwt("{\"tid\":\"" + Tenant + "\",\"oid\":\"" + Oid + "\",\"appid\":\"app-1\",\"idtyp\":\"app\"}"), ScriptedResponse.Error(HttpStatusCode.Forbidden, "Authorization_RequestDenied", "Insufficient privileges."));
        using var closeHttp = http;

        var result = await preflight.RunAsync(Ct);

        Assert.Equal(Tenant, result.TenantId);
        Assert.Null(result.Organization);
        Assert.Single(handler.Requests);
        Assert.Contains(log.Entries, static e => e.Level == LogLevel.Warning && e.Text.Contains("Organization.Read.All", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnOpaqueTokenTakesTheTenantFromTheOrganization()
    {
        var (preflight, _, _, _, http) = Create("opaque-token", Organization());
        using var closeHttp = http;

        var result = await preflight.RunAsync(Ct);

        Assert.Equal(Tenant, result.TenantId);
        Assert.Null(result.PrincipalObjectId);
        Assert.Equal("user", result.PrincipalType);
    }

    [Fact]
    public async Task AnOpaqueTokenWithAnUnreadableOrganizationCannotNameTheTenant()
    {
        var (preflight, _, _, _, http) = Create("opaque-token", ScriptedResponse.Error(HttpStatusCode.Forbidden, "Authorization_RequestDenied", "denied"));
        using var closeHttp = http;

        var error = await Assert.ThrowsAsync<GraphTransportException>(() => preflight.RunAsync(Ct));

        Assert.Equal(GraphTransportFailure.InvalidResponse, error.Failure);
        Assert.Contains("Organization.Read.All", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GraphRejectingTheTokenIsAnAuthenticationFailureWithTheRequestId()
    {
        var (preflight, handler, tokens, _, http) = Create(Jwt("{\"tid\":\"" + Tenant + "\"}"));
        using var closeHttp = http;
        handler.Fallback = _ => ScriptedResponse.Error(HttpStatusCode.Unauthorized, "InvalidAuthenticationToken", "Access token validation failure.", "req-401");

        var error = await Assert.ThrowsAsync<GraphTransportException>(() => preflight.RunAsync(Ct));

        Assert.Equal(GraphTransportFailure.NotAuthenticated, error.Failure);
        Assert.Contains("InvalidAuthenticationToken", error.Message, StringComparison.Ordinal);
        Assert.Contains("req-401", error.Message, StringComparison.Ordinal);
        Assert.True(tokens.Invalidations >= 1);
    }

    [Fact]
    public async Task ATokenProviderFailureSurfacesBeforeAnyRequest()
    {
        var handler = new ScriptedHttpHandler();
        using var http = new System.Net.Http.HttpClient(handler, disposeHandler: false);
        var tokens = new FakeTokenProvider { Failure = new GraphTransportException(GraphTransportFailure.NotAuthenticated, "Not signed in: nothing worked.") };
        var client = new HttpGraphClient(http, tokens, new RetryPolicy(new RetryOptions(), new RecordingDelayProvider(), new TestLogger<RetryPolicy>(new LogSink())), new GraphOptions());
        var preflight = new GraphPreflight(tokens, client, GraphCloud.Public, GraphAuthMode.Auto);

        var error = await Assert.ThrowsAsync<GraphTransportException>(() => preflight.RunAsync(Ct));

        Assert.Equal("Not signed in: nothing worked.", error.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ADifferentOrganizationIdIsLoggedAsAWarningButTheTokenTenantWins()
    {
        var (preflight, _, _, log, http) = Create(Jwt("{\"tid\":\"" + Tenant + "\",\"oid\":\"" + Oid + "\"}"), Organization("99999999-9999-9999-9999-999999999999"));
        using var closeHttp = http;

        var result = await preflight.RunAsync(Ct);

        Assert.Equal(Tenant, result.TenantId);
        Assert.Contains(log.Entries, static e => e.Level == LogLevel.Warning && e.Text.Contains("differs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ThePreflightNeverLogsTheToken()
    {
        var token = Jwt("{\"tid\":\"" + Tenant + "\",\"oid\":\"" + Oid + "\",\"upn\":\"alice@contoso.example\"}");
        var (preflight, _, _, log, http) = Create(token, Organization());
        using var closeHttp = http;

        await preflight.RunAsync(Ct);

        Assert.DoesNotContain(token, log.AllText, StringComparison.Ordinal);
        Assert.Contains(log.Entries, static e => e.Level == LogLevel.Information && e.Text.Contains("alice@contoso.example", StringComparison.Ordinal));
        Assert.DoesNotContain("Bearer", string.Join('\n', log.Entries.Select(static e => e.Text)), StringComparison.Ordinal);
    }
}
