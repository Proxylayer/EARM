using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph.Tests.Infrastructure;
using Xunit;

namespace EArm.Graph.Tests;

/// <summary>
/// Review finding S-02 (scrubber): <see cref="SecretScrubber"/> replaces registered secret VALUES (raw, URL-encoded and JSON-escaped forms) everywhere this
/// assembly builds a message: Graph and transport exceptions, the error parser, <see cref="SecretRedactor.RedactText"/> (hence all logging) and Key Vault.
/// Every test opens its own scope, so nothing leaks to the rest of the run.
/// </summary>
public sealed class Review1B_SecretScrubberTests
{
    private const string Secret = "Tr0ub4dor&3-horse+staple";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- the registry --------------------------------------------------------------------------------------------

    [Fact]
    public void AnEmptyRegistryLeavesTextAlone()
    {
        using var scope = SecretScrubber.Begin();
        Assert.Equal("nothing to hide", SecretScrubber.Scrub("nothing to hide"));
        Assert.Equal(string.Empty, SecretScrubber.Scrub(null));
        Assert.Equal(string.Empty, SecretScrubber.Scrub(string.Empty));
        Assert.False(SecretScrubber.HasValues);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("ab")]
    public void ValuesShorterThanThreeCharactersAreIgnored(string? secret)
    {
        using var scope = SecretScrubber.Begin();
        SecretScrubber.Register(secret);

        Assert.False(SecretScrubber.HasValues);
        Assert.Equal("ab a b", SecretScrubber.Scrub("ab a b"));
    }

    [Fact]
    public void AValueOfExactlyThreeCharactersIsRegistered()
    {
        using var scope = SecretScrubber.Begin();
        SecretScrubber.Register("xyz");

        Assert.Equal("a *** b", SecretScrubber.Scrub("a xyz b"));
    }

    [Fact]
    public void EveryOccurrenceIsReplacedWithTheMaskOrdinally()
    {
        using var scope = SecretScrubber.Begin();
        SecretScrubber.Register("Hunter2!");

        Assert.Equal("*** and *** and hunter2!", SecretScrubber.Scrub("Hunter2! and Hunter2! and hunter2!"));
        Assert.Equal("***", SecretScrubber.Mask);
    }

    [Fact]
    public void TheLongestValueWinsWhenOneContainsAnother()
    {
        using var scope = SecretScrubber.Begin();
        SecretScrubber.Register("abc");
        SecretScrubber.Register("abcdef");

        Assert.Equal("***", SecretScrubber.Scrub("abcdef"));
        Assert.Equal("*** ***", SecretScrubber.Scrub("abc abcdef"));
    }

    [Fact]
    public void TheUrlEncodedAndJsonEscapedFormsAreMaskedToo()
    {
        using var scope = SecretScrubber.Begin();
        SecretScrubber.Register(Secret);

        foreach (var form in new[]
        {
            Secret,
            Uri.EscapeDataString(Secret),
            WebUtility.UrlEncode(Secret),
            System.Text.Json.JsonEncodedText.Encode(Secret).ToString(),
            System.Text.Json.JsonEncodedText.Encode(Secret, System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping).ToString(),
        })
        {
            var scrubbed = SecretScrubber.Scrub("value=" + form + "&x=1");
            Assert.Equal("value=***&x=1", scrubbed);
        }
    }

    [Fact]
    public void AJsonEscapedQuoteAndUnicodeValueIsMasked()
    {
        using var scope = SecretScrubber.Begin();
        const string secret = "pa\"ss\\wörd<1>";
        SecretScrubber.Register(secret);

        var json = new JsonObject { ["message"] = "bad value " + secret }.ToJsonString();

        var scrubbed = SecretScrubber.Scrub(json);

        Assert.Equal("{\"message\":\"bad value ***\"}", scrubbed);
        Assert.DoesNotContain("<1>", scrubbed, StringComparison.Ordinal);
    }

    [Fact]
    public void ClearForgetsEverything()
    {
        using var scope = SecretScrubber.Begin();
        SecretScrubber.Register(Secret);
        SecretScrubber.Clear();

        Assert.False(SecretScrubber.HasValues);
        Assert.Equal(Secret, SecretScrubber.Scrub(Secret));
    }

