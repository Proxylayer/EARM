using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Graph.Tests.Infrastructure;
using Xunit;

namespace EArm.Graph.Tests;

/// <summary>The retry table, row by row, through <see cref="RetryPolicy.Decide"/> and <see cref="RetryPolicy.ExecuteAsync{T}"/>.</summary>
public sealed class RetryPolicyTests
{
    private static readonly TimeSpan[] ThrottleLadder =
    [
        TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(16),
        TimeSpan.FromSeconds(32), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60),
    ];

    private static readonly TimeSpan[] ServerErrorLadder =
    [
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(16),
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static RetryPolicy CreatePolicy(RecordingDelayProvider? delays = null, RetryOptions? options = null)
    {
        return new RetryPolicy(options ?? new RetryOptions(), delays ?? new RecordingDelayProvider(), new TestLogger<RetryPolicy>(new LogSink()));
    }

    private static GraphException Graph(HttpStatusCode status, string code = "", string message = "", RetryHint hint = RetryHint.Default, TimeSpan? retryAfter = null)
    {
        return new GraphException(status, code, message, hint, retryAfter: retryAfter);
    }

    private static GraphException Throttled(TimeSpan? retryAfter = null)
    {
        return Graph(HttpStatusCode.TooManyRequests, "TooManyRequests", "Too many requests.", retryAfter: retryAfter);
    }

    private static GraphException NotFound(RetryHint hint)
    {
        return Graph(HttpStatusCode.NotFound, "Request_ResourceNotFound", "Resource 'x' does not exist or one of its queried reference-property objects are not present.", hint);
    }

    // ---- Decide: 429 -------------------------------------------------------------------------------------------

    [Fact]
    public void Throttled_BacksOffDoublingFromTwoSecondsToSixtyMaxEightRetries()
    {
        var policy = CreatePolicy();
        var state = new RetryState();

        var delays = new List<TimeSpan>();
        for (var i = 0; i < ThrottleLadder.Length; i++)
        {
            var decision = policy.Decide(Throttled(), RetryHint.Default, state);
            Assert.True(decision.Retry, $"retry {i} should be allowed");
            Assert.Equal(RetryReason.Throttled, decision.Reason);
            delays.Add(decision.Delay);
        }

        Assert.Equal(ThrottleLadder, delays);
        Assert.Equal(8, state.ThrottleRetries);
        Assert.Equal(RetryDecision.Stop, policy.Decide(Throttled(), RetryHint.Default, state));
        Assert.Equal(8, state.TotalRetries);
    }

    [Fact]
    public void Throttled_HonoursRetryAfterAndCapsItAtTwoMinutes()
    {
        var policy = CreatePolicy();
        var state = new RetryState();

        var honoured = policy.Decide(Throttled(TimeSpan.FromSeconds(3)), RetryHint.Default, state);
        Assert.True(honoured.Retry);
        Assert.Equal(TimeSpan.FromSeconds(3), honoured.Delay);
        Assert.Equal(RetryReason.Throttled, honoured.Reason);

        var capped = policy.Decide(Throttled(TimeSpan.FromMinutes(10)), RetryHint.Default, state);
        Assert.Equal(TimeSpan.FromSeconds(120), capped.Delay);

        var zero = policy.Decide(Throttled(TimeSpan.Zero), RetryHint.Default, state);
        Assert.Equal(TimeSpan.FromSeconds(8), zero.Delay);
        Assert.Equal(3, state.ThrottleRetries);
    }

    [Fact]
    public void Throttled_IsRetriedUnderExpectReplicationLagToo()
    {
        var policy = CreatePolicy();
        var decision = policy.Decide(Graph(HttpStatusCode.TooManyRequests, hint: RetryHint.ExpectReplicationLag), RetryHint.ExpectReplicationLag, new RetryState());
        Assert.True(decision.Retry);
        Assert.Equal(RetryReason.Throttled, decision.Reason);
    }

    // ---- Decide: 5xx -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public void ServerError_IsExponentialWithAtMostFiveRetries(HttpStatusCode status)
    {
        var policy = CreatePolicy();
        var state = new RetryState();
        var error = Graph(status, "ServiceUnavailable", "Service unavailable.");
        Assert.True(error.IsTransient);

        var delays = new List<TimeSpan>();
        for (var i = 0; i < ServerErrorLadder.Length; i++)
        {
            var decision = policy.Decide(error, RetryHint.Default, state);
            Assert.True(decision.Retry, $"retry {i} should be allowed");
            Assert.Equal(RetryReason.ServerError, decision.Reason);
            delays.Add(decision.Delay);
        }

        Assert.Equal(ServerErrorLadder, delays);
        Assert.Equal(5, state.ServerErrorRetries);
        Assert.Equal(RetryDecision.Stop, policy.Decide(error, RetryHint.Default, state));
    }

    // ---- Decide: replication ----------------------------------------------------------------------------------

    [Fact]
    public void NotFound_IsTerminalUnderDefault()
    {
        var policy = CreatePolicy();
        var error = NotFound(RetryHint.Default);
        Assert.False(error.IsReplication);
        Assert.Equal(RetryDecision.Stop, policy.Decide(error, RetryHint.Default, new RetryState()));
    }

    [Fact]
    public void NotFound_UnderExpectReplicationLag_RetriesEveryFiveSecondsForNinetySeconds()
    {
        var policy = CreatePolicy();
        var state = new RetryState();
        var error = NotFound(RetryHint.ExpectReplicationLag);
        Assert.True(error.IsReplication);

        var retries = 0;
        while (true)
        {
            var decision = policy.Decide(error, RetryHint.ExpectReplicationLag, state);
            if (!decision.Retry)
            {
                break;
            }

            retries++;
            Assert.Equal(TimeSpan.FromSeconds(5), decision.Delay);
            Assert.Equal(RetryReason.Replication, decision.Reason);
            Assert.True(retries <= 18, "replication retries must stop after the 90 s window");
        }

        Assert.Equal(18, retries);
        Assert.Equal(18, state.ReplicationRetries);
        Assert.Equal(TimeSpan.FromSeconds(90), state.ReplicationWaited);
    }

    [Theory]
    [InlineData("Resource '00000000-0000-0000-0000-000000000000' does not exist or one of its queried reference-property objects are not present.")]
    [InlineData("The source resource object 'x' don't exist.")]
    [InlineData("Invalid object identifier 'x'. PrincipalNotFound.")]
    [InlineData("Resource 'x' does not reference a valid application object.")]
    [InlineData("Principal 'x' was not found in the directory.")]
    public void BadRequestWithReplicationMessage_IsReplicationOnlyUnderExpectReplicationLag(string message)
    {
        var policy = CreatePolicy();

        var lagged = Graph(HttpStatusCode.BadRequest, "Request_BadRequest", message, RetryHint.ExpectReplicationLag);
        Assert.True(lagged.IsReplication);
        Assert.False(lagged.IsAlreadyExists);
        var decision = policy.Decide(lagged, RetryHint.ExpectReplicationLag, new RetryState());
        Assert.True(decision.Retry);
        Assert.Equal(RetryReason.Replication, decision.Reason);
        Assert.Equal(TimeSpan.FromSeconds(5), decision.Delay);

        var plain = Graph(HttpStatusCode.BadRequest, "Request_BadRequest", message, RetryHint.Default);
        Assert.False(plain.IsReplication);
        Assert.Equal(RetryDecision.Stop, policy.Decide(plain, RetryHint.Default, new RetryState()));
    }

    // ---- Decide: idempotent success ---------------------------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "Request_BadRequest", "Another object with the same value for property uniqueName already exists.")]
    [InlineData(HttpStatusCode.Conflict, "Request_MultipleObjectsWithSameKeyValue", "Another object with the same value for property proxyAddresses already exists.")]
    [InlineData(HttpStatusCode.Conflict, "Request_MultipleObjectsWithSameKeyValue", "")]
    [InlineData(HttpStatusCode.BadRequest, "Request_BadRequest", "Permission entry already exists.")]
    [InlineData(HttpStatusCode.BadRequest, "Request_BadRequest", "Permission being assigned already exists on the object.")]
    [InlineData(HttpStatusCode.BadRequest, "Request_BadRequest", "One or more added object references already exist for the following modified properties: 'members'.")]
    [InlineData(HttpStatusCode.Conflict, "Request_ConflictingObject", "A conflicting object with one or more of the specified property values is present in the directory.")]
    public void AlreadyExists_IsNeverRetried(HttpStatusCode status, string code, string message)
    {
        var policy = CreatePolicy();

        foreach (var hint in new[] { RetryHint.Default, RetryHint.ExpectReplicationLag })
        {
            var error = Graph(status, code, message, hint);
            Assert.True(error.IsAlreadyExists, $"{status} {code} '{message}' should be idempotent-success");
            Assert.False(error.IsReplication);
            Assert.Equal(RetryDecision.Stop, policy.Decide(error, hint, new RetryState()));
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "Request_ResourceNotFound", "already exists")]
    [InlineData(HttpStatusCode.Forbidden, "Authorization_RequestDenied", "already exists")]
    public void AlreadyExistsPhraseOutsideFourHundredOrConflict_DoesNotCount(HttpStatusCode status, string code, string message)
    {
        Assert.False(Graph(status, code, message).IsAlreadyExists);
    }

    // ---- Decide: 401 / 403 / other 400 -----------------------------------------------------------------------

    [Fact]
    public void Unauthorized_GetsExactlyOneAuthRefresh()
    {
        var policy = CreatePolicy();
        var state = new RetryState();
        var error = Graph(HttpStatusCode.Unauthorized, "InvalidAuthenticationToken", "Access token has expired or is not yet valid.");

        var first = policy.Decide(error, RetryHint.Default, state);
        Assert.True(first.Retry);
        Assert.Equal(RetryReason.AuthRefresh, first.Reason);
        Assert.Equal(TimeSpan.Zero, first.Delay);
        Assert.True(state.AuthRefreshed);

        Assert.Equal(RetryDecision.Stop, policy.Decide(error, RetryHint.Default, state));
        Assert.Equal(1, state.TotalRetries);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "Authorization_RequestDenied", "Insufficient privileges to complete the operation.")]
    [InlineData(HttpStatusCode.BadRequest, "Request_BadRequest", "Property displayName is invalid.")]
    [InlineData(HttpStatusCode.BadRequest, "Request_BadRequest", "The object is referenced by one or more policies.")]
    [InlineData(HttpStatusCode.MethodNotAllowed, "Request_UnsupportedQuery", "")]
    [InlineData(HttpStatusCode.Gone, "", "")]
    public void TerminalGraphErrors_AreNotRetried(HttpStatusCode status, string code, string message)
    {
        var policy = CreatePolicy();
        foreach (var hint in new[] { RetryHint.Default, RetryHint.ExpectReplicationLag })
        {
            Assert.Equal(RetryDecision.Stop, policy.Decide(Graph(status, code, message, hint), hint, new RetryState()));
        }
    }

    // ---- Decide: transport failures ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(GraphTransportFailure.Network)]
    [InlineData(GraphTransportFailure.Timeout)]
    public void TransportFailure_GetsThreeAttemptsInTotal(GraphTransportFailure failure)
    {
        var policy = CreatePolicy();
        var state = new RetryState();
        var error = new GraphTransportException(failure, "GET /users: boom");
        Assert.True(error.IsRetryable);

        var first = policy.Decide(error, RetryHint.Default, state);
        Assert.True(first.Retry);
        Assert.Equal(RetryReason.TransportFailure, first.Reason);
        Assert.Equal(TimeSpan.FromSeconds(1), first.Delay);

        var second = policy.Decide(error, RetryHint.Default, state);
        Assert.True(second.Retry);
        Assert.Equal(TimeSpan.FromSeconds(2), second.Delay);

        Assert.Equal(RetryDecision.Stop, policy.Decide(error, RetryHint.Default, state));
        Assert.Equal(3, state.TransportAttempts);
        Assert.Equal(2, state.TotalRetries);
    }

    [Theory]
    [InlineData(GraphTransportFailure.NotAuthenticated)]
    [InlineData(GraphTransportFailure.InvalidResponse)]
    [InlineData(GraphTransportFailure.UnsafeUrl)]
    public void TransportFailure_NotAuthenticatedInvalidAndUnsafe_AreTerminal(GraphTransportFailure failure)
    {
        var policy = CreatePolicy();
        var error = new GraphTransportException(failure, "GET /users: nope");
        Assert.False(error.IsRetryable);
        Assert.Equal(RetryDecision.Stop, policy.Decide(error, RetryHint.Default, new RetryState()));
        Assert.Equal(RetryDecision.Stop, policy.Decide(error, RetryHint.ExpectReplicationLag, new RetryState()));
    }

    // ---- Decide: RetryHint.None and foreign exceptions ----------------------------------------------------------

    [Fact]
    public void HintNone_StopsEverything()
    {
        var policy = CreatePolicy();
        var errors = new Exception[]
        {
            Throttled(TimeSpan.FromSeconds(3)),
            Graph(HttpStatusCode.ServiceUnavailable),
            NotFound(RetryHint.ExpectReplicationLag),
            Graph(HttpStatusCode.Unauthorized, "InvalidAuthenticationToken"),
            new GraphTransportException(GraphTransportFailure.Network, "boom"),
        };

        foreach (var error in errors)
        {
            Assert.Equal(RetryDecision.Stop, policy.Decide(error, RetryHint.None, new RetryState()));
        }
    }

    [Fact]
    public void ForeignExceptions_AreNotRetried()
    {
        var policy = CreatePolicy();
        Assert.Equal(RetryDecision.Stop, policy.Decide(new InvalidOperationException("x"), RetryHint.Default, new RetryState()));
    }

    [Fact]
    public void Backoff_IsBaseTimesTwoToTheAttemptCappedAtMax()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), RetryPolicy.Backoff(TimeSpan.FromSeconds(2), 0, TimeSpan.FromSeconds(60)));
        Assert.Equal(TimeSpan.FromSeconds(16), RetryPolicy.Backoff(TimeSpan.FromSeconds(2), 3, TimeSpan.FromSeconds(60)));
        Assert.Equal(TimeSpan.FromSeconds(60), RetryPolicy.Backoff(TimeSpan.FromSeconds(2), 5, TimeSpan.FromSeconds(60)));
        Assert.Equal(TimeSpan.FromSeconds(30), RetryPolicy.Backoff(TimeSpan.FromSeconds(1), 40, TimeSpan.FromSeconds(30)));
        Assert.Equal(TimeSpan.FromSeconds(1), RetryPolicy.Backoff(TimeSpan.FromSeconds(5), 0, TimeSpan.FromSeconds(1)));
    }

    // ---- ExecuteAsync sequences with the recording delay provider ---------------------------------------------

    [Fact]
    public async Task Execute_ThrottledTwiceThenSuccess_WaitsRetryAfterEachTime()
    {
        var delays = new RecordingDelayProvider();
        var policy = CreatePolicy(delays);
        var script = new ScriptedOperation(Throttled(TimeSpan.FromSeconds(3)), Throttled(TimeSpan.FromSeconds(3)));

        var result = await policy.ExecuteAsync(script.InvokeAsync, RetryHint.Default, null, Ct);

        Assert.Equal("ok", result);
        Assert.Equal(3, script.Calls);
        Assert.Equal([TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3)], delays.Delays);
    }

    [Fact]
    public async Task Execute_ThrottledTwiceWithoutRetryAfter_BacksOffTwoThenFour()
    {
        var delays = new RecordingDelayProvider();
        var policy = CreatePolicy(delays);
        var script = new ScriptedOperation(Throttled(), Throttled());

        var result = await policy.ExecuteAsync(script.InvokeAsync, RetryHint.Default, null, Ct);

        Assert.Equal("ok", result);
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)], delays.Delays);
    }

    [Fact]
    public async Task Execute_ServiceUnavailableSixTimes_ThrowsAfterFiveRetries()
    {
        var delays = new RecordingDelayProvider();
        var policy = CreatePolicy(delays);
        var error = Graph(HttpStatusCode.ServiceUnavailable, "ServiceUnavailable", "Service unavailable.");
        var script = new ScriptedOperation(error, error, error, error, error, error);

        var thrown = await Assert.ThrowsAsync<GraphException>(() => policy.ExecuteAsync(script.InvokeAsync, RetryHint.Default, null, Ct));

        Assert.Same(error, thrown);
        Assert.Equal(6, script.Calls);
        Assert.Equal(ServerErrorLadder, delays.Delays);
    }

    [Fact]
    public async Task Execute_ReplicationThreeTimesThenSuccess_WaitsFiveSecondsEachTime()
    {
        var delays = new RecordingDelayProvider();
        var policy = CreatePolicy(delays);
        var error = NotFound(RetryHint.ExpectReplicationLag);
        var script = new ScriptedOperation(error, error, error);

        var result = await policy.ExecuteAsync(script.InvokeAsync, RetryHint.ExpectReplicationLag, null, Ct);

        Assert.Equal("ok", result);
        Assert.Equal(4, script.Calls);
        Assert.Equal([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)], delays.Delays);
    }

    [Fact]
    public async Task Execute_NotFoundUnderDefault_ThrowsWithoutRetry()
    {
        var delays = new RecordingDelayProvider();
        var policy = CreatePolicy(delays);
        var script = new ScriptedOperation(NotFound(RetryHint.Default));

        await Assert.ThrowsAsync<GraphException>(() => policy.ExecuteAsync(script.InvokeAsync, RetryHint.Default, null, Ct));

        Assert.Equal(1, script.Calls);
        Assert.Empty(delays.Delays);
    }

    [Fact]
    public async Task Execute_UnauthorizedThenSuccess_RefreshesOnceWithoutDelay()
    {
        var delays = new RecordingDelayProvider();
        var policy = CreatePolicy(delays);
        var script = new ScriptedOperation(Graph(HttpStatusCode.Unauthorized, "InvalidAuthenticationToken"));
        var refreshes = 0;

        var result = await policy.ExecuteAsync(
            script.InvokeAsync,
            RetryHint.Default,
            _ =>
            {
                refreshes++;
                return Task.CompletedTask;
            },
            Ct);

        Assert.Equal("ok", result);
        Assert.Equal(1, refreshes);
        Assert.Equal(2, script.Calls);
        Assert.Empty(delays.Delays);
    }

    [Fact]
    public async Task Execute_UnauthorizedTwice_ThrowsAfterTheSingleRefresh()
    {
        var policy = CreatePolicy();
        var error = Graph(HttpStatusCode.Unauthorized, "InvalidAuthenticationToken");
        var script = new ScriptedOperation(error, error);
        var refreshes = 0;

        var thrown = await Assert.ThrowsAsync<GraphException>(() => policy.ExecuteAsync(
            script.InvokeAsync,
            RetryHint.Default,
            _ =>
            {
                refreshes++;
                return Task.CompletedTask;
            },
            Ct));

        Assert.Same(error, thrown);
        Assert.Equal(1, refreshes);
        Assert.Equal(2, script.Calls);
    }

    [Fact]
    public async Task Execute_UnauthorizedWithoutRefreshCallback_IsTerminal()
    {
        var policy = CreatePolicy();
        var script = new ScriptedOperation(Graph(HttpStatusCode.Unauthorized, "InvalidAuthenticationToken"));

        await Assert.ThrowsAsync<GraphException>(() => policy.ExecuteAsync(script.InvokeAsync, RetryHint.Default, null, Ct));

        Assert.Equal(1, script.Calls);
    }

    [Fact]
    public async Task Execute_AlreadyExists_IsThrownImmediately()
    {
        var delays = new RecordingDelayProvider();
        var policy = CreatePolicy(delays);
        var error = Graph(HttpStatusCode.BadRequest, "Request_BadRequest", "Another object with the same value for property uniqueName already exists.", RetryHint.ExpectReplicationLag);
        var script = new ScriptedOperation(error);

        var thrown = await Assert.ThrowsAsync<GraphException>(() => policy.ExecuteAsync(script.InvokeAsync, RetryHint.ExpectReplicationLag, null, Ct));

        Assert.True(thrown.IsAlreadyExists);
        Assert.Equal(1, script.Calls);
        Assert.Empty(delays.Delays);
    }

    [Fact]
    public async Task Execute_TransportFailureThreeTimes_ThrowsAfterTwoRetries()
    {
        var delays = new RecordingDelayProvider();
        var policy = CreatePolicy(delays);
        var error = new GraphTransportException(GraphTransportFailure.Network, "GET /users: connection reset");
        var script = new ScriptedOperation(error, error, error);

        var thrown = await Assert.ThrowsAsync<GraphTransportException>(() => policy.ExecuteAsync(script.InvokeAsync, RetryHint.Default, null, Ct));

        Assert.Same(error, thrown);
        Assert.Equal(3, script.Calls);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], delays.Delays);
    }

    [Fact]
    public async Task Execute_HintNone_NeverRetries()
    {
        var delays = new RecordingDelayProvider();
        var policy = CreatePolicy(delays);
        var script = new ScriptedOperation(Throttled(TimeSpan.FromSeconds(3)));

        await Assert.ThrowsAsync<GraphException>(() => policy.ExecuteAsync(script.InvokeAsync, RetryHint.None, null, Ct));

        Assert.Equal(1, script.Calls);
        Assert.Empty(delays.Delays);
    }

    [Fact]
    public async Task Execute_CancelledToken_StopsBeforeTheNextAttempt()
    {
        var policy = CreatePolicy();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var script = new ScriptedOperation(Throttled());
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => policy.ExecuteAsync(script.InvokeAsync, RetryHint.Default, null, cancellation.Token));

        Assert.Equal(0, script.Calls);
    }

    [Fact]
    public async Task Execute_LogsRetriesWithRedactedMessages()
    {
        var sink = new LogSink();
        var policy = new RetryPolicy(new RetryOptions(), new RecordingDelayProvider(), new TestLogger<RetryPolicy>(sink));
        var script = new ScriptedOperation(Graph(HttpStatusCode.ServiceUnavailable, "ServiceUnavailable", "down; password=Hunter2-secret"));

        await policy.ExecuteAsync(script.InvokeAsync, RetryHint.Default, null, Ct);

        var entry = Assert.Single(sink.Entries, e => e.EventId.Id == 1020);
        Assert.Contains("ServerError", entry.Message);
        Assert.DoesNotContain("Hunter2-secret", sink.AllText);
    }

    /// <summary>An operation that throws the scripted exceptions in order, then returns "ok".</summary>
    private sealed class ScriptedOperation
    {
        private readonly Queue<Exception> _failures;

        public ScriptedOperation(params Exception[] failures)
        {
            _failures = new Queue<Exception>(failures);
        }

        public int Calls { get; private set; }

        public Task<string> InvokeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            if (_failures.Count > 0)
            {
                throw _failures.Dequeue();
            }

            return Task.FromResult("ok");
        }
    }
}
