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
        return ["deployment", verb, "-f", template, "-o", "json", .. extra];
    }

    private static JsonObject Operation(CliResult result, string resourceName)
    {
        return result.Json["operations"]!.AsArray().Select(o => o!.AsObject()).Single(o => o["resourceId"]!.GetValue<string>().EndsWith("/" + resourceName, StringComparison.Ordinal));
    }

    private static string StatusOf(CliResult result, string resourceName)
    {
        return Operation(result, resourceName)["status"]!.GetValue<string>();
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
        Assert.Equal(4, result.Json["operations"]!.AsArray().Count);
        Assert.Empty(sandbox.UnexpectedFiles());
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
    }

    [Fact]
    public async Task ARunWithNoFailureAndNoSkip_IsSucceeded()
    {
        using var sandbox = new CliSandbox(nameof(ARunWithNoFailureAndNoSkip_IsSucceeded));
        sandbox.Handler.Directory = new FakeDirectory();
        string template = sandbox.WriteTemplate("ok.entra.json", Resources(Group("grp-a"), Group("grp-b", after: "grp-a")));

        var result = await CliRunner.RunAsync(Deploy(sandbox, template, "create", "-y", "-n", "fine"));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("Succeeded", result.Json["status"]!.GetValue<string>());
        Assert.Equal((2, 0, 0), (result.Json["succeeded"]!.GetValue<int>(), result.Json["failed"]!.GetValue<int>(), result.Json["skipped"]!.GetValue<int>()));
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
        Assert.Empty(sandbox.UnexpectedFiles());
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

    // ---- -n / --name: only a label ----

    [Theory]
    [InlineData("-n")]
    [InlineData("--name")]
    public async Task Name_IsTheReportedDeploymentName_AndNothingIsWritten(string flag)
    {
        using var sandbox = new CliSandbox(nameof(Name_IsTheReportedDeploymentName_AndNothingIsWritten) + flag.Length);
        sandbox.UsePreflightOk();
        string template = sandbox.WriteEmptyTemplate("stem.entra.json");

        var named = await CliRunner.RunAsync(Deploy(sandbox, template, "create", "-y", flag, "chosen"));
        sandbox.UsePreflightOk();
        var unnamed = await CliRunner.RunAsync(Deploy(sandbox, template, "create", "-y"));
        sandbox.UsePreflightOk();
        var whatIf = await CliRunner.RunAsync(Deploy(sandbox, template, "what-if", flag, "other"));

        Assert.Equal(0, named.ExitCode);
        Assert.Equal("chosen", named.Json["deploymentName"]!.GetValue<string>());
        Assert.Equal(0, unnamed.ExitCode);
        Assert.Equal("stem", unnamed.Json["deploymentName"]!.GetValue<string>());
        Assert.Equal("other", whatIf.Json["deploymentName"]!.GetValue<string>());
        Assert.Empty(sandbox.UnexpectedFiles());
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

    private static string UsersTemplate(CliSandbox sandbox)
    {
        return sandbox.WriteTemplate("users.entra.json", "\"resources\": [ "
            + "{ \"type\": \"Microsoft.Entra/users\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"me@contoso.com\", \"properties\": { \"displayName\": \"Me\" } }, "
            + "{ \"type\": \"Microsoft.Entra/users\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"other@contoso.com\", \"properties\": { \"displayName\": \"Other\" } } ]");
    }

    private static string OutcomeOf(CliResult result, string upn)
    {
        return result.Json["outcomes"]!.AsArray().Single(o => o!["resourceId"]!.GetValue<string>().EndsWith("/" + upn, StringComparison.Ordinal))!["status"]!.GetValue<string>();
    }

    [Fact]
    public async Task TheSignedInPrincipalFromTheToken_IsNeverDeletedByDeploymentDelete()
    {
        using var sandbox = new CliSandbox(nameof(TheSignedInPrincipalFromTheToken_IsNeverDeletedByDeploymentDelete));
        var directory = new FakeDirectory();
        sandbox.Handler.Directory = directory;
        const string me = "7e570000-0000-4000-8000-0000000000aa";
        sandbox.Tokens.ObjectId = me;
        directory.AddUser("me@contoso.com", "Me", u => u["id"] = me);
        directory.AddUser("other@contoso.com", "Other");
        string template = UsersTemplate(sandbox);

        var result = await CliRunner.RunAsync(Deploy(sandbox, template, "delete", "-y"));

        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(directory.Find("/users", "userPrincipalName", "me@contoso.com"));
        Assert.Null(directory.Find("/users", "userPrincipalName", "other@contoso.com"));
        Assert.Equal("Refused", OutcomeOf(result, "me@contoso.com"));
        Assert.Equal("Deleted", OutcomeOf(result, "other@contoso.com"));
        Assert.Equal(1, result.Json["kept"]!.GetValue<int>());
        Assert.Contains("signed-in principal", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnObjectThatIsNotTheSignedInPrincipal_IsDeletedByDeploymentDelete()
    {
        // The control of the test above: with another oid in the token the very same user is deleted, so it is the principal id that protects it.
        using var sandbox = new CliSandbox(nameof(AnObjectThatIsNotTheSignedInPrincipal_IsDeletedByDeploymentDelete));
        var directory = new FakeDirectory();
        sandbox.Handler.Directory = directory;
        const string me = "7e570000-0000-4000-8000-0000000000aa";
        sandbox.Tokens.ObjectId = "7e570000-0000-4000-8000-0000000000bb";
        directory.AddUser("me@contoso.com", "Me", u => u["id"] = me);
        string template = UsersTemplate(sandbox);

        var result = await CliRunner.RunAsync(Deploy(sandbox, template, "delete", "-y"));

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
