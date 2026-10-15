using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EArm.Az;
using EArm.Cli;
using EArm.Cli.Commands;
using EArm.Core;
using EArm.Engine.Execution;
using EArm.Engine.State;
using EArm.Tests.Engine;
using Xunit;

namespace EArm.Tests.Cli;

/// <summary>
/// <c>deployment show --refresh</c> (drift report: InSync, Missing, Replaced, Unknown) and
/// <c>deployment delete --delete-resources</c> (per-object outcomes, partial failure, <c>--continue-on-error</c>,
/// <c>--force-unlock</c>, the confirmation prompt) over the real providers and the fake az.
/// </summary>
[Collection(CliCollection.Name)]
public sealed class StateRefreshAndDeleteCommandTests
{
    private const string AppA = "/providers/Microsoft.Entra/applications/alpha";
    private const string AppB = "/providers/Microsoft.Entra/applications/bravo";
    private const string AppC = "/providers/Microsoft.Entra/applications/charlie";
    private const string AppD = "/providers/Microsoft.Entra/applications/delta";
    private static readonly DateTimeOffset Start = new(2026, 10, 14, 10, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static StateEntry App(string uniqueName, string graphId)
    {
        return new StateEntry
        {
            Id = ResourceIdentifier.Create(Fixture.AppType, uniqueName),
            Type = Fixture.AppType,
            ApiVersion = "2026-10-01-preview",
            Key = new Dictionary<string, string> { ["uniqueName"] = uniqueName },
            GraphId = graphId,
            LastWriteOn = Start,
            ContentHash = "sha256:a",
        };
    }

    private static StateFile State(string name, params StateEntry[] entries)
    {
        var state = StateFile.CreateNew(name, CliSandbox.Tenant, Start);
        foreach (var entry in entries)
        {
            state.Upsert(entry);
        }

        state.CompleteRun(true, "Incremental", "sha256:t", Start.AddMinutes(1));
        return state;
    }

    private static string Responses(string preflight, params string[] more)
    {
        return preflight + (more.Length == 0 ? string.Empty : "," + string.Join(",", more));
    }

    private static string App200(string urlContains, string id, string uniqueName)
    {
        return "{ \"match\": { \"method\": \"GET\", \"urlContains\": \"" + urlContains + "\" }, \"exitCode\": 0, \"stdoutJson\": { \"id\": \"" + id + "\", \"uniqueName\": \"" + uniqueName + "\", \"displayName\": \"" + uniqueName + "\" } }";
    }

    private static string NotFound(string method, string urlContains)
    {
        return "{ \"match\": { \"method\": \"" + method + "\", \"urlContains\": \"" + urlContains + "\" }, \"exitCode\": 1, \"stderr\": \"ERROR: Not Found({\\\"error\\\":{\\\"code\\\":\\\"Request_ResourceNotFound\\\",\\\"message\\\":\\\"Resource does not exist.\\\"}})\" }";
    }

    private static string Forbidden(string method, string urlContains)
    {
        return "{ \"match\": { \"method\": \"" + method + "\", \"urlContains\": \"" + urlContains + "\" }, \"exitCode\": 1, \"stderr\": \"ERROR: Forbidden({\\\"error\\\":{\\\"code\\\":\\\"Authorization_RequestDenied\\\",\\\"message\\\":\\\"Insufficient privileges.\\\"}})\" }";
    }

    private static string DeleteOk(string urlContains)
    {
        return "{ \"match\": { \"method\": \"DELETE\", \"urlContains\": \"" + urlContains + "\" }, \"exitCode\": 0 }";
    }

    // ---- show --refresh ----

    [Fact]
    public async Task Show_Refresh_ReportsInSyncMissingReplacedAndUnknown()
    {
        using var sandbox = new CliSandbox(nameof(Show_Refresh_ReportsInSyncMissingReplacedAndUnknown));
        await sandbox.WriteStateAsync(State("drift", App("alpha", "A1"), App("bravo", "B1"), App("charlie", "C1"), App("delta", "D1")));
        sandbox.UsePreflightOk(extraResponses: Responses(
            App200("/applications/A1", "A1", "alpha"),
            NotFound("GET", "/applications/B1"),
            NotFound("GET", "uniqueName='bravo'"),
            NotFound("GET", "/applications/C1"),
            App200("uniqueName='charlie'", "C2", "charlie"),
            Forbidden("GET", "/applications/D1")));

        var table = await CliRunner.RunAsync([.. sandbox.AzArgs, "deployment", "show", "-n", "drift", "--refresh"]);

        Assert.Equal(0, table.ExitCode);
        Assert.Contains("Live", table.StandardOutput, StringComparison.Ordinal);
        var rows = table.StandardOutput.Split('\n').Select(l => l.TrimEnd()).ToList();
        Assert.Contains(rows, l => l.StartsWith(AppA, StringComparison.Ordinal) && l.Contains("InSync", StringComparison.Ordinal));
        Assert.Contains(rows, l => l.StartsWith(AppB, StringComparison.Ordinal) && l.Contains("Missing (not found in the tenant)", StringComparison.Ordinal));
        Assert.Contains(rows, l => l.StartsWith(AppC, StringComparison.Ordinal) && l.Contains("Replaced (the key now belongs to C2)", StringComparison.Ordinal));
        Assert.Contains(rows, l => l.StartsWith(AppD, StringComparison.Ordinal) && l.Contains("Unknown", StringComparison.Ordinal));
        var stateAfter = await sandbox.Store.LoadAsync("drift", Ct);
        Assert.Equal("A1", stateAfter!.GraphIdOf(AppA));
        Assert.Equal("C1", stateAfter.GraphIdOf(AppC));
    }

    [Fact]
    public async Task Show_Refresh_NotLoggedIn_AndTenantMismatch_ExitThree()
    {
        using var sandbox = new CliSandbox(nameof(Show_Refresh_NotLoggedIn_AndTenantMismatch_ExitThree));
        await sandbox.WriteStateAsync(State("drift", App("alpha", "A1")));

        sandbox.UseNotLoggedIn();
        var notLoggedIn = await CliRunner.RunAsync([.. sandbox.AzArgs, "deployment", "show", "-n", "drift", "--refresh"]);

        sandbox.UsePreflightOk(CliSandbox.OtherTenant);
        var mismatch = await CliRunner.RunAsync([.. sandbox.AzArgs, "deployment", "show", "-n", "drift", "--refresh"]);

        Assert.Equal(3, notLoggedIn.ExitCode);
        Assert.Contains("az login --allow-no-subscriptions", notLoggedIn.StandardError, StringComparison.Ordinal);
        Assert.Equal(3, mismatch.ExitCode);
        Assert.Contains("was deployed to tenant " + CliSandbox.Tenant, mismatch.StandardError, StringComparison.Ordinal);
        Assert.Equal(string.Empty, mismatch.StandardOutput);
    }

    [Fact]
    public async Task Show_OutputNone_PrintsNothing_AndExitsZero()
    {
        using var sandbox = new CliSandbox(nameof(Show_OutputNone_PrintsNothing_AndExitsZero));
        await sandbox.WriteStateAsync(State("quiet", App("alpha", "A1")));

        var result = await CliRunner.RunAsync("deployment", "show", "-n", "quiet", "--state-dir", sandbox.StateDir, "-o", "none");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
    }

    // ---- delete --delete-resources ----

    [Fact]
    public async Task Delete_WithDeleteResources_DeletesEveryObject_ReportsEach_AndRemovesTheState()
    {
        using var sandbox = new CliSandbox(nameof(Delete_WithDeleteResources_DeletesEveryObject_ReportsEach_AndRemovesTheState));
        await sandbox.WriteStateAsync(State("gone", App("alpha", "A1"), App("bravo", "B1")));
        sandbox.UsePreflightOk(extraResponses: Responses(DeleteOk("/applications/A1"), NotFound("DELETE", "/applications/B1")));

        var result = await CliRunner.RunAsync([.. sandbox.AzArgs, "deployment", "delete", "-n", "gone", "-y", "--delete-resources"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(2, result.StandardOutput.Split('\n').Count(l => l.TrimEnd().EndsWith("(deleted)", StringComparison.Ordinal)));
        Assert.DoesNotContain("(already gone)", result.StandardOutput, StringComparison.Ordinal); // the applications provider treats a 404 on delete as deleted itself
        Assert.Contains("Deleted 2 object(s) (0 already gone) and removed the state of deployment 'gone'.", result.StandardOutput, StringComparison.Ordinal);
        Assert.False(File.Exists(sandbox.StatePath("gone")));
        Assert.False(File.Exists(sandbox.LockPath("gone")));
    }

    [Fact]
    public async Task Delete_WithDeleteResources_StopsAtTheFirstFailure_AndKeepsTheRest()
    {
        using var sandbox = new CliSandbox(nameof(Delete_WithDeleteResources_StopsAtTheFirstFailure_AndKeepsTheRest));
        await sandbox.WriteStateAsync(State("partial", App("alpha", "A1"), App("bravo", "B1"), App("charlie", "C1")));
        sandbox.UsePreflightOk(extraResponses: Responses(Forbidden("DELETE", "/applications/")));

        var stopped = await CliRunner.RunAsync([.. sandbox.AzArgs, "deployment", "delete", "-n", "partial", "-y", "--delete-resources"]);

        Assert.Equal(1, stopped.ExitCode);
        Assert.Contains("1 delete(s) failed, 2 skipped; 3 entry(ies) remain in the state.", stopped.StandardError, StringComparison.Ordinal);
        Assert.Equal(2, (stopped.StandardOutput + stopped.StandardError).Split('\n').Count(l => l.Contains("(skipped: an earlier delete failed)", StringComparison.Ordinal)));
        var kept = await sandbox.Store.LoadAsync("partial", Ct);
        Assert.Equal(3, kept!.Resources.Count);
        Assert.Equal(StateFile.StatusFailed, kept.LastStatus);
    }

    [Fact]
    public async Task Delete_WithDeleteResources_PromptsUnlessYes_AndDecliningDeletesNothing()
    {
        using var sandbox = new CliSandbox(nameof(Delete_WithDeleteResources_PromptsUnlessYes_AndDecliningDeletesNothing));
        await sandbox.WriteStateAsync(State("prompted", App("alpha", "A1")));
        sandbox.UsePreflightOk();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var invocation = new StateInvocation
        {
            Name = "prompted",
            StateDir = sandbox.StateDir,
            Output = OutputFormat.Table,
            Az = new AzOptions { AzCommand = CliSandbox.FakeAzPath, TempDirectory = sandbox.TempDir },
            Yes = false,
        };

        int code = await DeleteCommand.RunAsync(invocation, deleteResources: true, forceUnlock: false, continueOnError: false, stdout, stderr, () => "n", Ct);

        Assert.Equal(1, code);
        Assert.Contains("Delete the 1 object(s) managed by deployment 'prompted' from tenant " + CliSandbox.Tenant + " and remove its state?", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("Nothing deleted.", stderr.ToString(), StringComparison.Ordinal);
        Assert.True(File.Exists(sandbox.StatePath("prompted")));
        Assert.DoesNotContain(sandbox.RecordedCommands(), c => c.Contains("--method DELETE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Delete_ForceUnlock_BreaksAForeignLock_AndProceeds()
    {
        using var sandbox = new CliSandbox(nameof(Delete_ForceUnlock_BreaksAForeignLock_AndProceeds));
        await sandbox.WriteStateAsync(State("locked", App("alpha", "A1")));
        sandbox.WriteLock("locked", DateTimeOffset.UtcNow);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var invocation = new StateInvocation { Name = "locked", StateDir = sandbox.StateDir, Output = OutputFormat.Table, Az = new AzOptions(), Yes = true };

        int refused = await DeleteCommand.RunAsync(invocation, deleteResources: false, forceUnlock: false, continueOnError: false, stdout, stderr, () => "y", Ct);
        int forced = await DeleteCommand.RunAsync(invocation, deleteResources: false, forceUnlock: true, continueOnError: false, stdout, stderr, () => "y", Ct);

        Assert.Equal(4, refused);
        Assert.Equal(0, forced);
        Assert.Contains("Broke the state lock of deployment 'locked' held by process 424242 on other-host", stderr.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(sandbox.StatePath("locked")));
        Assert.False(File.Exists(sandbox.LockPath("locked")));
    }
}
