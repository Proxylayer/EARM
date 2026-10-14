using System;
using System.Net;
using System.Text.Json.Nodes;
using EntraArm.Az;
using EntraArm.Az.Tests.Infrastructure;
using Xunit;

namespace EntraArm.Az.Tests;

/// <summary>README item 6: each canonical az stderr shape maps to the expected <see cref="GraphException"/> or <see cref="AzCliException"/>.</summary>
public sealed class GraphErrorMappingTests
{
    private static readonly GraphRequest Request = new("/users");

    [Theory]
    [InlineData("notFound", HttpStatusCode.NotFound, "Request_ResourceNotFound", false, false, true, "req-404-0001")]
    [InlineData("badRequestAlreadyExists", HttpStatusCode.BadRequest, "Request_BadRequest", false, true, false, "req-400-0001")]
    [InlineData("badRequestReplication", HttpStatusCode.BadRequest, "Request_BadRequest", false, false, true, "req-400-0002")]
    [InlineData("badRequestTerminal", HttpStatusCode.BadRequest, "Request_BadRequest", false, false, false, "req-400-0003")]
    [InlineData("conflict", HttpStatusCode.Conflict, "Request_MultipleObjectsWithSameKeyValue", false, true, false, "req-409-0001")]
    [InlineData("forbidden", HttpStatusCode.Forbidden, "Authorization_RequestDenied", false, false, false, "req-403-0001")]
    [InlineData("unauthorized", HttpStatusCode.Unauthorized, "InvalidAuthenticationToken", false, false, false, "req-401-0001")]
    [InlineData("tooManyRequests", HttpStatusCode.TooManyRequests, "TooManyRequests", true, false, false, null)]
    [InlineData("tooManyRequestsNoRetryAfter", HttpStatusCode.TooManyRequests, "TooManyRequests", true, false, false, null)]
    [InlineData("serviceUnavailable", HttpStatusCode.ServiceUnavailable, "ServiceUnavailable", true, false, false, null)]
    public void StderrShapeMapsToGraphException(
        string fixtureName,
        HttpStatusCode status,
        string code,
        bool isTransient,
        bool isAlreadyExists,
        bool isReplicationUnderLag,
        string? requestId)
    {
        var stderr = GraphErrorFixtures.Get(fixtureName);

        Assert.True(GraphErrorParser.TryParse(stderr, string.Empty, RetryHint.Default, out var plain));
        Assert.NotNull(plain);
        Assert.Equal(status, plain.Status);
        Assert.Equal(code, plain.Code);
        Assert.Equal(isTransient, plain.IsTransient);
        Assert.Equal(isAlreadyExists, plain.IsAlreadyExists);
        Assert.False(plain.IsReplication, "IsReplication must stay false under RetryHint.Default");
        Assert.Equal(requestId, plain.RequestId);
        Assert.Equal(RetryHint.Default, plain.Hint);
        Assert.NotNull(plain.Error);
        Assert.StartsWith($"Graph {(int)status} {code}", plain.Message, StringComparison.Ordinal);
        Assert.Equal(status == HttpStatusCode.NotFound, plain.IsNotFound);
        Assert.Equal(status == HttpStatusCode.Unauthorized, plain.IsUnauthorized);
        Assert.Equal(status == HttpStatusCode.Forbidden, plain.IsForbidden);

        Assert.True(GraphErrorParser.TryParse(stderr, string.Empty, RetryHint.ExpectReplicationLag, out var lagged));
        Assert.NotNull(lagged);
        Assert.Equal(isReplicationUnderLag, lagged.IsReplication);
        Assert.Equal(RetryHint.ExpectReplicationLag, lagged.Hint);
    }

    [Fact]
    public void RetryAfterIsParsedFromTheThrottleMessage()
    {
        Assert.True(GraphErrorParser.TryParse(GraphErrorFixtures.Get("tooManyRequests"), string.Empty, RetryHint.Default, out var throttled));
        Assert.Equal(TimeSpan.FromSeconds(3), throttled!.RetryAfter);

        Assert.True(GraphErrorParser.TryParse(GraphErrorFixtures.Get("tooManyRequestsNoRetryAfter"), string.Empty, RetryHint.Default, out var plain));
        Assert.Null(plain!.RetryAfter);
    }

