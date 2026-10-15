using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core;
using EArm.Engine;
using EArm.Engine.Execution;
using EArm.Engine.Providers;
using EArm.Engine.State;
using EArm.Graph;
using EArm.TestSupport;
using Xunit;
using static EArm.Tests.Engine.Review1A;

namespace EArm.Tests.Engine;

/// <summary>
/// After a create whose outcome is uncertain (the answer was lost) or that came back "already exists", the executor looks the object up by key.
/// Graph replicates a new object with some delay, and a displayName key is read through <c>$filter</c>, which answers 200 with an empty list (never
/// a 404) while the object is not replicated yet. The executor used to look once (and in production, where the plan carries no retry policy, never
/// waited at all): a committed create was then reported as failed and created again by the next run. It now polls the key read itself for the
/// replication window (<see cref="ReplicationWait"/>), with or without a retry policy, and never waits the window twice.
/// </summary>
public sealed class ReplicationWaitAfterCreateTests
{
    private const string Collection = "/directory/administrativeUnits";

    private static TemplateDocument Units(params string[] names)
    {
        return Templates.Resources(string.Join(", ", names.Select(n => "{ \"type\": \"Microsoft.Entra/administrativeUnits\", \"apiVersion\": \"" + ApiVersion + "\", \"name\": \"" + n + "\", \"properties\": { \"displayName\": \"" + n + "\" } }")));
    }

    private static string UnitId(string name)
    {
        return "/providers/Microsoft.Entra/administrativeUnits/" + name;
    }

    /// <summary>The planner options the CLI uses: no outer retry policy (HttpGraphClient retries each call itself).</summary>
    private static EArm.Engine.Planning.PlannerOptions WithoutRetryPolicy(EngineHost host)
    {
        return host.Options() with { RetryPolicy = null };
    }

