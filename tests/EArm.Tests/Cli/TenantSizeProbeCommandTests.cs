using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EArm.Cli;
using EArm.Cli.Hosting;
using EArm.TestSupport;
using Xunit;

namespace EArm.Tests.Cli;

/// <summary>
/// The tenant-size read at the start of every <c>earm deployment</c> run: one <c>GET /users/$count</c> (ConsistencyLevel: eventual, a single attempt)
/// right after the sign-in preflight, which never fails the run. Also the <see cref="ScriptedGraphHandler"/> default that answers it.
/// </summary>
public sealed class TenantSizeProbeCommandTests
{
    private const string UserCount = "GET /v1.0/users/$count";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string CountRule(int status, string? body = null, bool sticky = false)
    {
        return "{ \"match\": { \"method\": \"GET\", \"urlContains\": \"/users/$count\" }, \"status\": " + status + (body is null ? string.Empty : ", \"body\": " + body) + (sticky ? ", \"sticky\": true" : string.Empty) + " }";
    }

    [Theory]
    [InlineData("what-if")]
    [InlineData("create")]
    public async Task EveryRunReadsTheTenantSizeOnceRightAfterThePreflight(string verb)
    {
        using var sandbox = new CliSandbox(nameof(EveryRunReadsTheTenantSizeOnceRightAfterThePreflight) + verb);
        sandbox.UsePreflightOk();
        string template = sandbox.WriteEmptyTemplate();

        var result = verb == "create"
            ? await CliRunner.RunAsync("deployment", verb, "-f", template, "-y", "-o", "json")
            : await CliRunner.RunAsync("deployment", verb, "-f", template, "-o", "json");

        Assert.Equal(0, result.ExitCode);
        var commands = sandbox.RecordedCommands();
        Assert.Equal(2, commands.Count);
        Assert.StartsWith("GET /v1.0/organization", commands[0], StringComparison.Ordinal);
        Assert.Equal(UserCount, commands[1]);
        var probe = sandbox.RecordedRequests()[1];
        Assert.Equal("eventual", probe.Headers["ConsistencyLevel"]);
        Assert.Equal("text/plain", probe.Headers["Accept"]);
        Assert.StartsWith("Bearer ", probe.Headers["Authorization"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedPreflightStopsTheRunBeforeTheTenantSizeIsRead()
    {
        using var sandbox = new CliSandbox(nameof(AFailedPreflightStopsTheRunBeforeTheTenantSizeIsRead));
        sandbox.UseScript("{ \"responses\": [ { \"match\": { \"urlContains\": \"/organization\" }, \"sticky\": true, \"status\": 401, \"body\": " + ScriptedGraphHandler.Error("InvalidAuthenticationToken", "Access token validation failure.") + " } ] }");
        string template = sandbox.WriteEmptyTemplate();

        var result = await CliRunner.RunAsync("deployment", "what-if", "-f", template, "-o", "json");

        Assert.Equal(3, result.ExitCode);
        Assert.NotEmpty(sandbox.RecordedCommands());
        Assert.DoesNotContain(UserCount, sandbox.RecordedCommands());
    }

    public static TheoryData<string, string> UnreadableCounts => new()
    {
        { "403", CountRule(403, ScriptedGraphHandler.Error("Authorization_RequestDenied", "Insufficient privileges to complete the operation.")) },
        { "415", CountRule(415, ScriptedGraphHandler.Error("Request_BadRequest", "The requested media type is not supported.")) },
        { "429", CountRule(429, ScriptedGraphHandler.Error("TooManyRequests", "slow down")) },
        { "503", CountRule(503, ScriptedGraphHandler.Error("ServiceUnavailable", "try later")) },
        { "non-numeric", CountRule(200, "{ \"value\": \"lots\" }") },
        { "text", CountRule(200, "\"lots\"") },
    };

    [Theory]
    [MemberData(nameof(UnreadableCounts))]
    public async Task AnUnreadableTenantSizeIsReadOnceAndTheRunStillSucceeds(string name, string rule)
    {
        using var sandbox = new CliSandbox(nameof(AnUnreadableTenantSizeIsReadOnceAndTheRunStillSucceeds) + name);
        sandbox.UsePreflightOk(extraResponses: rule);
        string template = sandbox.WriteEmptyTemplate();

        var result = await CliRunner.RunAsync("deployment", "create", "-f", template, "-y", "-o", "json");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("Succeeded", result.Json["status"]!.GetValue<string>());

        // One attempt: a retry would have reached the handler's default answer and shown up as a second request.
        Assert.Single(sandbox.RecordedCommands(), c => c == UserCount);
    }

    // ---- overlapped with the Key Vault step and the plan ---------------------------------------------------

    private static string Groups(CliSandbox sandbox)
    {
        var resources = Enumerable.Range(0, 3).Select(i =>
            "{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"sized-group-" + i + "\", \"properties\": { \"displayName\": \"Sized group " + i + "\", \"mailEnabled\": false, \"mailNickname\": \"sized-group-" + i + "\", \"securityEnabled\": true } }");
        return sandbox.WriteTemplate("groups.entra.json", "\"resources\": [ " + string.Join(", ", resources) + " ]");
    }

    /// <summary>Installs, for the current async flow, the sandbox's fakes with <paramref name="handler"/> in front of its Graph handler.</summary>
    private static IDisposable Use(CliSandbox sandbox, HttpMessageHandler handler)
    {
        return ServiceOverrides.UseAmbient(new ServiceOverrides
        {
            Handler = handler,
            Tokens = sandbox.Tokens,
            Delay = new NoDelay(),
            RecordStore = sandbox.Overrides.RecordStore,
            TokenCacheDirectories = sandbox.Overrides.TokenCacheDirectories,
        });
    }

    [Theory]
    [MemberData(nameof(UnreadableCounts))]
    public async Task WithResourcesToRead_AnUnreadableTenantSizeIsStillReadOnce_AndTheRunSucceeds(string name, string rule)
    {
        // The probe now runs alongside the plan's reads: still one request, still never a failure of the run.
        using var sandbox = new CliSandbox(nameof(WithResourcesToRead_AnUnreadableTenantSizeIsStillReadOnce_AndTheRunSucceeds) + name);
        sandbox.Handler.Directory = new FakeDirectory();
        sandbox.UseScript("{ \"responses\": [ " + rule + " ] }");
        string template = Groups(sandbox);

        var result = await CliRunner.RunAsync("deployment", "what-if", "-f", template, "-o", "json");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(3, result.Json["summary"]!["create"]!.GetValue<int>());
        Assert.Single(sandbox.RecordedCommands(), c => c == UserCount);
        Assert.Contains(sandbox.RecordedCommands(), c => c.Contains("/groups", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("what-if")]
    [InlineData("create")]
    public async Task ThePlanDoesNotWaitForTheTenantSize_ButTheCommandDoesNotEndBeforeIt(string verb)
    {
        using var sandbox = new CliSandbox(nameof(ThePlanDoesNotWaitForTheTenantSize_ButTheCommandDoesNotEndBeforeIt) + verb);
        sandbox.Handler.Directory = new FakeDirectory();
        string template = Groups(sandbox);
        using var hold = new HoldUserCount(sandbox.Handler);
        using var scope = Use(sandbox, hold);

        var run = verb == "create"
            ? CliRunner.RunAsync("deployment", verb, "-f", template, "-y", "-o", "json")
            : CliRunner.RunAsync("deployment", verb, "-f", template, "-o", "json");

        await hold.Held.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        if (verb == "what-if")
        {
            // The probe is held: the what-if reads still go out (the probe is not awaited in series)...
            await hold.OthersWhileHeld.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        }

        // ...but the command does not finish while it is outstanding, and create does not start applying: the plan returns only after the probe.
        await Task.Delay(300, Ct);
        Assert.False(run.IsCompleted, "the command ended while the tenant-size probe was still outstanding");
        Assert.All(sandbox.RecordedRequests(), r => Assert.Equal("GET", r.Method));

        hold.Release();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(result.Json);
        Assert.Single(sandbox.RecordedCommands(), c => c == UserCount);
        if (verb == "create")
        {
            Assert.Equal("Succeeded", result.Json["status"]!.GetValue<string>());
            Assert.Equal(3, sandbox.Handler.Directory!.Count("/groups"));
        }
        else
        {
            Assert.Equal(3, result.Json["summary"]!["create"]!.GetValue<int>());
            Assert.DoesNotContain(sandbox.RecordedRequests(), r => r.Method != "GET");
        }
    }

    [Fact]
    public async Task CancellingWhileTheTenantSizeIsOutstanding_EndsTheRunPromptly_AndLeavesNothingRunning()
    {
        using var sandbox = new CliSandbox(nameof(CancellingWhileTheTenantSizeIsOutstanding_EndsTheRunPromptly_AndLeavesNothingRunning));
        sandbox.Handler.Directory = new FakeDirectory();
        string template = Groups(sandbox);
        using var hold = new HoldUserCount(sandbox.Handler);
        using var scope = Use(sandbox, hold);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var run = Program.InvokeAsync(["deployment", "what-if", "-f", template, "-o", "json"], new System.CommandLine.InvocationConfiguration { Output = stdout, Error = stderr }, cancel.Token);
        await hold.Held.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await hold.OthersWhileHeld.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await cancel.CancelAsync();

        int exit = await run.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.NotEqual(0, exit);
        Assert.Contains("Cancelled.", stderr.ToString(), StringComparison.Ordinal);

        // The held probe is not left running once the command has returned (its HTTP client was disposed with the run's services).
        await hold.Ended.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.True(hold.EndedByCancellation, "the outstanding tenant-size request should have been cancelled when the run ended");
        Assert.Single(sandbox.RecordedCommands().Concat(hold.HeldCommands), c => c == UserCount);
        Assert.All(sandbox.RecordedRequests(), r => Assert.Equal("GET", r.Method));
    }

    /// <summary>
    /// Holds <c>GET /users/$count</c> before it reaches the sandbox's handler until <see cref="Release"/> (or until the request is cancelled), and records
    /// whether other requests went out while it was held. Everything else passes straight through.
    /// </summary>
    private sealed class HoldUserCount(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _others = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<string> _heldCommands = [];

        public Task Held => _held.Task;

        public Task OthersWhileHeld => _others.Task;

        public Task Ended => _ended.Task;

        public bool EndedByCancellation { get; private set; }

        /// <summary>The held request while it has not reached the sandbox's handler (which records it only then).</summary>
        public IReadOnlyList<string> HeldCommands
        {
            get
            {
                lock (_heldCommands)
                {
                    return [.. _heldCommands];
                }
            }
        }

        public void Release()
        {
            _release.TrySetResult();
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/users/$count", StringComparison.Ordinal))
            {
                lock (_heldCommands)
                {
                    _heldCommands.Add("GET " + Uri.UnescapeDataString(request.RequestUri.PathAndQuery));
                }

                _held.TrySetResult();
                try
                {
                    await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    EndedByCancellation = true;
                    _ended.TrySetResult();
                    throw;
                }

                lock (_heldCommands)
                {
                    _heldCommands.Clear();
                }

                try
                {
                    return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _ended.TrySetResult();
                }
            }

            if (_held.Task.IsCompleted && !_release.Task.IsCompleted)
            {
                _others.TrySetResult();
            }

            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    // ---- the scripted handler's default answer ------------------------------------------------------------

    private static async Task<(HttpStatusCode Status, string Body, string? MediaType)> GetAsync(ScriptedGraphHandler handler, string url, HttpMethod? method = null, string? accept = null)
    {
        using var http = new HttpClient(handler, disposeHandler: false);
        using var request = new HttpRequestMessage(method ?? HttpMethod.Get, url);
        if (accept is not null)
        {
            request.Headers.TryAddWithoutValidation("Accept", accept);
        }

        using var response = await http.SendAsync(request, Ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(Ct), response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task TheHandlerAnswersTheUserCountAsAPlainTextNumber()
    {
        using var handler = new ScriptedGraphHandler();
        handler.SetScript("{ \"responses\": [] }");

        var small = await GetAsync(handler, "https://graph.microsoft.com/v1.0/users/$count");
        handler.UserCount = 1_234;
        var large = await GetAsync(handler, "https://graph.microsoft.com/beta/users/$count");

        Assert.Equal((HttpStatusCode.OK, "10", "text/plain"), small);
        Assert.Equal((HttpStatusCode.OK, "1234", "text/plain"), large);
        Assert.Equal(10, ScriptedGraphHandler.DefaultUserCount);
        Assert.Equal(2, handler.Requests.Count);
    }

    // Graph serves $count only as plain text: an Accept that rules text/plain out (such as the default application/json) is refused with 415.
    [Theory]
    [InlineData("application/json", false)]
    [InlineData("application/json;odata.metadata=minimal", false)]
    [InlineData("text/plain", true)]
    [InlineData("text/plain; charset=utf-8", true)]
    [InlineData("application/json, text/plain", true)]
    [InlineData("text/*", true)]
    [InlineData("*/*", true)]
    public async Task TheUserCountIsRefusedWith415WhenTheAcceptRulesPlainTextOut(string accept, bool answered)
    {
        using var handler = new ScriptedGraphHandler { UserCount = 1_234 };
        handler.SetScript("{ \"responses\": [] }");

        var (status, body, mediaType) = await GetAsync(handler, "https://graph.microsoft.com/v1.0/users/$count", accept: accept);

        if (answered)
        {
            Assert.Equal((HttpStatusCode.OK, "1234", "text/plain"), (status, body, mediaType));
        }
        else
        {
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, status);
            Assert.Equal("application/json", mediaType);
            Assert.Equal("Request_BadRequest", System.Text.Json.Nodes.JsonNode.Parse(body)!["error"]!["code"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task AScriptedRuleWinsOverTheUserCount()
    {
        using var handler = new ScriptedGraphHandler { UserCount = 5_000 };
        handler.SetScript("{ \"responses\": [ " + CountRule(403, ScriptedGraphHandler.Error("Authorization_RequestDenied", "denied")) + ", " + CountRule(200, "777") + " ] }");

        var denied = await GetAsync(handler, "https://graph.microsoft.com/v1.0/users/$count");
        var scripted = await GetAsync(handler, "https://graph.microsoft.com/v1.0/users/$count");
        var fallback = await GetAsync(handler, "https://graph.microsoft.com/v1.0/users/$count");

        Assert.Equal(HttpStatusCode.Forbidden, denied.Status);
        Assert.Equal((HttpStatusCode.OK, "777"), (scripted.Status, scripted.Body));
        Assert.Equal((HttpStatusCode.OK, "5000"), (fallback.Status, fallback.Body));
    }

    [Fact]
    public async Task WithoutAUserCountTheRequestIsUnscripted()
    {
        using var handler = new ScriptedGraphHandler { UserCount = null };
        handler.SetScript("{ \"responses\": [] }");

        await Assert.ThrowsAsync<InvalidOperationException>(() => GetAsync(handler, "https://graph.microsoft.com/v1.0/users/$count"));
    }

    [Fact]
    public async Task OnlyAGetOfTheUserCountIsAnsweredByDefault()
    {
        using var handler = new ScriptedGraphHandler();
        handler.SetScript("{ \"responses\": [] }");

        await Assert.ThrowsAsync<InvalidOperationException>(() => GetAsync(handler, "https://graph.microsoft.com/v1.0/users"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => GetAsync(handler, "https://graph.microsoft.com/v1.0/groups/$count"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => GetAsync(handler, "https://graph.microsoft.com/v1.0/users/$count", HttpMethod.Post));
    }

    [Fact]
    public async Task TheUserCountIsAnsweredEvenWhileAFakeDirectoryAnswersEverythingElse()
    {
        using var handler = new ScriptedGraphHandler { Directory = new FakeDirectory(), UserCount = 600 };

        var count = await GetAsync(handler, "https://graph.microsoft.com/v1.0/users/$count");

        Assert.Equal((HttpStatusCode.OK, "600"), (count.Status, count.Body));
        Assert.Single(handler.Requests);
    }
}