    [Fact]
    public async Task ARegistryIsSharedByWorkStartedAfterTheRegistrationButNotByOtherFlows()
    {
        using var scope = SecretScrubber.Begin();
        SecretScrubber.Register("flow-one-secret");

        var inherited = await Task.Run(() => SecretScrubber.Scrub("flow-one-secret"), Ct);
        var other = await Task.Run(
            () =>
            {
                using var inner = SecretScrubber.Begin();
                // A nested scope JOINS the outer registry (Review 3I): it sees the outer values and what it registers is visible to the outer flow too.
                SecretScrubber.Register("flow-two-secret");
                return SecretScrubber.Scrub("flow-one-secret flow-two-secret");
            },
            Ct);

        Assert.Equal("***", inherited);
        Assert.Equal("*** ***", other);
        // Review 3I (intentional change): the inner registration now reaches the outer flow, so a catch-all at the program level still masks what an inner scope learned.
        Assert.Equal("***", SecretScrubber.Scrub("flow-two-secret"));
    }

    [Fact]
    public async Task ConcurrentFlowsDoNotSeeEachOthersSecrets()
    {
        var gate = new Barrier(2);
        async Task<string> Run(string mine, string theirs)
        {
            await Task.Yield();
            using var scope = SecretScrubber.Begin();
            SecretScrubber.Register(mine);
            gate.SignalAndWait(TimeSpan.FromSeconds(10), Ct);
            await Task.Delay(20, Ct);
            return SecretScrubber.Scrub(mine + "|" + theirs);
        }

        var results = await Task.WhenAll(Run("alpha-secret", "beta-secret"), Run("beta-secret", "alpha-secret"));

        Assert.Equal("***|beta-secret", results[0]);
        Assert.Equal("***|alpha-secret", results[1]);
    }

    [Fact]
    public async Task RegisteringInsideAChildAsyncMethodIsVisibleWhenTheRegistryWasStartedByTheCaller()
    {
        using var scope = SecretScrubber.Begin();

        await RegisterInChildAsync("child-registered-secret");

        Assert.Equal("***", SecretScrubber.Scrub("child-registered-secret"));
    }

    private static async Task RegisterInChildAsync(string secret)
    {
        await Task.Yield();
        SecretScrubber.Register(secret);
    }

    [Fact]
    public void DisposingTheScopeRestoresTheOuterRegistry()
    {
        using var outer = SecretScrubber.Begin();
        SecretScrubber.Register("outer-secret");
        var inner = SecretScrubber.Begin();
        SecretScrubber.Register("inner-secret");
        Assert.Equal("*** ***", SecretScrubber.Scrub("outer-secret inner-secret"));

        inner.Dispose();
        inner.Dispose();

        // Review 3I (intentional change): an inner scope joins the outer registry, so ending it keeps what it registered.
        Assert.Equal("*** ***", SecretScrubber.Scrub("outer-secret inner-secret"));
    }

    [Fact]
    public async Task ManyThreadsRegisteringAndScrubbingNeverCorruptTheRegistry()
    {
        using var scope = SecretScrubber.Begin();
        var tasks = Enumerable.Range(0, 8).Select(worker => Task.Run(
            () =>
            {
                for (var i = 0; i < 200; i++)
                {
                    SecretScrubber.Register($"secret-{worker}-{i}");
                    _ = SecretScrubber.Scrub($"text secret-{worker}-{i} secret-{worker}-{Math.Max(0, i - 1)}");
                }
            },
            Ct));

        await Task.WhenAll(tasks);

        Assert.Equal("***", SecretScrubber.Scrub("secret-3-199"));
        Assert.Equal("***", SecretScrubber.Scrub("secret-7-0"));
    }

    // ---- the wiring ----------------------------------------------------------------------------------------------