    [Theory]
    [InlineData("Too many requests. Retry after 3 seconds.", 3)]
    [InlineData("Retry-After: 120", 120)]
    [InlineData("retry_after=45", 45)]
    [InlineData("retry after 999999 seconds", 300)]
    [InlineData("no hint here", null)]
    [InlineData("", null)]
    public void ParseRetryAfterReadsSecondsAndCapsAtFiveMinutes(string text, int? expectedSeconds)
    {
        var parsed = GraphErrorParser.ParseRetryAfter(text);
        Assert.Equal(expectedSeconds is null ? null : TimeSpan.FromSeconds(expectedSeconds.Value), parsed);
    }

    [Fact]
    public void StderrAndGraphMessageAreRedactedInTheException()
    {
        const string secret = "TopSecret-abc123";
        var stderr = GraphErrorFixtures.Get("badRequestTerminal")
            + " {\"secretText\":\"" + secret + "\"} password=" + secret
            + " Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c";

        Assert.True(GraphErrorParser.TryParse(stderr, string.Empty, RetryHint.Default, out var error));
        Assert.NotNull(error);
        Assert.Equal(HttpStatusCode.BadRequest, error.Status);
        Assert.DoesNotContain(secret, error.RawStandardError);
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9", error.RawStandardError);
        Assert.Contains("\"secretText\": \"***\"", error.RawStandardError);
        Assert.Contains("password=***", error.RawStandardError);

        var leaky = new GraphException(HttpStatusCode.BadRequest, "Request_BadRequest", "Invalid password=" + secret + " given", rawStandardError: "client_secret=" + secret);
        Assert.DoesNotContain(secret, leaky.GraphMessage);
        Assert.DoesNotContain(secret, leaky.Message);
        Assert.DoesNotContain(secret, leaky.RawStandardError);
    }

    [Fact]
    public void ErrorDocumentOnStdoutIsAlsoParsed()
    {
        const string stdout = "{\"error\":{\"code\":\"Authorization_RequestDenied\",\"message\":\"Insufficient privileges to complete the operation.\"}}";

        Assert.True(GraphErrorParser.TryParse(string.Empty, stdout, RetryHint.Default, out var error));
        Assert.Equal(HttpStatusCode.Forbidden, error!.Status);
        Assert.False(GraphErrorParser.TryParse("ERROR: something without a graph document", "not json", RetryHint.Default, out var none));
        Assert.Null(none);
    }

    [Fact]
    public void ErrorDocumentWithExitZeroIsMappedThroughTheStatusTable()
    {
        var result = new AzResult(0, "{\"error\":{\"code\":\"Request_ResourceNotFound\",\"message\":\"Resource 'x' does not exist or one of its queried reference-property objects are not present.\"}}", string.Empty, TimeSpan.FromMilliseconds(5), false);

        var error = Assert.Throws<GraphException>(() => GraphResultMapper.ToGraphResponse(result, "GET", Request));

        Assert.Equal(HttpStatusCode.NotFound, error.Status);
        Assert.True(error.IsNotFound);
    }

    [Theory]
    [InlineData("Request_ResourceNotFound", "", HttpStatusCode.NotFound)]
    [InlineData("Authorization_RequestDenied", "", HttpStatusCode.Forbidden)]
    [InlineData("InvalidAuthenticationToken", "", HttpStatusCode.Unauthorized)]
    [InlineData("Authentication_ExpiredToken", "", HttpStatusCode.Unauthorized)]
    [InlineData("Request_MultipleObjectsWithSameKeyValue", "", HttpStatusCode.Conflict)]
    [InlineData("TooManyRequests", "", HttpStatusCode.TooManyRequests)]
    [InlineData("Request_ThrottledTemporarily", "", HttpStatusCode.TooManyRequests)]
    [InlineData("ServiceUnavailable", "", HttpStatusCode.ServiceUnavailable)]
    [InlineData("UnknownError", "", HttpStatusCode.InternalServerError)]
    [InlineData("BadGateway", "", HttpStatusCode.BadGateway)]
    [InlineData("GatewayTimeout", "", HttpStatusCode.GatewayTimeout)]
    [InlineData("Request_BadRequest", "", HttpStatusCode.BadRequest)]
    [InlineData("", "Request was throttled", HttpStatusCode.TooManyRequests)]
    [InlineData("", "Insufficient privileges to complete the operation.", HttpStatusCode.Forbidden)]
    [InlineData("SomethingNew", "whatever", HttpStatusCode.BadRequest)]
    public void StatusFromCodeMapsKnownCodes(string code, string message, HttpStatusCode expected)
    {
        Assert.Equal(expected, GraphErrorParser.StatusFromCode(code, message));
    }

