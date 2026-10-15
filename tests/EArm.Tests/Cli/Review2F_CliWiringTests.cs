using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Cli.Hosting;
using EArm.Engine.State;
using EArm.Graph.Auth;
using EArm.TestSupport;
using Xunit;

namespace EArm.Tests.Cli;

/// <summary>
/// Review 2F / mutation review: every flag of <c>deployment what-if</c> / <c>create</c> is asserted through the real command line to reach the engine.
/// A mutant that drops <c>--parallelism</c>, <c>--continue-on-error</c>, <c>--lock-timeout</c>, <c>--on-soft-deleted-conflict</c>, <c>--result-format</c>,
/// <c>--no-pretty-print</c>, <c>-n/--name</c> or <c>--exit-code-on-changes</c> between the parser and the engine, or forgets to hand the preflight's tenant, cloud and signed-in
/// principal on (<c>tenant()</c>, <c>environment()</c>, the protected-object check, the app-only token rule), fails one of these tests. Everything runs in-process
/// against an in-memory directory behind the real <c>HttpClient</c> stack. (<c>--allow-ca-enforce</c> is covered for what-if and create in
/// <see cref="Review1D_CaFlagTests"/>.)
/// </summary>
[Collection(CliCollection.Name)]
public sealed class Review2F_CliWiringTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Group(string name, string? after = null, string? properties = null)
    {
        return "{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"" + name + "\", " +
            (after is null ? string.Empty : "\"dependsOn\": [\"[resourceId('Microsoft.Entra/groups', '" + after + "')]\"], ") +
            "\"properties\": " + (properties ?? "{ \"displayName\": \"Display " + name + "\" }") + " }";
    }

    private static string Resources(params string[] resources)
    {
        return "\"resources\": [ " + string.Join(", ", resources) + " ]";
    }

    private static string[] Deploy(CliSandbox sandbox, string template, string verb, params string[] extra)
    {
        return ["deployment", verb, "-f", template, "--state-dir", sandbox.StateDir, "-o", "json", .. extra];
    }

    private static JsonObject Operation(CliResult result, string resourceName)
    {
        return result.Json["operations"]!.AsArray().Select(o => o!.AsObject()).Single(o => o["resourceId"]!.GetValue<string>().EndsWith("/" + resourceName, StringComparison.Ordinal));
    }

    private static string StatusOf(CliResult result, string resourceName)
    {
        return Operation(result, resourceName)["status"]!.GetValue<string>();
    }

    private static JsonObject ReadState(CliSandbox sandbox, string name)
    {
        return JsonNode.Parse(File.ReadAllText(sandbox.StatePath(name)))!.AsObject();
    }

    // ---- a transport probe: concurrency, holding requests open, injected answers ----

    private sealed class ProbeHandler : DelegatingHandler
    {
        private int _inFlight;
        private int _max;

        public TimeSpan Hold { get; set; }

        /// <summary>Returns a response to answer the request with instead of the scripted fake, or null to pass it on. Receives the request and its body text.</summary>
        public Func<HttpRequestMessage, string?, HttpResponseMessage?>? Intercept { get; set; }

        public int MaxConcurrent => Volatile.Read(ref _max);

        public void ResetMax()
        {
            Volatile.Write(ref _max, 0);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            int now = Interlocked.Increment(ref _inFlight);
            int seen;
            do
            {
                seen = Volatile.Read(ref _max);
            }
            while (now > seen && Interlocked.CompareExchange(ref _max, now, seen) != seen);

            try
            {
                if (Hold > TimeSpan.Zero)
                {
                    await Task.Delay(Hold, cancellationToken).ConfigureAwait(false);
                }

                if (Intercept is { } intercept)
                {
                    string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    if (intercept(request, body) is { } answer)
                    {
                        answer.RequestMessage = request;
                        return answer;
                    }
                }

                return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }

    private static HttpResponseMessage Forbidden()
    {
        return new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("{\"error\":{\"code\":\"Authorization_RequestDenied\",\"message\":\"Insufficient privileges to complete the operation.\"}}", Encoding.UTF8, "application/json"),
        };
    }

    /// <summary>Puts a <see cref="ProbeHandler"/> in front of the sandbox's fake Graph for the rest of the test.</summary>
    private static (ProbeHandler Probe, IDisposable Scope) InstallProbe(CliSandbox sandbox)
    {
        var probe = new ProbeHandler { InnerHandler = sandbox.Handler };
        var overrides = new ServiceOverrides
        {
            Handler = probe,
            Tokens = sandbox.Tokens,
            Delay = new NoDelay(),
            RecordStore = new AuthRecordStore(sandbox.RecordPath),
        };
        return (probe, ServiceOverrides.UseAmbient(overrides));
    }

    // ---- --parallelism ----

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [Trait("Category", "Timing")]
    public async Task Parallelism_BoundsTheConcurrentRequests_OfWhatIfAndCreate_AndIsReallyUsed(int parallelism)
    {
        using var sandbox = new CliSandbox(nameof(Parallelism_BoundsTheConcurrentRequests_OfWhatIfAndCreate_AndIsReallyUsed) + parallelism);
        sandbox.Handler.Directory = new FakeDirectory();
        var (probe, scope) = InstallProbe(sandbox);
        using var _ = scope;
        probe.Hold = TimeSpan.FromMilliseconds(250); // long enough that a loaded machine still overlaps the requests (Category=Timing: run serially)
        string template = sandbox.WriteTemplate("par.entra.json", Resources(Enumerable.Range(0, 8).Select(i => Group("grp-" + i)).ToArray()));

        probe.ResetMax();
        var whatIf = await CliRunner.RunAsync(Deploy(sandbox, template, "what-if", "--parallelism", parallelism.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        int whatIfMax = probe.MaxConcurrent;
        probe.ResetMax();
        var create = await CliRunner.RunAsync(Deploy(sandbox, template, "create", "-y", "--parallelism", parallelism.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        int createMax = probe.MaxConcurrent;

        Assert.Equal(0, whatIf.ExitCode);
        Assert.Equal(0, create.ExitCode);
        Assert.InRange(whatIfMax, 1, parallelism);
        Assert.InRange(createMax, 1, parallelism);
        if (parallelism > 1)
        {
            // The independent groups really did run side by side: a flag that is parsed but ignored (everything serial) fails here.
            Assert.Equal(parallelism, createMax);
        }
    }

    [Fact]
    [Trait("Category", "Timing")]
    public async Task Parallelism_DefaultsToSeveralRequestsInParallel_NotToOne()
    {
        using var sandbox = new CliSandbox(nameof(Parallelism_DefaultsToSeveralRequestsInParallel_NotToOne));
        sandbox.Handler.Directory = new FakeDirectory();
        var (probe, scope) = InstallProbe(sandbox);
        using var _ = scope;
        probe.Hold = TimeSpan.FromMilliseconds(250); // long enough that a loaded machine still overlaps the requests (Category=Timing: run serially)
        string template = sandbox.WriteTemplate("par.entra.json", Resources(Enumerable.Range(0, 8).Select(i => Group("grp-" + i)).ToArray()));

        probe.ResetMax();
        var create = await CliRunner.RunAsync(Deploy(sandbox, template, "create", "-y"));

        Assert.Equal(0, create.ExitCode);
        Assert.InRange(probe.MaxConcurrent, 2, 4);
    }

    // ---- --continue-on-error and the run status (skipped operations count as a failed run) ----

    private static (CliSandbox Sandbox, FakeDirectory Directory, string Template) FailingGroupRig(string label)
    {
        var sandbox = new CliSandbox(label);
        var directory = new FakeDirectory();
        sandbox.Handler.Directory = directory;
        var (probe, _) = InstallProbe(sandbox); // the probe override stays installed until the sandbox resets the ambient overrides on dispose
        probe.Intercept = (request, body) => request.Method != HttpMethod.Get && body is not null && body.Contains("Display grp-a", StringComparison.Ordinal) ? Forbidden() : null;
        // grp-a fails; grp-b is independent; grp-c needs grp-b (second batch, unrelated to the failure); grp-d needs grp-a (second batch, depends on the failure).
        string template = sandbox.WriteTemplate("coe.entra.json", Resources(Group("grp-a"), Group("grp-b"), Group("grp-c", after: "grp-b"), Group("grp-d", after: "grp-a")));
        return (sandbox, directory, template);
    }

    [Fact]
    public async Task WithoutContinueOnError_AFailureStopsEveryLaterBatch_AndTheRunIsFailed()
    {
        var (sandbox, directory, template) = FailingGroupRig(nameof(WithoutContinueOnError_AFailureStopsEveryLaterBatch_AndTheRunIsFailed));
        using var _ = sandbox;

        var result = await CliRunner.RunAsync(Deploy(sandbox, template, "create", "-y", "-n", "stop"));

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("Failed", result.Json["status"]!.GetValue<string>());
        Assert.Equal("Failed", StatusOf(result, "grp-a"));
        Assert.Contains("Authorization_RequestDenied", Operation(result, "grp-a")["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("Succeeded", StatusOf(result, "grp-b"));
        Assert.Equal("Skipped", StatusOf(result, "grp-c"));
        Assert.Equal("Skipped", StatusOf(result, "grp-d"));
        Assert.Equal((1, 1, 2), (result.Json["succeeded"]!.GetValue<int>(), result.Json["failed"]!.GetValue<int>(), result.Json["skipped"]!.GetValue<int>()));
        Assert.Null(directory.Find("/groups", "uniqueName", "grp-c"));
        var state = ReadState(sandbox, "stop");
        Assert.Equal("Failed", state["lastStatus"]!.GetValue<string>());
        Assert.Equal(["/providers/Microsoft.Entra/groups/grp-b"], state["resources"]!.AsObject().Select(p => p.Key).ToArray());
        Assert.Equal(4, state["operations"]!.AsArray().Count);
    }

    [Fact]
    public async Task WithContinueOnError_OnlyTheDependentsOfTheFailureAreSkipped_AndTheRunIsStillFailed()
    {
        var (sandbox, directory, template) = FailingGroupRig(nameof(WithContinueOnError_OnlyTheDependentsOfTheFailureAreSkipped_AndTheRunIsStillFailed));
        using var _ = sandbox;

        var result = await CliRunner.RunAsync(Deploy(sandbox, template, "create", "-y", "-n", "go-on", "--continue-on-error"));

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("Failed", result.Json["status"]!.GetValue<string>());
        Assert.Equal("Failed", StatusOf(result, "grp-a"));
        Assert.Equal("Succeeded", StatusOf(result, "grp-b"));
        Assert.Equal("Succeeded", StatusOf(result, "grp-c"));
        Assert.Equal("Skipped", StatusOf(result, "grp-d"));
        Assert.Contains("grp-a", Operation(result, "grp-d")["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal((2, 1, 1), (result.Json["succeeded"]!.GetValue<int>(), result.Json["failed"]!.GetValue<int>(), result.Json["skipped"]!.GetValue<int>()));
        Assert.NotNull(directory.Find("/groups", "uniqueName", "grp-c"));
        Assert.Null(directory.Find("/groups", "uniqueName", "grp-d"));
        var state = ReadState(sandbox, "go-on");
        Assert.Equal("Failed", state["lastStatus"]!.GetValue<string>());
        Assert.Equal(["/providers/Microsoft.Entra/groups/grp-b", "/providers/Microsoft.Entra/groups/grp-c"], state["resources"]!.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task ARunWithNoFailureAndNoSkip_IsSucceeded_InTheResultAndInTheState()
    {
        using var sandbox = new CliSandbox(nameof(ARunWithNoFailureAndNoSkip_IsSucceeded_InTheResultAndInTheState));
        sandbox.Handler.Directory = new FakeDirectory();
        string template = sandbox.WriteTemplate("ok.entra.json", Resources(Group("grp-a"), Group("grp-b", after: "grp-a")));

        var result = await CliRunner.RunAsync(Deploy(sandbox, template, "create", "-y", "-n", "fine"));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("Succeeded", result.Json["status"]!.GetValue<string>());
        Assert.Equal((2, 0, 0), (result.Json["succeeded"]!.GetValue<int>(), result.Json["failed"]!.GetValue<int>(), result.Json["skipped"]!.GetValue<int>()));
        Assert.Equal("Succeeded", ReadState(sandbox, "fine")["lastStatus"]!.GetValue<string>());
    }

    // ---- --lock-timeout, and which verbs lock ----

    [Fact]
    public async Task LockTimeout_APositiveValueWaitsForTheHolder_ThenTheRunProceeds()
    {
        using var sandbox = new CliSandbox(nameof(LockTimeout_APositiveValueWaitsForTheHolder_ThenTheRunProceeds));
        sandbox.UsePreflightOk();
        string template = sandbox.WriteEmptyTemplate();
        var holder = await sandbox.Store.LockAsync("empty", TimeSpan.Zero, Ct);

        var run = CliRunner.RunAsync(Deploy(sandbox, template, "create", "-y", "--lock-timeout", "20"));
        await Task.Delay(700, Ct);
        Assert.False(run.IsCompleted, "the run must be waiting for the lock, not have failed or finished");
        await holder.DisposeAsync();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("Succeeded", result.Json["status"]!.GetValue<string>());
        Assert.False(File.Exists(sandbox.LockPath("empty")));
    }

    [Fact]
    [Trait("Category", "Timing")]
    public async Task LockTimeout_IsHonouredInSeconds_ThenExitsFour()
    {
        using var sandbox = new CliSandbox(nameof(LockTimeout_IsHonouredInSeconds_ThenExitsFour));
        sandbox.UsePreflightOk();
        string template = sandbox.WriteEmptyTemplate();
        await using var holder = await sandbox.Store.LockAsync("empty", TimeSpan.Zero, Ct);

        var clock = Stopwatch.StartNew();
        var result = await CliRunner.RunAsync(Deploy(sandbox, template, "create", "-y", "--lock-timeout", "1"));
        clock.Stop();

        Assert.Equal(4, result.ExitCode);
        Assert.InRange(clock.Elapsed, TimeSpan.FromMilliseconds(900), TimeSpan.FromSeconds(60));
        // -o json: the refusal is the one JSON document on stdout (review round 2, S2-20), not prose on stderr.
        Assert.Equal("Failed", result.Json["status"]!.GetValue<string>());
        Assert.Contains("is locked", result.Json["diagnostics"]![0]!["message"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhatIfTakesNoLock_ButCreateAndDeleteDo()
    {
        using var sandbox = new CliSandbox(nameof(WhatIfTakesNoLock_ButCreateAndDeleteDo));
        sandbox.UsePreflightOk();
        string template = sandbox.WriteEmptyTemplate();
        await sandbox.WriteStateAsync(StateFile.CreateNew("empty", CliSandbox.Tenant, DateTimeOffset.UtcNow));
        await using var holder = await sandbox.Store.LockAsync("empty", TimeSpan.Zero, Ct);

        var whatIf = await CliRunner.RunAsync(Deploy(sandbox, template, "what-if"));
        sandbox.UsePreflightOk();
        var whatIfAlias = await CliRunner.RunAsync(Deploy(sandbox, template, "create", "--what-if"));
        sandbox.UsePreflightOk();
        var create = await CliRunner.RunAsync(Deploy(sandbox, template, "create", "-y", "--lock-timeout", "0"));
        var delete = await CliRunner.RunAsync("deployment", "delete", "-n", "empty", "-y", "--state-dir", sandbox.StateDir);

        Assert.Equal(0, whatIf.ExitCode);
        Assert.Equal(0, whatIfAlias.ExitCode);
        Assert.Contains("without taking its lock", whatIf.StandardError, StringComparison.Ordinal);
        Assert.Equal(4, create.ExitCode);
        Assert.Equal(4, delete.ExitCode);
        Assert.True(File.Exists(sandbox.StatePath("empty")), "a locked delete must not remove the state");
    }

    // ---- --on-soft-deleted-conflict ----

    [Fact]
    public async Task OnSoftDeletedConflict_ErrorIsTheDefault_RestoreRestores()
    {
        using var sandbox = new CliSandbox(nameof(OnSoftDeletedConflict_ErrorIsTheDefault_RestoreRestores));
        var directory = new FakeDirectory();
        sandbox.Handler.Directory = directory;
        var deleted = directory.AddDeleted("/groups", new JsonObject
        {
            ["uniqueName"] = "m365-team",
            ["displayName"] = "Old team",
            ["mailNickname"] = "m365-team",
            ["groupTypes"] = new JsonArray("Unified"),
            ["mailEnabled"] = true,
            ["securityEnabled"] = false,
        });
        string deletedId = deleted["id"]!.GetValue<string>();
        string template = sandbox.WriteTemplate("m365.entra.json", Resources(Group("m365-team", properties: "{ \"displayName\": \"M365 Team\", \"groupTypes\": [\"Unified\"] }")));

        var byDefault = await CliRunner.RunAsync(Deploy(sandbox, template, "create", "-y", "-n", "by-default"));
        var explicitError = await CliRunner.RunAsync(Deploy(sandbox, template, "create", "-y", "-n", "explicit", "--on-soft-deleted-conflict", "error"));
        bool restoredBefore = sandbox.RecordedCommands().Any(c => c.Contains("/restore", StringComparison.Ordinal));
        var restore = await CliRunner.RunAsync(Deploy(sandbox, template, "create", "-y", "-n", "restore", "--on-soft-deleted-conflict", "restore"));

        Assert.Equal(1, byDefault.ExitCode);
        Assert.Contains("SoftDeletedConflict", byDefault.StandardOutput + byDefault.StandardError, StringComparison.Ordinal);
        Assert.Equal(1, explicitError.ExitCode);
        Assert.False(restoredBefore, "nothing is restored unless asked");
        Assert.Equal(0, restore.ExitCode);
        Assert.Contains(sandbox.RecordedCommands(), c => c == "POST /v1.0/directory/deletedItems/" + deletedId + "/restore");
        Assert.Equal(0, directory.Count("/directory/deletedItems"));
        Assert.Equal(deletedId, directory.Find("/groups", "uniqueName", "m365-team")!["id"]!.GetValue<string>());
    }

    // ---- what-if flags ----

    [Fact]
    public async Task ExitCodeOnChanges_IsOffByDefault_AndAppliesToTheCreateWhatIfAlias()
    {
        using var sandbox = new CliSandbox(nameof(ExitCodeOnChanges_IsOffByDefault_AndAppliesToTheCreateWhatIfAlias));
        var directory = new FakeDirectory();
        sandbox.Handler.Directory = directory;
        string template = sandbox.WriteTemplate("changes.entra.json", Resources(Group("grp-a")));

        var plain = await CliRunner.RunAsync(Deploy(sandbox, template, "what-if"));
        var flagged = await CliRunner.RunAsync(Deploy(sandbox, template, "what-if", "--exit-code-on-changes"));
        var alias = await CliRunner.RunAsync(Deploy(sandbox, template, "create", "--what-if", "--exit-code-on-changes"));
        var aliasPlain = await CliRunner.RunAsync(Deploy(sandbox, template, "create", "--what-if"));

        Assert.Equal(0, plain.ExitCode);
        Assert.Equal(1, plain.Json["summary"]!["create"]!.GetValue<int>());
        Assert.Equal(5, flagged.ExitCode);
        Assert.Equal(5, alias.ExitCode);
        Assert.Equal(0, aliasPlain.ExitCode);
        Assert.Null(directory.Find("/groups", "uniqueName", "grp-a"));
        Assert.False(File.Exists(sandbox.StatePath("changes")), "a what-if never writes a state");
        Assert.DoesNotContain(sandbox.RecordedRequests(), r => r.Method != "GET");

        // Once deployed the same flag exits 0: it follows the what-if, not the verb.
        Assert.Equal(0, (await CliRunner.RunAsync(Deploy(sandbox, template, "create", "-y"))).ExitCode);
        Assert.Equal(0, (await CliRunner.RunAsync(Deploy(sandbox, template, "what-if", "--exit-code-on-changes"))).ExitCode);
    }

    [Fact]
    public async Task ResultFormat_ResourceIdOnly_DropsThePayloads_FullKeepsThem()
    {
        using var sandbox = new CliSandbox(nameof(ResultFormat_ResourceIdOnly_DropsThePayloads_FullKeepsThem));
        sandbox.Handler.Directory = new FakeDirectory();
        string template = sandbox.WriteTemplate("fmt.entra.json", Resources(Group("grp-a")));

        var full = await CliRunner.RunAsync(Deploy(sandbox, template, "what-if"));
        var explicitFull = await CliRunner.RunAsync(Deploy(sandbox, template, "what-if", "--result-format", "FullResourcePayloads"));
        var idOnly = await CliRunner.RunAsync(Deploy(sandbox, template, "what-if", "--result-format", "ResourceIdOnly"));
        var createIdOnly = await CliRunner.RunAsync(Deploy(sandbox, template, "create", "--what-if", "--result-format", "ResourceIdOnly"));

        var fullChange = Assert.Single(full.Json["changes"]!.AsArray())!.AsObject();
        Assert.NotNull(fullChange["after"]);
        Assert.NotNull(Assert.Single(explicitFull.Json["changes"]!.AsArray())!["after"]);
        var idChange = Assert.Single(idOnly.Json["changes"]!.AsArray())!.AsObject();
        Assert.Null(idChange["after"]);
        Assert.Equal("Create", idChange["changeType"]!.GetValue<string>());
        Assert.Equal(fullChange["resourceId"]!.GetValue<string>(), idChange["resourceId"]!.GetValue<string>());
        Assert.Null(Assert.Single(createIdOnly.Json["changes"]!.AsArray())!["after"]);
    }

    [Fact]
    public async Task NoPrettyPrint_WritesOneLineOfJson_ForWhatIfAndCreate()
    {
        using var sandbox = new CliSandbox(nameof(NoPrettyPrint_WritesOneLineOfJson_ForWhatIfAndCreate));
        sandbox.Handler.Directory = new FakeDirectory();
        string template = sandbox.WriteTemplate("compact.entra.json", Resources(Group("grp-a")));

        var pretty = await CliRunner.RunAsync(Deploy(sandbox, template, "what-if"));
        var compact = await CliRunner.RunAsync(Deploy(sandbox, template, "what-if", "--no-pretty-print"));
        var compactCreate = await CliRunner.RunAsync(Deploy(sandbox, template, "create", "-y", "--no-pretty-print"));
        var prettyCreate = await CliRunner.RunAsync(Deploy(sandbox, template, "create", "-y", "-n", "second"));

        Assert.Contains('\n', pretty.StandardOutput.Trim());
        Assert.DoesNotContain('\n', compact.StandardOutput.Trim());
        Assert.NotNull(compact.Json["changes"]);
        Assert.DoesNotContain('\n', compactCreate.StandardOutput.Trim());
        Assert.Equal("Succeeded", compactCreate.Json["status"]!.GetValue<string>());
        Assert.Contains('\n', prettyCreate.StandardOutput.Trim());
    }

    // ---- -n / --name and --state-dir ----

    [Theory]
    [InlineData("-n")]
    [InlineData("--name")]
    public async Task Name_ChoosesTheStateFile_TheLockAndTheReportedDeploymentName(string flag)
    {
        using var sandbox = new CliSandbox(nameof(Name_ChoosesTheStateFile_TheLockAndTheReportedDeploymentName) + flag.Length);
        sandbox.UsePreflightOk();
        string template = sandbox.WriteEmptyTemplate("stem.entra.json");
        sandbox.WriteLock("chosen", DateTimeOffset.UtcNow);

        var locked = await CliRunner.RunAsync(Deploy(sandbox, template, "create", "-y", "--lock-timeout", "0", flag, "chosen"));
        var unlocked = await CliRunner.RunAsync(Deploy(sandbox, template, "create", "-y", "--lock-timeout", "0"));
        var whatIf = await CliRunner.RunAsync(Deploy(sandbox, template, "what-if", flag, "other"));

        Assert.Equal(4, locked.ExitCode);
        Assert.Contains("'chosen'", locked.Json["diagnostics"]![0]!["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(0, unlocked.ExitCode);
        Assert.Equal("stem", unlocked.Json["deploymentName"]!.GetValue<string>());
        Assert.True(File.Exists(sandbox.StatePath("stem")));
        Assert.False(File.Exists(sandbox.StatePath("chosen")));
        Assert.Equal("other", whatIf.Json["deploymentName"]!.GetValue<string>());
    }

    [Fact]
    public async Task StateDir_IsWhereTheStateLands_AndNothingIsWrittenNextToTheTemplate()
    {
        using var sandbox = new CliSandbox(nameof(StateDir_IsWhereTheStateLands_AndNothingIsWrittenNextToTheTemplate));
        sandbox.UsePreflightOk();
        string template = sandbox.WriteEmptyTemplate();
        string custom = Path.Combine(sandbox.Root, "custom-state");

        var result = await CliRunner.RunAsync("deployment", "create", "-f", template, "-y", "-o", "json", "--state-dir", custom);

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(Path.Combine(custom, "empty.json")));
        Assert.False(Directory.Exists(sandbox.StateDir));
        Assert.False(Directory.Exists(Path.Combine(sandbox.TemplatesDir, ".earm")));
    }

    // ---- the preflight's tenant, cloud and principal reach the engine ----

    [Fact]
    public async Task TenantAndEnvironmentFunctions_AreFilledFromThePreflight()
    {
        using var sandbox = new CliSandbox(nameof(TenantAndEnvironmentFunctions_AreFilledFromThePreflight));
        sandbox.UsePreflightOk();
        string template = sandbox.WriteTemplate(
            "env.entra.json",
            "\"resources\": [], \"outputs\": { " +
            "\"cloud\": { \"type\": \"string\", \"value\": \"[environment().name]\" }, " +
            "\"graph\": { \"type\": \"string\", \"value\": \"[environment().graph]\" }, " +
            "\"login\": { \"type\": \"string\", \"value\": \"[environment().authentication.loginEndpoint]\" }, " +
            "\"tid\": { \"type\": \"string\", \"value\": \"[tenant().tenantId]\" }, " +
            "\"domain\": { \"type\": \"string\", \"value\": \"[tenant().defaultDomain]\" }, " +
            "\"tenantName\": { \"type\": \"string\", \"value\": \"[tenant().displayName]\" } }");

        var result = await CliRunner.RunAsync(Deploy(sandbox, template, "create", "-y"));

        Assert.Equal(0, result.ExitCode);
        var outputs = result.Json["outputs"]!.AsObject();
        string Output(string name) => outputs[name]!["value"]!.GetValue<string>();
        Assert.Equal("AzureCloud", Output("cloud"));
        Assert.Equal("https://graph.microsoft.com", Output("graph"));
        Assert.Equal("https://login.microsoftonline.com/", Output("login"));
        Assert.Equal(CliSandbox.Tenant, Output("tid"));
        Assert.Equal("contoso.com", Output("domain"));
        Assert.Equal("Contoso", Output("tenantName"));
    }

    private static StateEntry UserEntry(string upn, string graphId)
    {
        return new StateEntry
        {
            Id = EArm.Core.ResourceIdentifier.Create("Microsoft.Entra/users", upn),
            Type = "Microsoft.Entra/users",
            ApiVersion = "2026-10-01-preview",
            Key = new Dictionary<string, string> { ["userPrincipalName"] = upn },
            GraphId = graphId,
            LastWriteOn = DateTimeOffset.UtcNow,
        };
    }

    [Fact]
    public async Task TheSignedInPrincipalFromTheToken_IsNeverDeletedByCompleteMode()
    {
        using var sandbox = new CliSandbox(nameof(TheSignedInPrincipalFromTheToken_IsNeverDeletedByCompleteMode));
        var directory = new FakeDirectory();
        sandbox.Handler.Directory = directory;
        const string me = "7e570000-0000-4000-8000-0000000000aa";
        sandbox.Tokens.ObjectId = me;
        directory.AddUser("me@contoso.com", "Me", u => u["id"] = me);
        var other = directory.AddUser("other@contoso.com", "Other");
        string template = sandbox.WriteEmptyTemplate();
        var state = StateFile.CreateNew("empty", CliSandbox.Tenant, DateTimeOffset.UtcNow);
        state.Upsert(UserEntry("me@contoso.com", me));
        state.Upsert(UserEntry("other@contoso.com", other["id"]!.GetValue<string>()));
        state.CompleteRun(true, "Incremental", "sha256:previous", DateTimeOffset.UtcNow);
        await sandbox.WriteStateAsync(state);

        var result = await CliRunner.RunAsync(Deploy(sandbox, template, "create", "-y", "--mode", "Complete", "--allow-delete-all", "--continue-on-error"));

        Assert.Equal(1, result.ExitCode);
        Assert.NotNull(directory.Find("/users", "userPrincipalName", "me@contoso.com"));
        Assert.Null(directory.Find("/users", "userPrincipalName", "other@contoso.com"));
        Assert.Contains("ProtectedObject", result.StandardOutput + result.StandardError, StringComparison.Ordinal);
        Assert.Equal("Failed", StatusOf(result, "me@contoso.com"));
        Assert.Equal("Succeeded", StatusOf(result, "other@contoso.com"));
        Assert.Contains("/providers/Microsoft.Entra/users/me@contoso.com", ReadState(sandbox, "empty")["resources"]!.AsObject().Select(p => p.Key));
        Assert.DoesNotContain("/providers/Microsoft.Entra/users/other@contoso.com", ReadState(sandbox, "empty")["resources"]!.AsObject().Select(p => p.Key));
    }

    [Fact]
    public async Task AnObjectThatIsNotTheSignedInPrincipal_IsDeletedByCompleteMode()
    {
        // The control of the test above: with another oid in the token the very same entry is deleted, so it is the principal id that protects it.
        using var sandbox = new CliSandbox(nameof(AnObjectThatIsNotTheSignedInPrincipal_IsDeletedByCompleteMode));
        var directory = new FakeDirectory();
        sandbox.Handler.Directory = directory;
        const string me = "7e570000-0000-4000-8000-0000000000aa";
        sandbox.Tokens.ObjectId = "7e570000-0000-4000-8000-0000000000bb";
        directory.AddUser("me@contoso.com", "Me", u => u["id"] = me);
        string template = sandbox.WriteEmptyTemplate();
        var state = StateFile.CreateNew("empty", CliSandbox.Tenant, DateTimeOffset.UtcNow);
        state.Upsert(UserEntry("me@contoso.com", me));
        state.CompleteRun(true, "Incremental", "sha256:previous", DateTimeOffset.UtcNow);
        await sandbox.WriteStateAsync(state);

        var result = await CliRunner.RunAsync(Deploy(sandbox, template, "create", "-y", "--mode", "Complete", "--allow-delete-all"));

        Assert.Equal(0, result.ExitCode);
        Assert.Null(directory.Find("/users", "userPrincipalName", "me@contoso.com"));
    }

    [Fact]
    public async Task AnApplicationToken_IsReportedToThePlanner_AsAppOnly()
    {
        const string userWithOwnerOf = "{ \"type\": \"Microsoft.Entra/users\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"owner@contoso.com\", " +
            "\"properties\": { \"displayName\": \"Owner\", \"mailNickname\": \"owner\", \"ownerOf\": [\"7e570000-0000-4000-8000-0000000000cc\"] } }";
        using var sandbox = new CliSandbox(nameof(AnApplicationToken_IsReportedToThePlanner_AsAppOnly));
        sandbox.Handler.Directory = new FakeDirectory();
        string template = sandbox.WriteTemplate("owner.entra.json", Resources(userWithOwnerOf));

        var delegated = await CliRunner.RunAsync(Deploy(sandbox, template, "what-if"));
        sandbox.Tokens.AppId = "0a9a0000-0000-4000-8000-0000000000dd";
        sandbox.Tokens.Scopes = [];
        sandbox.Tokens.Roles = ["Application.ReadWrite.All"];
        var appOnly = await CliRunner.RunAsync(Deploy(sandbox, template, "what-if"));

        Assert.DoesNotContain(delegated.Diagnostics, d => d!["code"]!.GetValue<string>() == "EdgeUnsupportedInAppMode");
        Assert.Contains(appOnly.Diagnostics, d => d!["code"]!.GetValue<string>() == "EdgeUnsupportedInAppMode");
    }
}