    [Fact]
    public void RedactTextAppliesTheRegisteredValues()
    {
        using var scope = SecretScrubber.Begin();
        SecretScrubber.Register(Secret);

        Assert.Equal("the password was *** ok", SecretRedactor.RedactText("the password was " + Secret + " ok"));
        Assert.Equal("q=***", SecretRedactor.RedactText("q=" + Uri.EscapeDataString(Secret)));
        Assert.Equal("***", SecretRedactor.RedactValue("displayName", Secret));
    }

    [Fact]
    public void AGraphExceptionNeverCarriesARegisteredValue()
    {
        using var scope = SecretScrubber.Begin();
        SecretScrubber.Register(Secret);
        var error = new JsonObject { ["code"] = "Request_BadRequest", ["message"] = "Invalid value '" + Secret + "'.", ["innerError"] = new JsonObject { ["detail"] = Secret } };

        var exception = new GraphException(HttpStatusCode.BadRequest, "Code" + Secret, "Invalid value '" + Secret + "'.", RetryHint.Default, error);

        Assert.DoesNotContain(Secret, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, exception.GraphMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, exception.Code, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, exception.Error!.ToJsonString(), StringComparison.Ordinal);
        Assert.Contains("***", exception.Message, StringComparison.Ordinal);
        Assert.NotSame(error, exception.Error);
    }