    [Fact]
    public void BatchSubResponseErrorsCarryTheExplicitStatus()
    {
        var body = JsonNode.Parse("{\"error\":{\"code\":\"Request_ResourceNotFound\",\"message\":\"Resource does not exist or one of its queried reference-property objects are not present.\"}}");

        var notFound = GraphErrorParser.FromBatchResponse(404, body, RetryHint.ExpectReplicationLag);
        Assert.Equal(HttpStatusCode.NotFound, notFound.Status);
        Assert.Equal("Request_ResourceNotFound", notFound.Code);
        Assert.True(notFound.IsReplication);

        var bare = GraphErrorParser.FromBatchResponse(503, null, RetryHint.Default);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, bare.Status);
        Assert.True(bare.IsTransient);
        Assert.Contains("503", bare.GraphMessage);
    }

    [Theory]
    [InlineData(1, "notLoggedIn", null, false, AzCliFailure.NotLoggedIn)]
    [InlineData(1, "connectionError", null, false, AzCliFailure.Transient)]
    [InlineData(127, null, "", false, AzCliFailure.NotInstalled)]
    [InlineData(1, null, "", true, AzCliFailure.Transient)]
    [InlineData(1, null, "ERROR: unexpected keyword argument", false, AzCliFailure.Unknown)]
    [InlineData(1, null, "'az' is not recognized as an internal or external command", false, AzCliFailure.NotInstalled)]
    [InlineData(1, null, "EntraArm: Azure CLI ('az') was not found on PATH.", false, AzCliFailure.NotInstalled)]
    [InlineData(1, null, "ERROR: Interactive authentication is needed. Please run: az login", false, AzCliFailure.NotLoggedIn)]
    [InlineData(1, null, "ERROR: AADSTS700082: The refresh token has expired due to inactivity.", false, AzCliFailure.NotLoggedIn)]
    [InlineData(1, null, "ERROR: ('Connection aborted.', RemoteDisconnected('Remote end closed connection without response'))", false, AzCliFailure.Transient)]
    [InlineData(1, null, "ERROR: Bad Gateway", false, AzCliFailure.Transient)]
    public void AzCliFailuresAreClassified(int exitCode, string? fixtureName, string? literalStderr, bool timedOut, AzCliFailure expected)
    {
        var stderr = fixtureName is null ? literalStderr ?? string.Empty : GraphErrorFixtures.Get(fixtureName);
        var result = new AzResult(timedOut ? -1 : exitCode, string.Empty, stderr, TimeSpan.FromMilliseconds(10), timedOut);

        Assert.Equal(expected, AzCliException.Classify(result));

        var exception = AzCliException.FromResult(result, "az rest");
        Assert.Equal(expected, exception.Failure);
        Assert.Equal(result.ExitCode, exception.ExitCode);
        Assert.Equal(timedOut, exception.TimedOut);
        Assert.Equal(expected is AzCliFailure.Transient or AzCliFailure.Unknown, exception.IsRetryable);
        Assert.StartsWith("az rest: ", exception.Message, StringComparison.Ordinal);
        if (expected == AzCliFailure.NotLoggedIn)
        {
            Assert.Contains("az login --allow-no-subscriptions", exception.Message);
        }

        if (expected == AzCliFailure.NotInstalled)
        {
            Assert.Contains("--az-command", exception.Message);
        }
    }

    [Fact]
    public void FailureWithoutGraphDocumentBecomesAzCliException()
    {
        var result = new AzResult(1, string.Empty, GraphErrorFixtures.Get("notLoggedIn"), TimeSpan.FromMilliseconds(10), false);

        var exception = Assert.Throws<AzCliException>(() => GraphResultMapper.ToGraphResponse(result, "GET", Request));

        Assert.Equal(AzCliFailure.NotLoggedIn, exception.Failure);
        Assert.Contains(AzCliException.LoginHint, exception.Message);
        Assert.Contains("GET https://graph.microsoft.com/v1.0/users", exception.Message);
    }

    [Fact]
    public void NonJsonStdoutWithExitZeroIsAnUnknownAzFailure()
    {
        var result = new AzResult(0, "WARNING: not json at all", string.Empty, TimeSpan.FromMilliseconds(10), false);

        var exception = Assert.Throws<AzCliException>(() => GraphResultMapper.ToGraphResponse(result, "GET", Request));

        Assert.Equal(AzCliFailure.Unknown, exception.Failure);
        Assert.Contains("not JSON", exception.Message);
    }
}
