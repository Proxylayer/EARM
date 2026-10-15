using System;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph.Tests.Infrastructure;
using Xunit;

namespace EArm.Graph.Tests;

/// <summary>Review 3I, finding 12: small tests that pin behaviour the mutation run showed to be unguarded (GP07, GU16, GU19, HC05, HC16, RD06).</summary>
public sealed class Review3I_MutantTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // GP07: a character outside the BMP is percent-encoded as its four UTF-8 bytes, never truncated to its low 16 bits (U+10041 would become 'A').
    [Fact]
    public void ACharacterOutsideTheBasicPlaneIsPercentEncodedAsUtf8()
    {
        Assert.Equal("%F0%90%81%81", GraphPath.Segment("\U00010041"));
        Assert.Equal("a%F0%90%81%81b", GraphPath.Segment("a\U00010041b"));
        Assert.Equal("applications(uniqueName='%F0%9F%98%80')", GraphPath.AlternateKey("/applications", "uniqueName", "\U0001F600").TrimStart('/'));
        Assert.DoesNotContain("A", GraphPath.KeyValue("\U00010041"), StringComparison.Ordinal);
    }

    [Fact]
    public void ALoneSurrogateInAPathSegmentBecomesTheReplacementCharacterBytes()
    {
        Assert.Equal("%EF%BF%BD", GraphPath.Segment("\uDC00"));
    }

    // GU19
    [Fact]
    public void ALoneSurrogateInAQueryValueIsReplacedByU_FFFD()
    {
        Assert.Equal("%EF%BF%BD", GraphUrl.EncodeValue("\uDC00"));
        Assert.Equal("%EF%BF%BD", GraphUrl.EncodeValue("\uD800"));
        Assert.Equal("a%F0%9F%98%80b", GraphUrl.EncodeValue("a\U0001F600b"));
    }

    // GU16
    [Fact]
    public void ASecretLookingTextInsideANonSecretQueryValueIsMaskedAndTheRestIsKept()
    {
        var uri = new Uri("https://graph.microsoft.com/v1.0/groups?$filter=displayName%20eq%20%27x%27&note=password=hunter2&$select=id");

        var redacted = GraphUrl.RedactForLog(uri);

        Assert.DoesNotContain("hunter2", redacted, StringComparison.Ordinal);
        Assert.Contains("$select=id", redacted, StringComparison.Ordinal);
        Assert.Contains("displayName%20eq%20%27x%27", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void ASecretNamedQueryParameterIsMaskedWholeAndAPagingTokenIsNot()
    {
        var uri = new Uri("https://graph.microsoft.com/v1.0/users?password=hunter2&$skiptoken=RFNwdAIAAQAAAB1")!;

        var redacted = GraphUrl.RedactForLog(uri);

        Assert.DoesNotContain("hunter2", redacted, StringComparison.Ordinal);
        Assert.Contains("$skiptoken=RFNwdAIAAQAAAB1", redacted, StringComparison.Ordinal);
    }

    // HC05: a 5xx answer to a POST inside a $batch may follow a commit: ambiguous and not sent again.
    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public async Task ABatchSubResponseOf5xxOnAPostIsAmbiguousAndNotRepeated(int status)
    {
        using var harness = new ClientHarness(new ScriptedHttpHandler(
            ScriptedResponse.Json("{\"responses\":[{\"id\":\"1\",\"status\":" + status + ",\"body\":{\"error\":{\"code\":\"x\",\"message\":\"m\"}}}]}"),
            ScriptedResponse.Json("{}", HttpStatusCode.Created)));

        var results = await harness.Client.BatchAsync([new BatchRequest("POST", "/groups", new JsonObject { ["displayName"] = "g" })], GraphApi.V1, RetryHint.Default, Ct);

        var result = Assert.Single(results);
        Assert.False(result.Succeeded);
        Assert.True(result.Error!.IsAmbiguous);
        Assert.Single(harness.Handler.Requests);
    }

    [Fact]
    public async Task ABatchSubResponseOf500OnAGetIsNotAmbiguousAndIsRepeated()
    {
        using var harness = new ClientHarness(new ScriptedHttpHandler(
            ScriptedResponse.Json("{\"responses\":[{\"id\":\"1\",\"status\":500,\"body\":{\"error\":{\"code\":\"x\",\"message\":\"m\"}}}]}"),
            ScriptedResponse.Json("{\"id\":\"u\"}")));

        var results = await harness.Client.BatchAsync([new BatchRequest("GET", "/users/u")], GraphApi.V1, RetryHint.Default, Ct);

        Assert.True(Assert.Single(results).Succeeded);
        Assert.Equal(2, harness.Handler.Requests.Count);
    }

    // HC16
    [Theory]
    [InlineData("X:Bad")]
    [InlineData("X-Bad\r\nInjected")]
    public async Task AHeaderNameThatIsNotAValidHttpNameIsRejectedBeforeAnythingIsSent(string name)
    {
        using var harness = new ClientHarness(new ScriptedHttpHandler(ScriptedResponse.Json("{}")));

        await Assert.ThrowsAsync<ArgumentException>(() => harness.Client.GetAsync(new GraphRequest("/users").WithHeader(name, "v"), Ct));

        Assert.Empty(harness.Handler.Requests);
    }
    [Fact]
    public async Task AHeaderValueWithALineBreakIsRejected()
    {
        using var harness = new ClientHarness(new ScriptedHttpHandler(ScriptedResponse.Json("{}")));

        await Assert.ThrowsAsync<ArgumentException>(() => harness.Client.GetAsync(new GraphRequest("/users").WithHeader("X-Ok", "a\nb"), Ct));
    }

    // RD06: a text that ends right after the property name (or its colon and blanks) has no value to mask and must not be read past its end.
    [Theory]
    [InlineData("{\"password\":")]
    [InlineData("{\"password\": ")]
    [InlineData("{\"password\":\t")]
    [InlineData("\"password\":")]
    [InlineData("\"secret\" :   ")]
    public void ATextEndingRightAfterASecretPropertyNameIsReturnedUnchanged(string text)
    {
        Assert.Equal(text, SecretRedactor.RedactText(text));
    }

    [Fact]
    public void ATextWhoseSecretValueIsNotQuotedIsLeftAlone()
    {
        Assert.Equal("{\"password\": 12345}", SecretRedactor.RedactText("{\"password\": 12345}"));
    }
}
