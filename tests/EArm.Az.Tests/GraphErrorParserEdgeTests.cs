using System;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EArm.Az;
using Xunit;

namespace EArm.Az.Tests;

/// <summary>
/// <see cref="GraphErrorParser"/> over hostile and unusual error text: truncated and nested documents, braces and
/// escaped quotes inside strings, unknown reason phrases, status fallbacks, retry-after limits, and the invariant that
/// the mapped status for a known code does not depend on its letter case.
/// </summary>
public sealed class GraphErrorParserEdgeTests
{
    private static GraphException? Parse(string? stderr, string? stdout = null, RetryHint hint = RetryHint.Default)
    {
        return GraphErrorParser.TryParse(stderr, stdout, hint, out var error) ? error : null;
    }

    // ---- extraction ----

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ERROR: something broke, no document")]
    [InlineData("{\"error\": \"just a string\"}")]
    [InlineData("{\"error\": [1,2]}")]
    [InlineData("{\"error\": {\"code\": \"x\", \"message\": \"truncated")]
    [InlineData("{ \"error\": ")]
    [InlineData("}{ \"error\" }")]
    public void ExtractErrorObject_ReturnsNull_WhenThereIsNoUsableDocument(string text)
    {
        Assert.Null(GraphErrorParser.ExtractErrorObject(text, out int start));
        Assert.Equal(-1, start);
        Assert.False(GraphErrorParser.TryParse(text, null, RetryHint.Default, out _));
    }

    [Fact]
    public void ExtractErrorObject_SkipsATruncatedCandidate_AndFindsTheNextValidOne()
    {
        string text = "ERROR: {\"error\":{\"code\":\"broken\",\"message\":\"cut off\" then more text ... {\"error\":{\"code\":\"Request_ResourceNotFound\",\"message\":\"the real one\"}}";

        var error = GraphErrorParser.ExtractErrorObject(text, out int start);

        Assert.NotNull(error);
        Assert.Equal("Request_ResourceNotFound", error["code"]!.GetValue<string>());
        Assert.True(start > 0);
    }

    [Fact]
    public void ExtractErrorObject_HandlesBracesAndEscapedQuotesInsideStrings()
    {
        string message = "message with } brace, { another, \\\"quoted\\\" and a backslash \\\\ end";
        string text = "prefix {\"error\":{\"code\":\"Request_BadRequest\",\"message\":\"" + message + "\",\"innerError\":{\"request-id\":\"abc-123\"}}} suffix {not json}";

        var error = GraphErrorParser.ExtractErrorObject(text, out _);

        Assert.NotNull(error);
        Assert.Equal("message with } brace, { another, \"quoted\" and a backslash \\ end", error["message"]!.GetValue<string>());
        Assert.Equal("abc-123", error["innerError"]!["request-id"]!.GetValue<string>());
    }

    [Fact]
    public void ExtractErrorObject_AcceptsTrailingCommas_AndDeepNesting()
    {
        string nested = string.Concat(Enumerable.Repeat("{\"d\":", 30)) + "1" + new string('}', 30);
        string text = "{\"error\":{\"code\":\"X\",\"message\":\"m\",\"details\":" + nested + ",}}";

        var error = GraphErrorParser.ExtractErrorObject(text, out _);

        Assert.NotNull(error);
        Assert.Equal("X", error["code"]!.GetValue<string>());
    }

    [Fact]
    public void ExtractErrorObject_OnAHugeMessage_StillParses()
    {
        string message = string.Concat(Enumerable.Repeat("lorem ipsum dolor ", 12_000));
        string text = "ERROR: Bad Request({\"error\":{\"code\":\"Request_BadRequest\",\"message\":\"" + message + "\"}})";

        var error = Parse(text);

        Assert.NotNull(error);
        Assert.Equal(HttpStatusCode.BadRequest, error.Status);
    }

    [Fact]
    public void TryParse_PrefersStderr_ThenFallsBackToStdout_AndBothEmptyIsFalse()
    {
        string stderr = "ERROR: Forbidden({\"error\":{\"code\":\"Authorization_RequestDenied\",\"message\":\"from stderr\"}})";
        string stdout = "{\"error\":{\"code\":\"Request_ResourceNotFound\",\"message\":\"from stdout\"}}";

        Assert.Equal("from stderr", Parse(stderr, stdout)!.GraphMessage);
        Assert.Equal("from stdout", Parse("no document here", stdout)!.GraphMessage);
        Assert.Equal("from stdout", Parse(null, stdout)!.GraphMessage);
        Assert.Null(Parse(null, null));
        Assert.Null(Parse(string.Empty, string.Empty));
    }

    // ---- status ----

