using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
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

    // ---- the scripted handler's default answer ------------------------------------------------------------

    private static async Task<(HttpStatusCode Status, string Body, string? MediaType)> GetAsync(ScriptedGraphHandler handler, string url, HttpMethod? method = null)
    {
        using var http = new HttpClient(handler, disposeHandler: false);
        using var request = new HttpRequestMessage(method ?? HttpMethod.Get, url);
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
