using System;
using System.Linq;
using System.Collections.Generic;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Xunit;

namespace EArm.Graph.Tests;

/// <summary>The fixed cloud table, the origin check, query encoding and the error parser (pure, no I/O).</summary>
public sealed class GraphUrlAndCloudTests
{
    // ---- clouds -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null, "public", "graph.microsoft.com", "https://login.microsoftonline.com/", "vault.azure.net", "AzureCloud")]
    [InlineData("", "public", "graph.microsoft.com", "https://login.microsoftonline.com/", "vault.azure.net", "AzureCloud")]
    [InlineData("PUBLIC", "public", "graph.microsoft.com", "https://login.microsoftonline.com/", "vault.azure.net", "AzureCloud")]
    [InlineData("usgov", "usgov", "graph.microsoft.us", "https://login.microsoftonline.us/", "vault.usgovcloudapi.net", "AzureUSGovernment")]
    [InlineData("UsGovDod", "usgovdod", "dod-graph.microsoft.us", "https://login.microsoftonline.us/", "vault.usgovcloudapi.net", "AzureUSGovernmentDoD")]
    [InlineData("china", "china", "microsoftgraph.chinacloudapi.cn", "https://login.chinacloudapi.cn/", "vault.azure.cn", "AzureChinaCloud")]
    public void TheCloudTableMapsKeysToHosts(string? key, string expectedKey, string graphHost, string authority, string vaultSuffix, string environmentName)
    {
        var cloud = GraphCloud.Parse(key);

        Assert.Equal(expectedKey, cloud.Key);
        Assert.Equal(graphHost, cloud.GraphHost);
        Assert.Equal("https://" + graphHost, cloud.GraphOrigin);
        Assert.Equal($"https://{graphHost}/.default", cloud.GraphScope);
        Assert.Equal(authority, cloud.AuthorityHost.AbsoluteUri);
        Assert.Equal(vaultSuffix, cloud.KeyVaultSuffix);
        Assert.Equal($"https://{vaultSuffix}/.default", cloud.KeyVaultScope);
        Assert.Equal(environmentName, cloud.EnvironmentName);
    }

