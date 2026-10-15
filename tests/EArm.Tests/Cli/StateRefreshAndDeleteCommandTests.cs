using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;
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
/// <c>--force-unlock</c>, the confirmation prompt) over the real providers and a fake Microsoft Graph.
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
        return "{ \"match\": { \"method\": \"GET\", \"urlContains\": \"" + urlContains + "\" }, \"status\": 200, \"body\": { \"id\": \"" + id + "\", \"uniqueName\": \"" + uniqueName + "\", \"displayName\": \"" + uniqueName + "\" } }";
    }

    private static string NotFound(string method, string urlContains)
    {
        return "{ \"match\": { \"method\": \"" + method + "\", \"urlContains\": \"" + urlContains + "\" }, \"status\": 404, \"body\": { \"error\": { \"code\": \"Request_ResourceNotFound\", \"message\": \"Resource does not exist.\" } } }";
    }

    private static string Forbidden(string method, string urlContains)
    {
        return "{ \"match\": { \"method\": \"" + method + "\", \"urlContains\": \"" + urlContains + "\" }, \"status\": 403, \"body\": { \"error\": { \"code\": \"Authorization_RequestDenied\", \"message\": \"Insufficient privileges.\" } } }";
    }

    private static string DeleteOk(string urlContains)
    {
        return "{ \"match\": { \"method\": \"DELETE\", \"urlContains\": \"" + urlContains + "\" }, \"status\": 204 }";
    }

    // ---- show --refresh ----

    [Fact]
    public async Task Show_Refresh_ReportsInSyncMissingReplacedAndUnknown()
    {
        using var sandbox = new CliSandbox(nameof(Show_Refresh_ReportsInSyncMissingReplacedAndUnknown));
        await sandbox.WriteStateAsync(State("drift", App("alpha", "a1000000-0000-4000-8000-000000000001"), App("bravo", "b1000000-0000-4000-8000-000000000001"), App("charlie", "c1000000-0000-4000-8000-000000000001"), App("delta", "d1000000-0000-4000-8000-000000000001")));
        sandbox.UsePreflightOk(extraResponses: Responses(
            App200("uniqueName='alpha'", "a1000000-0000-4000-8000-000000000001", "alpha"),
            NotFound("GET", "uniqueName='bravo'"),
            App200("uniqueName='charlie'", "c2000000-0000-4000-8000-000000000002", "charlie"),
            Forbidden("GET", "uniqueName='delta'")));

        var table = await CliRunner.RunAsync([.. sandbox.StateArgs, "deployment", "show", "-n", "drift", "--refresh"]);

        Assert.Equal(0, table.ExitCode);
        Assert.Contains("Live", table.StandardOutput, StringComparison.Ordinal);
        var rows = table.StandardOutput.Split('\n').Select(l => l.TrimEnd()).ToList();
        Assert.Contains(rows, l => l.StartsWith(AppA, StringComparison.Ordinal) && l.Contains("InSync", StringComparison.Ordinal));
        Assert.Contains(rows, l => l.StartsWith(AppB, StringComparison.Ordinal) && l.Contains("Missing (not found in the tenant)", StringComparison.Ordinal));
        Assert.Contains(rows, l => l.StartsWith(AppC, StringComparison.Ordinal) && l.Contains("Replaced (the key now belongs to c2000000-0000-4000-8000-000000000002)", StringComparison.Ordinal));
        Assert.Contains(rows, l => l.StartsWith(AppD, StringComparison.Ordinal) && l.Contains("Unknown", StringComparison.Ordinal));
        var stateAfter = await sandbox.Store.LoadAsync("drift", Ct);
        Assert.Equal("a1000000-0000-4000-8000-000000000001", stateAfter!.GraphIdOf(AppA));
        Assert.Equal("c1000000-0000-4000-8000-000000000001", stateAfter.GraphIdOf(AppC));
    }

    [Fact]
    public async Task Show_Refresh_NotSignedIn_AndTenantMismatch_ExitThree()
    {
        using var sandbox = new CliSandbox(nameof(Show_Refresh_NotSignedIn_AndTenantMismatch_ExitThree));
        await sandbox.WriteStateAsync(State("drift", App("alpha", "a1000000-0000-4000-8000-000000000001")));

        sandbox.UseNotLoggedIn();
        var notSignedIn = await CliRunner.RunAsync([.. sandbox.StateArgs, "deployment", "show", "-n", "drift", "--refresh"]);

        sandbox.UsePreflightOk(CliSandbox.OtherTenant);
        var mismatch = await CliRunner.RunAsync([.. sandbox.StateArgs, "deployment", "show", "-n", "drift", "--refresh"]);

        Assert.Equal(3, notSignedIn.ExitCode);
        Assert.Contains("earm auth login", notSignedIn.StandardError, StringComparison.Ordinal);
        Assert.Equal(3, mismatch.ExitCode);
        Assert.Contains("was deployed to tenant " + CliSandbox.Tenant, mismatch.StandardError, StringComparison.Ordinal);
        Assert.Equal(string.Empty, mismatch.StandardOutput);
    }

    [Fact]
    public async Task Show_OutputNone_PrintsNothing_AndExitsZero()
    {
        using var sandbox = new CliSandbox(nameof(Show_OutputNone_PrintsNothing_AndExitsZero));
        await sandbox.WriteStateAsync(State("quiet", App("alpha", "a1000000-0000-4000-8000-000000000001")));

        var result = await CliRunner.RunAsync("deployment", "show", "-n", "quiet", "--state-dir", sandbox.StateDir, "-o", "none");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
    }
}
