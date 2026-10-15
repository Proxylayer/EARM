using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core;
using EArm.Engine.Execution;
using EArm.Engine.State;
using EArm.Graph;
using EArm.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static EArm.Tests.Engine.Review1A;

namespace EArm.Tests.Engine;

/// <summary>
/// Ctrl+C while a write is on the wire. <see cref="HttpGraphClient"/> used to link the caller's token into the HTTP send, so the POST was aborted
/// although Graph may already have committed it: the executor rethrew the cancellation, recorded the resource as skipped, and the object (and the
/// one-time secret only its response carries) was orphaned. A write that was sent now runs to completion (bounded by the request timeout), its outcome
/// is recorded (the secret goes to the secure outputs file), and only then does the cancellation stop the run: later resources are skipped.
/// The whole deployment runs through the real <see cref="HttpGraphClient"/> over a handler that serves the fake directory and behaves like
/// <see cref="SocketsHttpHandler"/>: a request whose token is cancelled mid-flight is aborted.
/// </summary>
public sealed class InFlightWriteCancellationTests
{
    private static TemplateDocument AppSecretAndDependent()
    {
        return Templates.Resources(
            "{ \"type\": \"Microsoft.Entra/applications\", \"apiVersion\": \"" + ApiVersion + "\", \"name\": \"app1\", \"properties\": { \"displayName\": \"app1\" } }, "
            + "{ \"type\": \"Microsoft.Entra/applications/passwordCredentials\", \"apiVersion\": \"" + ApiVersion + "\", \"name\": \"app1/ci-secret\", \"properties\": { \"endDateTime\": \"2027-10-01T00:00:00Z\" }, "
            + "\"dependsOn\": [\"[resourceId('Microsoft.Entra/applications', 'app1')]\"] }, "
            + "{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"" + ApiVersion + "\", \"name\": \"after\", \"properties\": { \"displayName\": \"after\", \"mailEnabled\": false, \"securityEnabled\": true }, "
            + "\"dependsOn\": [\"[resourceId('Microsoft.Entra/applications/passwordCredentials', 'app1', 'ci-secret')]\"] }");
    }

    [Fact]
    public async Task ACancellationWhileThePostIsInFlight_LetsItComplete_RecordsTheObjectAndItsSecret_AndSkipsTheRest()
    {
        using var directory = new TestDirectory(nameof(ACancellationWhileThePostIsInFlight_LetsItComplete_RecordsTheObjectAndItsSecret_AndSkipsTheRest));
        string path = directory.File("secure.json");
        using var cts = new CancellationTokenSource();
        CancelMidFlightHandler? handler = null;
        var host = new EngineHost(fake =>
        {
            handler = new CancelMidFlightHandler(((FakeGraphClient)fake).Directory, "/addPassword", cts);
            return HttpClientOver(handler);
        });
        var state = NewState();
        var store = new InMemoryStateStore();
        var plan = await host.ExpandAsync(AppSecretAndDependent(), state, options: host.Options() with { RetryPolicy = null, Parallelism = 1 });
        var sink = new FileStateSink(store, state, plan);
        await sink.BeginAsync(Ct);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.Executor().ExecuteAsync(plan, new ExecuteOptions { StateSink = sink, State = state, SecureOutputsFile = path }, cts.Token));

        // The POST was not aborted: Graph committed it and the answer was read.
        Assert.True(handler!.Fired);
        Assert.False(handler.AbortedInFlight);
        var app = Assert.Single(host.Directory.Objects("/applications"));
        var credential = Assert.Single(app["passwordCredentials"]!.AsArray())!.AsObject();

        // Its outcome is recorded: the state entry and the one-time secret.
        string credentialId = "/providers/Microsoft.Entra/applications/app1/passwordCredentials/ci-secret";
        var saved = (await store.LoadAsync(EngineHost.DeploymentName, Ct))!;
        Assert.Equal(StateFile.StatusCancelled, saved.LastStatus);
        Assert.Equal(credential["keyId"]!.GetValue<string>(), saved.GraphIdOf(credentialId));
        Assert.Contains(saved.Operations, o => o.Status == OperationStatus.Succeeded && o.ResourceId.EndsWith("/passwordCredentials/ci-secret", StringComparison.Ordinal));
        var written = JsonNode.Parse(await File.ReadAllTextAsync(path, Ct))!.AsObject();
        Assert.StartsWith("fake-secret-", written[SecureOutputsFile.OneTimeSecretsKey]![credentialId]!["secretText"]!.GetValue<string>(), StringComparison.Ordinal);