    [Fact]
    public async Task AnAmbiguousCreate_WhoseKeyReadIsEmptyUntilReplicated_IsRecordedAsCreated_WithoutASecondCreate()
    {
        var host = new EngineHost(graph => new LaggingCreateClient(graph, Collection, hiddenReads: 3, LaggingCreateClient.Outcome.CommitThenTimeOut));
        var state = NewState();

        var (_, result, _) = await ExecuteAsync(host, state, Units("au1"), planner: WithoutRetryPolicy(host));

        Assert.True(result.Succeeded, string.Join("\n", result.Operations.Select(o => o.Error)));
        Assert.Equal(1, host.Directory.Count(Collection));
        Assert.Single(host.Fake.Writes, w => w.Method == "POST" && w.Path == Collection);
        var op = Assert.Single(result.Operations);
        Assert.Equal(OperationStatus.Succeeded, op.Status);
        Assert.Equal(ChangeType.Create, op.ChangeType);
        Assert.Contains(result.Diagnostics, d => d.Code == EngineDiagnosticCodes.CreateOutcomeUncertain);
        Assert.Equal(StateProvenance.Created, state.Find(UnitId("au1"))!.Provenance);
        Assert.Equal(IdOf(host.Directory.Objects(Collection).Single()), state.GraphIdOf(UnitId("au1")));
        Assert.Equal([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)], host.ReplicationDelays.Delays);
    }

    [Fact]
    public async Task WithARetryPolicy_TheReplicationWindowIsWaitedOnce_ByTheExecutor()
    {
        var host = new EngineHost(graph => new LaggingCreateClient(graph, Collection, hiddenReads: 2, LaggingCreateClient.Outcome.CommitThenTimeOut));
        var state = NewState();

        var (_, result, _) = await ExecuteAsync(host, state, Units("au1"));

        Assert.True(result.Succeeded, string.Join("\n", result.Operations.Select(o => o.Error)));
        Assert.Single(host.Fake.Writes, w => w.Method == "POST" && w.Path == Collection);
        Assert.Equal(2, host.ReplicationDelays.Delays.Count);
        Assert.Empty(host.Delays.Delays);
    }

    [Fact]
    public async Task AnAlreadyExistsCreate_WhoseKeyReadIsEmptyUntilReplicated_AdoptsTheObject()
    {
        var host = new EngineHost(graph => new LaggingCreateClient(graph, Collection, hiddenReads: 2, LaggingCreateClient.Outcome.ConcurrentCreatorThenAlreadyExists));
        var state = NewState();

        var (_, result, _) = await ExecuteAsync(host, state, Units("au1"), planner: WithoutRetryPolicy(host));

        Assert.True(result.Succeeded, string.Join("\n", result.Operations.Select(o => o.Error)));
        Assert.Equal(1, host.Directory.Count(Collection));
        Assert.Equal(2, host.ReplicationDelays.Delays.Count);
    }

    [Fact]
    public async Task AnAmbiguousCreate_ThatNeverCommitted_FailsWithTheOriginalError_AfterTheBoundedWindow()
    {
        var host = new EngineHost();
        host.Fake.Failures.Add("POST", Collection, _ => new GraphTransportException(GraphTransportFailure.Timeout, "POST: the request timed out.", ambiguous: true), times: 10);

        var (_, result, _) = await ExecuteAsync(host, NewState(), Units("au1"), planner: WithoutRetryPolicy(host));

        Assert.False(result.Succeeded);
        Assert.Contains("timed out", Assert.Single(result.Operations).Error, StringComparison.Ordinal);
        Assert.Equal(0, host.Directory.Count(Collection));
        var window = ReplicationWait.Default;
        Assert.Equal((int)(window.Window / window.Interval), host.ReplicationDelays.Delays.Count);
        Assert.Single(host.Fake.Writes, w => w.Method == "POST" && w.Path == Collection);
    }

    [Fact]
    public void ReplicationWait_RejectsANonPositiveInterval_AndANegativeWindow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReplicationWait { Interval = TimeSpan.Zero });
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReplicationWait { Window = TimeSpan.FromSeconds(-1) });
    }

    /// <summary>
    /// The create of one collection either commits and then loses its answer (a timeout), or loses a race to a concurrent creator ("already exists");
    /// either way the object exists afterwards, but the next <paramref name="hiddenReads"/> reads of the collection do not show it yet (replication lag:
    /// a <c>$filter</c> answers with an empty page, a GET by id with "not found").
    /// </summary>
    private sealed class LaggingCreateClient(IGraphClient inner, string collection, int hiddenReads, LaggingCreateClient.Outcome outcome) : GraphClientDecorator(inner)
    {
        private int _hiddenReadsLeft = hiddenReads;
        private readonly object _gate = new();
        private int _armed;

        public enum Outcome
        {
            CommitThenTimeOut,
            ConcurrentCreatorThenAlreadyExists,
        }

        public override async Task<GraphResponse> PostAsync(GraphRequest request, JsonNode? body, CancellationToken cancellationToken)
        {
            if (request.Path != collection || Interlocked.Exchange(ref _armed, 1) == 1)
            {
                return await base.PostAsync(request, body, cancellationToken);
            }

            if (outcome == Outcome.CommitThenTimeOut)
            {
                await base.PostAsync(request, body, cancellationToken);
                throw new GraphTransportException(GraphTransportFailure.Timeout, "POST: Graph did not answer within 120 s.", ambiguous: true);
            }

            await base.PostAsync(request, body, cancellationToken);
            throw new GraphException(HttpStatusCode.BadRequest, "Request_BadRequest", "Another object with the same value for property displayName already exists.", request.Hint);
        }

        public override async IAsyncEnumerable<JsonNode> GetAllPagesAsync(GraphRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (Hidden(request))
            {
                yield break;
            }

            await foreach (var item in base.GetAllPagesAsync(request, cancellationToken))
            {
                yield return item;
            }
        }

        public override Task<JsonNode?> GetAsync(GraphRequest request, CancellationToken cancellationToken)
        {
            return Hidden(request) ? Task.FromResult<JsonNode?>(null) : base.GetAsync(request, cancellationToken);
        }

        private bool Hidden(GraphRequest request)
        {
            if (Volatile.Read(ref _armed) == 0 || !request.Path.StartsWith(collection, StringComparison.Ordinal))
            {
                return false;
            }

            lock (_gate)
            {
                if (_hiddenReadsLeft == 0)
                {
                    return false;
                }

                _hiddenReadsLeft--;
                return true;
            }
        }
    }
}