    [Theory]
    [InlineData("evil")]
    [InlineData("https://evil.example")]
    [InlineData("graph.microsoft.com")]
    [InlineData("public ; evil")]
    public void AnythingOutsideTheAllowListIsRejected(string value)
    {
        var error = Assert.Throws<GraphConfigurationException>(() => GraphCloud.Parse(value));
        Assert.Contains("public, usgov, usgovdod, china", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThereAreExactlyFourClouds()
    {
        Assert.Equal(["public", "usgov", "usgovdod", "china"], GraphCloud.All.Select(static c => c.Key));
    }

    // ---- origin check -------------------------------------------------------------------------------------

    [Theory]
    [InlineData("https://graph.microsoft.com/v1.0/users", null)]
    [InlineData("https://graph.microsoft.com/beta/users?$top=1", null)]
    [InlineData("https://GRAPH.MICROSOFT.COM/v1.0/users", null)]
    [InlineData("https://graph.microsoft.com:443/v1.0/users", null)]
    [InlineData("https://graph.microsoft.com/V1.0/users", null)]
    [InlineData("http://graph.microsoft.com/v1.0/users", "scheme")]
    [InlineData("https://graph.microsoft.com:444/v1.0/users", "port")]
    [InlineData("https://graph.microsoft.com:80/v1.0/users", "port")]
    [InlineData("https://user@graph.microsoft.com/v1.0/users", "user information")]
    [InlineData("https://graph.microsoft.com.evil.example/v1.0/users", "host")]
    [InlineData("https://sub.graph.microsoft.com/v1.0/users", "host")]
    [InlineData("https://graph.microsoft.com./v1.0/users", "host")]
    [InlineData("https://127.0.0.1/v1.0/users", "host")]
    [InlineData("https://[::1]/v1.0/users", "host")]
    [InlineData("https://graph.microsoft.com/", "path")]
    [InlineData("https://graph.microsoft.com/v1.0", "path")]
    [InlineData("https://graph.microsoft.com/v2.0/users", "path")]
    [InlineData("https://graph.microsoft.com/v1.0/users#x", "fragment")]
    [InlineData("file:///etc/passwd", "scheme")]
    public void TheOriginCheckAcceptsOnlyHttpsOnTheGraphHostUnderAnApiRoot(string url, string? problem)
    {
        var result = GraphUrl.Check(GraphCloud.Public, new Uri(url));

        if (problem is null)
        {
            Assert.Null(result);
        }
        else
        {
            Assert.NotNull(result);
            Assert.Contains(problem, result, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ErrorMessagesNameOnlyTheAuthorityNeverTheQueryOrUserInfo()
    {
        var error = Assert.Throws<GraphTransportException>(() => GraphUrl.EnsureAllowed(GraphCloud.Public, new Uri("https://user:hunter2@evil.example:8443/v1.0/x?access_token=abc"), "test"));

        Assert.Equal(GraphTransportFailure.UnsafeUrl, error.Failure);
        Assert.Contains("https://evil.example:8443", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("access_token", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/users", true)]
    [InlineData("/users?$filter=a%20eq%20%27b%27", true)]
    [InlineData("/groups/g1/members/$ref", true)]
    [InlineData("users", false)]
    [InlineData("//evil.example/x", false)]
    [InlineData("/\\evil.example/x", false)]
    [InlineData("https://evil.example/x", false)]
    [InlineData("/https://evil.example/x", false)]
    [InlineData("/a\r\nb", false)]
    [InlineData("/", false)]
    [InlineData("", false)]
    public void BatchUrlsMustBeRootRelativePaths(string url, bool ok)
    {
        if (ok)
        {
            GraphUrl.ValidateBatchUrl(url);
        }
        else
        {
            Assert.Equal(GraphTransportFailure.UnsafeUrl, Assert.Throws<GraphTransportException>(() => GraphUrl.ValidateBatchUrl(url)).Failure);
        }
    }

    // ---- query building and log redaction -------------------------------------------------------------------

    [Fact]
    public void QueryNamesKeepTheirDollarSignAndValuesAreFullyEncoded()
    {
        var query = GraphUrl.BuildQuery(new Dictionary<string, string> { ["$filter"] = "a eq 'b c'", ["custom key"] = "x&y=z", ["empty"] = string.Empty });

        Assert.Equal("$filter=a%20eq%20%27b%20c%27&custom%20key=x%26y%3Dz&empty=", query);
        Assert.Equal(string.Empty, GraphUrl.BuildQuery(null));
        Assert.Equal(string.Empty, GraphUrl.BuildQuery(new Dictionary<string, string>()));
    }

    [Fact]
    public void LogRedactionMasksSecretParametersButNotPagingTokens()
    {
        var uri = new Uri("https://graph.microsoft.com/v1.0/users?$skiptoken=abc&access_token=eyJhbGciOiJIUzI1NiJ9.e30.sig&client_secret=zzz&$top=5");

        var text = GraphUrl.RedactForLog(uri);

        Assert.Equal("/v1.0/users?$skiptoken=abc&access_token=***&client_secret=***&$top=5", text);
        Assert.Equal("/v1.0/users", GraphUrl.RedactForLog(new Uri("https://graph.microsoft.com/v1.0/users")));
    }

    // ---- error parser -------------------------------------------------------------------------------------

    private static readonly TimeProvider Clock = new Infrastructure.ManualTimeProvider(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero));

    [Theory]
    [InlineData("3", 3)]
    [InlineData(" 120 ", 120)]
    [InlineData("999999", 300)]
    [InlineData("Wed, 14 Oct 2026 12:00:30 GMT", 30)]
    [InlineData("Wed, 14 Oct 2026 11:00:00 GMT", 0)]
    [InlineData("Wed, 14 Oct 2026 18:00:00 GMT", 300)]
    [InlineData("soon", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void RetryAfterTextIsSecondsOrAnHttpDateClampedToFiveMinutes(string? text, int? expectedSeconds)
    {
        var parsed = GraphErrorParser.ParseRetryAfterText(text, Clock);

        Assert.Equal(expectedSeconds is null ? null : TimeSpan.FromSeconds(expectedSeconds.Value), parsed);
    }

    [Fact]
    public void RetryAfterHeaderValuesAreSecondsOrDates()
    {
        Assert.Equal(TimeSpan.FromSeconds(9), GraphErrorParser.ParseRetryAfter(new RetryConditionHeaderValue(TimeSpan.FromSeconds(9)), Clock));
        Assert.Equal(TimeSpan.FromSeconds(60), GraphErrorParser.ParseRetryAfter(new RetryConditionHeaderValue(new DateTimeOffset(2026, 10, 14, 12, 1, 0, TimeSpan.Zero)), Clock));
        Assert.Null(GraphErrorParser.ParseRetryAfter(null, Clock));
    }

    [Fact]
    public void BatchSubResponseErrorsCarryTheExplicitStatusAndHeaders()
    {
        var body = JsonNode.Parse("{\"error\":{\"code\":\"Request_ResourceNotFound\",\"message\":\"Resource does not exist or one of its queried reference-property objects are not present.\",\"innerError\":{\"request-id\":\"inner\"}}}");

        var notFound = GraphErrorParser.FromBatchResponse(404, body, null, RetryHint.ExpectReplicationLag, Clock);
        Assert.Equal(HttpStatusCode.NotFound, notFound.Status);
        Assert.Equal("Request_ResourceNotFound", notFound.Code);
        Assert.True(notFound.IsReplication);
        Assert.Equal("inner", notFound.RequestId);

        var headers = new JsonObject { ["Retry-After"] = "11", ["request-id"] = "hdr", ["client-request-id"] = "cli" };
        var throttled = GraphErrorParser.FromBatchResponse(429, JsonNode.Parse("{\"error\":{\"code\":\"TooManyRequests\",\"message\":\"slow\"}}"), headers, RetryHint.Default, Clock);
        Assert.Equal(TimeSpan.FromSeconds(11), throttled.RetryAfter);
        Assert.Equal("hdr", throttled.RequestId);
        Assert.Equal("cli", throttled.ClientRequestId);

        var bare = GraphErrorParser.FromBatchResponse(503, null, null, RetryHint.Default, Clock);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, bare.Status);
        Assert.True(bare.IsTransient);
        Assert.Contains("503", bare.GraphMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "Request_BadRequest", "Another object with the same value for property uniqueName already exists.", true, false)]
    [InlineData(HttpStatusCode.Conflict, "Request_MultipleObjectsWithSameKeyValue", "dup", true, false)]
    [InlineData(HttpStatusCode.BadRequest, "Request_BadRequest", "Property displayName is invalid.", false, false)]
    [InlineData(HttpStatusCode.BadRequest, "Request_BadRequest", "The source resource object 'ServicePrincipal_1' don't exist.", false, true)]
    [InlineData(HttpStatusCode.NotFound, "Request_ResourceNotFound", "Resource 'x' does not exist.", false, true)]
    [InlineData(HttpStatusCode.Forbidden, "Authorization_RequestDenied", "Insufficient privileges.", false, false)]
    public void ExceptionFlagsFollowTheRealStatusAndMessage(HttpStatusCode status, string code, string message, bool alreadyExists, bool replicationUnderLag)
    {
        var plain = new GraphException(status, code, message, RetryHint.Default);
        var lagged = new GraphException(status, code, message, RetryHint.ExpectReplicationLag);

        Assert.Equal(alreadyExists, plain.IsAlreadyExists);
        Assert.False(plain.IsReplication);
        Assert.Equal(replicationUnderLag, lagged.IsReplication);
    }

    [Fact]
    public void GraphExceptionMessagesAreRedacted()
    {
        const string secret = "TopSecret-abc123";

        var error = new GraphException(HttpStatusCode.BadRequest, "Request_BadRequest", "Invalid password=" + secret + " given");

        Assert.DoesNotContain(secret, error.GraphMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, new GraphTransportException(GraphTransportFailure.Network, "client_secret=" + secret).Message, StringComparison.Ordinal);
    }
}