        // Only then is the cancellation honoured: nothing after the credential was attempted.
        Assert.Contains(saved.Operations, o => o.Status == OperationStatus.Skipped && o.ResourceId.EndsWith("/groups/after", StringComparison.Ordinal));
        Assert.Equal(0, host.Directory.Count("/groups"));
        Assert.DoesNotContain(handler.Commands, c => c.Contains("/groups", StringComparison.Ordinal) && !c.StartsWith("GET", StringComparison.Ordinal));
    }

    /// <summary>
    /// The cancellation arrives after the provider's write committed but before its next request (here the re-read of an upsert that answered without
    /// a body): that request is cancelled, and the executor looks the object up by key once more, uncancellably, and records it instead of reporting the
    /// resource skipped and orphaning the object.
    /// </summary>
    [Fact]
    public async Task ACancellationBetweenTheProvidersRequests_AfterTheWriteCommitted_StillRecordsTheObject()
    {
        using var cts = new CancellationTokenSource();
        var host = new EngineHost(graph => new CommitThenCancelClient(graph, "/groups(", cts));
        var state = NewState();
        var store = new InMemoryStateStore();
        var plan = await host.ExpandAsync(Groups("g1", "g2"), state, options: host.Options() with { RetryPolicy = null, Parallelism = 1 });
        var sink = new FileStateSink(store, state, plan);
        await sink.BeginAsync(Ct);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.Executor().ExecuteAsync(plan, new ExecuteOptions { Parallelism = 1, StateSink = sink, State = state }, cts.Token));

        var saved = (await store.LoadAsync(EngineHost.DeploymentName, Ct))!;
        var created = Assert.Single(host.Directory.Objects("/groups"));
        var recorded = Assert.Single(saved.Resources);
        Assert.Equal(IdOf(created), recorded.Value.GraphId);
        Assert.Contains(saved.Operations, o => o.ResourceId == recorded.Key && o.Status == OperationStatus.Failed && o.Error!.Contains("cancelled", StringComparison.Ordinal));
        Assert.Contains(saved.Operations, o => o.ResourceId != recorded.Key && o.Status == OperationStatus.Skipped);
        Assert.Equal(StateFile.StatusCancelled, saved.LastStatus);
    }

    [Fact]
    public async Task ACancellationWhileAReadIsInFlight_StillAbortsTheRead()
    {
        using var cts = new CancellationTokenSource();
        var fake = new FakeGraphClient();
        var handler = new CancelMidFlightHandler(fake.Directory, "/groups", cts);
        var client = HttpClientOver(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAsync(new GraphRequest("/groups"), cts.Token));

        Assert.True(handler.AbortedInFlight);
    }

    [Fact]
    public async Task AWriteIsNotSent_WhenTheCancellationCameFirst()
    {
        using var cts = new CancellationTokenSource();
        var fake = new FakeGraphClient();
        var handler = new CancelMidFlightHandler(fake.Directory, "/never", cts);
        var client = HttpClientOver(handler);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.PostAsync(new GraphRequest("/groups"), new JsonObject { ["displayName"] = "g" }, cts.Token));

        Assert.Empty(handler.Commands);
        Assert.Equal(0, fake.Directory.Count("/groups"));
    }

    private static HttpGraphClient HttpClientOver(HttpMessageHandler handler)
    {
        var http = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        var retry = new RetryPolicy(new RetryOptions(), new RecordingDelayProvider(), NullLogger<RetryPolicy>.Instance);
        return new HttpGraphClient(http, new FakeJwtTokenProvider { TenantId = EngineHost.Tenant }, retry);
    }

    /// <summary>The first matching upsert commits, the caller cancels, and the answer carries no body; every later call observes the cancelled token.</summary>
    private sealed class CommitThenCancelClient(IGraphClient inner, string pathPrefix, CancellationTokenSource caller) : GraphClientDecorator(inner)
    {
        private int _fired;

        public override async Task<GraphResponse> PatchAsync(GraphRequest request, JsonNode body, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = await base.PatchAsync(request, body, cancellationToken);
            if (request.Path.StartsWith(pathPrefix, StringComparison.Ordinal) && Interlocked.Exchange(ref _fired, 1) == 0)
            {
                await caller.CancelAsync();
                return new GraphResponse(response.Status, null);
            }

            return response;
        }

        public override Task<JsonNode?> GetAsync(GraphRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return base.GetAsync(request, cancellationToken);
        }
    }

    /// <summary>
    /// Serves the fake directory over HTTP. On the first request whose URL contains <c>marker</c>, the caller's cancellation is triggered while the
    /// request is in flight; when that reaches the request's own token (it was linked to the caller's), the request is aborted as a real socket handler
    /// would abort it - nothing is answered, although the server may have committed it. Otherwise it is committed and answered.
    /// </summary>
    private sealed class CancelMidFlightHandler(FakeDirectory directory, string marker, CancellationTokenSource caller) : HttpMessageHandler
    {
        private readonly List<string> _commands = [];
        private readonly object _gate = new();
        private int _fired;

        public bool Fired => Volatile.Read(ref _fired) == 1;

        public bool AbortedInFlight { get; private set; }

        public IReadOnlyList<string> Commands
        {
            get
            {
                lock (_gate)
                {
                    return [.. _commands];
                }
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(CancellationToken.None);
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
            var recorded = new RecordedHttpRequest(request.Method.Method, request.RequestUri!, headers, body);
            lock (_gate)
            {
                _commands.Add(recorded.Command);
            }

            if (request.RequestUri!.AbsoluteUri.Contains(marker, StringComparison.Ordinal) && Interlocked.Exchange(ref _fired, 1) == 0)
            {
                await caller.CancelAsync();
                if (cancellationToken.IsCancellationRequested)
                {
                    AbortedInFlight = true;
                    throw new TaskCanceledException("The request was aborted mid-flight.", null, cancellationToken);
                }
            }

            return FakeDirectoryHttp.Respond(directory, recorded);
        }
    }
}