    [Fact]
    public void AGraphExceptionThrownLaterStillHidesAValueRegisteredAfterItWasBuilt()
    {
        using var scope = SecretScrubber.Begin();
        var exception = new GraphException(HttpStatusCode.BadRequest, "x", "value is " + Secret);
        SecretScrubber.Register(Secret);

        Assert.DoesNotContain(Secret, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ATransportExceptionNeverCarriesARegisteredValueIncludingItsInnerExceptions()
    {
        using var scope = SecretScrubber.Begin();
        SecretScrubber.Register(Secret);

        var exception = new GraphTransportException(GraphTransportFailure.Network, "failed for " + Secret, new HttpRequestException("inner " + Secret));

        Assert.DoesNotContain(Secret, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheBatchErrorParserScrubsItsOutput()
    {
        using var scope = SecretScrubber.Begin();
        SecretScrubber.Register(Secret);
        var body = JsonNode.Parse("{\"error\":{\"code\":\"Bad\",\"message\":\"echo " + Secret + "\"}}");

        var exception = GraphErrorParser.FromBatchResponse(400, body, null, RetryHint.Default, TimeProvider.System);

        Assert.DoesNotContain(Secret, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, exception.Error!.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEchoedSecretInA400BodyNeverReachesTheMessageToStringOrTheLog()
    {
        using var scope = SecretScrubber.Begin();
        SecretScrubber.Register(Secret);
        var echoed = JsonEscape("The value '" + Secret + "' is not valid; request had " + Uri.EscapeDataString(Secret));
        using var harness = new ClientHarness(new ScriptedHttpHandler(new ScriptedResponse
        {
            Status = HttpStatusCode.BadRequest,
            Body = "{\"error\":{\"code\":\"Request_BadRequest\",\"message\":\"" + echoed + "\",\"innerError\":{\"request-id\":\"r1\"}}}",
        }));

        var error = await Assert.ThrowsAsync<GraphException>(() => harness.Client.PostAsync(new GraphRequest("/applications"), new JsonObject { ["x"] = Secret }, Ct));

        AssertNoSecret(error.Message, error.GraphMessage, error.ToString(), error.Error!.ToJsonString(), harness.Log.AllText);
        Assert.Contains("***", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEchoedSecretInARetriedResponseNeverReachesTheRetryLogLines()
    {
        using var scope = SecretScrubber.Begin();
        SecretScrubber.Register(Secret);
        using var harness = new ClientHarness(new ScriptedHttpHandler(
            ScriptedResponse.Error(HttpStatusCode.ServiceUnavailable, "serviceNotAvailable", "retry " + Secret),
            ScriptedResponse.Json("{\"value\":[]}")));

        await harness.Client.GetAsync(new GraphRequest("/users"), Ct);

        Assert.Contains(harness.Log.Entries, static e => e.EventId.Id == 1020);
        AssertNoSecret(harness.Log.AllText);
    }

    [Fact]
    public async Task ATransportFailureThatEchoesTheSecretIsScrubbed()
    {
        using var scope = SecretScrubber.Begin();
        SecretScrubber.Register(Secret);
        using var harness = new ClientHarness();
        harness.Handler.Fallback = _ => new ScriptedResponse { Throw = new HttpRequestException("proxy said " + Secret) };

        var error = await Assert.ThrowsAsync<GraphTransportException>(() => harness.Client.GetAsync(new GraphRequest("/users"), Ct));

        AssertNoSecret(error.Message, error.ToString(), harness.Log.AllText);
    }

    [Fact]
    public async Task KeyVaultFailureMessagesAreScrubbed()
    {
        using var scope = SecretScrubber.Begin();
        SecretScrubber.Register(Secret);
        var handler = new ScriptedHttpHandler(ScriptedResponse.Error(HttpStatusCode.Forbidden, "Forbidden", "caller used " + Secret));
        using var http = new HttpClient(handler, disposeHandler: false);
        var log = new LogSink();
        var client = new KeyVaultClient(http, new FakeTokenProvider(), new RetryPolicy(new RetryOptions(), new RecordingDelayProvider(), new TestLogger<RetryPolicy>(log)), GraphCloud.Public, TimeSpan.FromSeconds(5), new TestLogger<KeyVaultClient>(log));

        var error = await Assert.ThrowsAsync<GraphException>(() => client.GetSecretAsync("contoso-kv", "bg-initial", null, Ct));

        AssertNoSecret(error.Message, error.ToString(), log.AllText);
    }

    [Fact]
    public async Task KeyVaultNetworkFailuresAreScrubbed()
    {
        using var scope = SecretScrubber.Begin();
        SecretScrubber.Register(Secret);
        var handler = new ScriptedHttpHandler { Fallback = _ => new ScriptedResponse { Throw = new HttpRequestException("reset " + Secret) } };
        using var http = new HttpClient(handler, disposeHandler: false);
        var log = new LogSink();
        var client = new KeyVaultClient(http, new FakeTokenProvider(), new RetryPolicy(new RetryOptions(), new RecordingDelayProvider(), new TestLogger<RetryPolicy>(log)), GraphCloud.Public, TimeSpan.FromSeconds(5), new TestLogger<KeyVaultClient>(log));

        var error = await Assert.ThrowsAsync<GraphTransportException>(() => client.GetSecretAsync("contoso-kv", "bg-initial", null, Ct));

        AssertNoSecret(error.Message, error.ToString(), log.AllText);
    }

    [Fact]
    public async Task KeyVaultRefusesAResponseOverTheSizeLimit()
    {
        var oversized = new string('x', (int)KeyVaultClient.MaxResponseBytes + 10);
        var handler = new ScriptedHttpHandler(ScriptedResponse.Json("{\"value\":\"" + oversized + "\"}"));
        using var http = new HttpClient(handler, disposeHandler: false);
        var log = new LogSink();
        var client = new KeyVaultClient(http, new FakeTokenProvider(), new RetryPolicy(new RetryOptions(), new RecordingDelayProvider(), new TestLogger<RetryPolicy>(log)), GraphCloud.Public, TimeSpan.FromSeconds(5), new TestLogger<KeyVaultClient>(log));

        var error = await Assert.ThrowsAsync<GraphTransportException>(() => client.GetSecretAsync("contoso-kv", "bg-initial", null, Ct));

        Assert.Equal(GraphTransportFailure.ResponseTooLarge, error.Failure);
    }

    private static string JsonEscape(string text)
    {
        return System.Text.Json.JsonEncodedText.Encode(text).ToString();
    }

    private static void AssertNoSecret(params string[] texts)
    {
        foreach (var text in texts)
        {
            Assert.DoesNotContain(Secret, text, StringComparison.Ordinal);
            Assert.DoesNotContain(Uri.EscapeDataString(Secret), text, StringComparison.Ordinal);
            Assert.DoesNotContain(JsonEscape(Secret), text, StringComparison.Ordinal);
        }
    }
}