    [Theory]
    [InlineData("ERROR: Bad Request({\"error\":{\"code\":\"x\",\"message\":\"m\"}})", HttpStatusCode.BadRequest)]
    [InlineData("ERROR: Gateway Time-out({\"error\":{\"code\":\"x\",\"message\":\"m\"}})", HttpStatusCode.GatewayTimeout)]
    [InlineData("ERROR: gateway timeout ({\"error\":{\"code\":\"x\",\"message\":\"m\"}})", HttpStatusCode.GatewayTimeout)]
    [InlineData("ERROR: Insufficient Storage({\"error\":{\"code\":\"x\",\"message\":\"m\"}})", HttpStatusCode.InsufficientStorage)]
    [InlineData("ERROR: Unprocessable Entity({\"error\":{\"code\":\"x\",\"message\":\"m\"}})", HttpStatusCode.UnprocessableEntity)]
    [InlineData("status code: 503 ERROR: ({\"error\":{\"code\":\"x\",\"message\":\"m\"}})", HttpStatusCode.ServiceUnavailable)]
    [InlineData("HTTPError 429 ({\"error\":{\"code\":\"x\",\"message\":\"m\"}})", HttpStatusCode.TooManyRequests)]
    [InlineData("status: 200 ({\"error\":{\"code\":\"x\",\"message\":\"m\"}})", HttpStatusCode.BadRequest)]
    [InlineData("({\"error\":{\"code\":\"x\",\"message\":\"m\"}}) Not Found(", HttpStatusCode.BadRequest)]
    public void ReasonPhraseAndNumericStatus_DecideTheStatusBeforeTheCode(string stderr, HttpStatusCode expected)
    {
        var error = Parse(stderr);

        Assert.NotNull(error);
        Assert.Equal(expected, error.Status);
    }

    [Fact]
    public void StatusFromCode_IsCaseInsensitive_ForEveryKnownCode()
    {
        string[] codes =
        [
            "Request_ResourceNotFound", "ResourceNotFound", "ItemNotFound", "Directory_ObjectNotFound", "NotFound", "Authorization_RequestDenied",
            "Authorization_IdentityNotFound", "AccessDenied", "Forbidden", "InvalidAuthenticationToken", "InvalidAuthenticationTokenHeader", "Unauthorized",
            "Request_MultipleObjectsWithSameKeyValue", "Request_ConflictingObject", "Conflict", "TooManyRequests", "ActivityLimitReached",
            "Request_ThrottledTemporarily", "ServiceUnavailable", "Service_ServiceUnavailable", "UnknownError", "InternalServerError", "Service_InternalServerError",
            "generalException", "BadGateway", "GatewayTimeout", "Request_BadRequest", "BadRequest", "Request_UnsupportedQuery", "InvalidRequest",
        ];

        foreach (string code in codes)
        {
            var expected = GraphErrorParser.StatusFromCode(code, string.Empty);
            Assert.Equal(expected, GraphErrorParser.StatusFromCode(code.ToUpperInvariant(), string.Empty));
            Assert.Equal(expected, GraphErrorParser.StatusFromCode(code.ToLowerInvariant(), string.Empty));
            Assert.NotEqual(HttpStatusCode.OK, expected);
        }
    }

    [Theory]
    [InlineData("Authentication_ExpiredToken", "", HttpStatusCode.Unauthorized)]
    [InlineData("SomethingAuthenticationTokenSomething", "", HttpStatusCode.Unauthorized)]
    [InlineData("ExpiredToken", "", HttpStatusCode.Unauthorized)]
    [InlineData("Whatever_Throttled", "", HttpStatusCode.TooManyRequests)]
    [InlineData("", "Your request was THROTTLED, slow down", HttpStatusCode.TooManyRequests)]
    [InlineData("", "Too Many Requests, try later", HttpStatusCode.TooManyRequests)]
    [InlineData("", "Insufficient privileges to complete the operation.", HttpStatusCode.Forbidden)]
    [InlineData("completely_unknown", "nothing recognisable", HttpStatusCode.BadRequest)]
    [InlineData("", "", HttpStatusCode.BadRequest)]
    public void StatusFromCode_FallsBackOnPatternsInTheCodeThenTheMessage(string code, string message, HttpStatusCode expected)
    {
        Assert.Equal(expected, GraphErrorParser.StatusFromCode(code, message));
    }

    [Fact]
    public void StatusFromCode_NullArguments_AreTreatedAsEmpty()
    {
        Assert.Equal(HttpStatusCode.BadRequest, GraphErrorParser.StatusFromCode(null!, null!));
    }

    // ---- error object ----

    [Fact]
    public void FromErrorObject_ToleratesMissingAndNonStringMembers()
    {
        var empty = GraphErrorParser.FromErrorObject(null, new JsonObject(), RetryHint.Default, null);
        var numeric = GraphErrorParser.FromErrorObject(null, new JsonObject { ["code"] = 42, ["message"] = new JsonObject(), ["innerError"] = "not an object" }, RetryHint.Default, null);

        Assert.Equal(HttpStatusCode.BadRequest, empty.Status);
        Assert.Equal(string.Empty, empty.Code);
        Assert.Equal(HttpStatusCode.BadRequest, numeric.Status);
        Assert.Equal(string.Empty, numeric.Code);
        Assert.Null(numeric.RequestId);
        Assert.Throws<ArgumentNullException>(() => GraphErrorParser.FromErrorObject(null, null!, RetryHint.Default, null));
    }

    [Fact]
    public void FromErrorObject_UsesTheExplicitStatus_TheInnerRequestId_AndRetryAfterFromTheMessageThenStderr()
    {
        var error = new JsonObject
        {
            ["code"] = "TooManyRequests",
            ["message"] = "Too many requests. Retry after 7 seconds.",
            ["innerError"] = new JsonObject { ["request-id"] = "req-1" },
        };

        var fromMessage = GraphErrorParser.FromErrorObject(HttpStatusCode.TooManyRequests, error, RetryHint.Default, "Retry-After: 99");
        var fromStderr = GraphErrorParser.FromErrorObject(HttpStatusCode.TooManyRequests, new JsonObject { ["code"] = "TooManyRequests", ["message"] = "slow down" }, RetryHint.Default, "Retry-After: 11");

        Assert.Equal(TimeSpan.FromSeconds(7), fromMessage.RetryAfter);
        Assert.Equal("req-1", fromMessage.RequestId);
        Assert.Equal(TimeSpan.FromSeconds(11), fromStderr.RetryAfter);
    }

    [Theory]
    [InlineData("retry after 5 seconds", 5)]
    [InlineData("Retry-After: 0", 0)]
    [InlineData("RETRY_AFTER=45", 45)]
    [InlineData("retry   after   300", 300)]
    [InlineData("retry after 301", 300)]
    [InlineData("retry after 999999", 300)]
    [InlineData("retry after 9999999", 999999 + 0)]
    [InlineData("retry after -5", null)]
    [InlineData("retry after soon", null)]
    [InlineData("no hint here", null)]
    public void ParseRetryAfter_ReadsDigitsCapsAtFiveMinutes_AndIgnoresTheRest(string text, int? expectedSeconds)
    {
        var parsed = GraphErrorParser.ParseRetryAfter(text);

        if (text.Contains("9999999", StringComparison.Ordinal))
        {
            // Seven digits: the pattern reads at most six, so the leading six digits are what is parsed, then capped.
            Assert.Equal(TimeSpan.FromMinutes(5), parsed);
            return;
        }

        Assert.Equal(expectedSeconds is null ? null : TimeSpan.FromSeconds(expectedSeconds.Value), parsed);
    }

    [Fact]
    public void ParseRetryAfter_NullAndEmpty_AreNull()
    {
        Assert.Null(GraphErrorParser.ParseRetryAfter(null));
        Assert.Null(GraphErrorParser.ParseRetryAfter(string.Empty));
    }

    // ---- batch ----

    [Fact]
    public void FromBatchResponse_UsesTheErrorDocument_WhenThereIsOne_AndAGenericMessageOtherwise()
    {
        var withError = GraphErrorParser.FromBatchResponse(409, new JsonObject { ["error"] = new JsonObject { ["code"] = "Request_ConflictingObject", ["message"] = "dup" } }, RetryHint.Default);
        var textBody = GraphErrorParser.FromBatchResponse(502, JsonValue.Create("<html>bad gateway</html>"), RetryHint.Default);
        var noBody = GraphErrorParser.FromBatchResponse(0, null, RetryHint.ExpectReplicationLag);
        var errorNotAnObject = GraphErrorParser.FromBatchResponse(500, new JsonObject { ["error"] = "string" }, RetryHint.Default);

        Assert.Equal(HttpStatusCode.Conflict, withError.Status);
        Assert.Equal("Request_ConflictingObject", withError.Code);
        Assert.Equal(HttpStatusCode.BadGateway, textBody.Status);
        Assert.Contains("status 502", textBody.GraphMessage, StringComparison.Ordinal);
        Assert.Equal((HttpStatusCode)0, noBody.Status);
        Assert.Equal(HttpStatusCode.InternalServerError, errorNotAnObject.Status);
        Assert.Contains("status 500", errorNotAnObject.GraphMessage, StringComparison.Ordinal);
    }

    // ---- redaction through the parser ----

    [Fact]
    public void ASecretInTheErrorMessageOrStderr_NeverReachesTheExceptionText()
    {
        const string jwt = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIiwibmFtZSI6IkpvaG4ifQ.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c";
        string stderr = "ERROR: Unauthorized({\"error\":{\"code\":\"InvalidAuthenticationToken\",\"message\":\"Access token validation failed. Authorization: Bearer " + jwt + " client_secret=hunter2-canary\"}})";

        var error = Parse(stderr)!;
        string everything = error.ToString() + "|" + error.Message + "|" + error.GraphMessage + "|" + error.RawStandardError;

        Assert.DoesNotContain("hunter2-canary", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c", everything, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Unauthorized, error.Status);
    }

    [Fact]
    public void NonAsciiMessages_RoundTripThroughTheParser()
    {
        string text = "ERROR: Bad Request({\"error\":{\"code\":\"Request_BadRequest\",\"message\":\"Ünïcødé 日本語 🎉 failed\"}})";

        var error = Parse(text)!;

        Assert.Equal("Ünïcødé 日本語 🎉 failed", error.GraphMessage);
        Assert.Equal(Encoding.UTF8.GetByteCount(error.GraphMessage), Encoding.UTF8.GetBytes(error.GraphMessage).Length);
    }
}
