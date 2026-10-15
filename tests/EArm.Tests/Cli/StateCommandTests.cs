using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Cli;
using EArm.Cli.Commands;
using EArm.Core;
using EArm.Core.Types;
using EArm.Engine.Execution;
using EArm.Engine.Providers;
using EArm.Engine.State;
using EArm.Providers;
using EArm.Tests.Engine;
using Xunit;

namespace EArm.Tests.Cli;

/// <summary><c>deployment list / show / operation list / delete</c> and <c>state unlock / forget</c> over sandboxed state files.</summary>
[Collection(CliCollection.Name)]
public sealed class StateCommandTests
{
    private const string AppId = "/providers/Microsoft.Entra/applications/orders-api";
    private const string FicId = AppId + "/federatedIdentityCredentials/github";
    private static readonly DateTimeOffset Start = new(2026, 10, 14, 10, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static StateFile Sample(string name)
    {
        var state = StateFile.CreateNew(name, CliSandbox.Tenant, Start);
        state.Upsert(new StateEntry
        {
            Id = ResourceIdentifier.Create(Fixture.AppType, "orders-api"),
            Type = Fixture.AppType,
            ApiVersion = "2026-10-01-preview",
            Key = new Dictionary<string, string> { ["uniqueName"] = "orders-api" },
            GraphId = "a1000000-0000-4000-8000-000000000001",
            LastWriteOn = Start,
            ContentHash = "sha256:a",
        });
        state.Upsert(new StateEntry
        {
            Id = ResourceIdentifier.Create(Fixture.FicType, "orders-api/github"),
            Type = Fixture.FicType,
            ApiVersion = "2026-10-01-preview",
            Key = new Dictionary<string, string> { ["name"] = "github" },
            GraphId = "f1000000-0000-4000-8000-000000000001",
            ParentGraphId = "a1000000-0000-4000-8000-000000000001",
            DependsOn = [AppId],
            LastWriteOn = Start,
        });
        state.Operations.Add(new OperationRecord { ResourceId = AppId, Type = Fixture.AppType, Status = OperationStatus.Succeeded, ChangeType = ChangeType.Create, Batch = 1, StartedOn = Start, FinishedOn = Start.AddSeconds(2), GraphId = "a1000000-0000-4000-8000-000000000001" });
        state.Operations.Add(new OperationRecord { ResourceId = FicId, Type = Fixture.FicType, Status = OperationStatus.Failed, ChangeType = ChangeType.Create, Batch = 2, StartedOn = Start, FinishedOn = Start, Error = "Graph 403 Authorization_RequestDenied: no", ErrorCode = "ResourceApplyFailed" });
        state.CompleteRun(false, "Incremental", "sha256:template", Start.AddMinutes(1));
        return state;
    }

    private static StateInvocation Invocation(CliSandbox sandbox, string? name, bool yes = false)
    {
        return new StateInvocation
        {
            Name = name,
            StateDir = sandbox.StateDir,
            Output = OutputFormat.Table,
            Connection = sandbox.Connection,
            Yes = yes,
        };
    }

    // ---- list ----

    [Fact]
    public async Task List_ShowsEveryState_AsTableAndJson()
    {
        using var sandbox = new CliSandbox(nameof(List_ShowsEveryState_AsTableAndJson));
        await sandbox.WriteStateAsync(Sample("b-dep"));
        await sandbox.WriteStateAsync(Sample("a-dep"));
        sandbox.WriteLock("b-dep", DateTimeOffset.UtcNow);
        sandbox.WriteRawState("broken", "{ \"schemaVersion\": 9 }");

        var table = await CliRunner.RunAsync("deployment", "list", "--state-dir", sandbox.StateDir);
        var json = await CliRunner.RunAsync("deployment", "list", "--state-dir", sandbox.StateDir, "-o", "json");

        Assert.Equal(0, table.ExitCode);
        var lines = table.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd()).ToList();
        Assert.Equal("State directory: " + sandbox.StateDir, lines[0]);
        Assert.StartsWith("Name", lines[1], StringComparison.Ordinal);
        Assert.Contains("Status", lines[1]);
        Assert.Contains("Lock", lines[1]);
        Assert.StartsWith("a-dep", lines[3], StringComparison.Ordinal);
        Assert.Contains("Failed", lines[3]);
        Assert.Contains(CliSandbox.Tenant, lines[3]);
        Assert.StartsWith("b-dep", lines[4], StringComparison.Ordinal);
        Assert.EndsWith("locked", lines[4], StringComparison.Ordinal);
        Assert.StartsWith("broken", lines[5], StringComparison.Ordinal);
        Assert.Contains("unreadable", lines[5]);
        Assert.Contains(lines, l => l.Contains("broken: ", StringComparison.Ordinal) && l.Contains("schemaVersion 9", StringComparison.Ordinal));

        Assert.Equal(0, json.ExitCode);
        Assert.Equal(sandbox.StateDir, json.Json["stateDirectory"]!.GetValue<string>());
        var deployments = json.Json["deployments"]!.AsArray();
        Assert.Equal(["a-dep", "b-dep", "broken"], deployments.Select(d => d!["name"]!.GetValue<string>()).ToList());
        Assert.False(deployments[0]!["locked"]!.GetValue<bool>());
        Assert.True(deployments[1]!["locked"]!.GetValue<bool>());
        Assert.Contains("424242", deployments[1]!["lockHolder"]!.GetValue<string>());
        Assert.Equal(2, deployments[0]!["resourceCount"]!.GetValue<int>());
        Assert.Equal("Failed", deployments[0]!["lastStatus"]!.GetValue<string>());
        Assert.Equal("Incremental", deployments[0]!["lastMode"]!.GetValue<string>());
        Assert.Null(deployments[0]!["error"]);
        Assert.Contains("schemaVersion 9", deployments[2]!["error"]!.GetValue<string>());
        Assert.Null(deployments[2]!["resourceCount"]);
    }

    [Fact]
    public async Task List_EmptyOrMissingDirectory_ReportsNoDeployments()
    {
        using var sandbox = new CliSandbox(nameof(List_EmptyOrMissingDirectory_ReportsNoDeployments));

        var table = await CliRunner.RunAsync("deployment", "list", "--state-dir", sandbox.StateDir);
        var json = await CliRunner.RunAsync("deployment", "list", "--state-dir", sandbox.StateDir, "-o", "json");

        Assert.Equal(0, table.ExitCode);
        Assert.Contains("No deployments.", table.StandardOutput);
        Assert.Empty(json.Json["deployments"]!.AsArray());
        Assert.False(Directory.Exists(sandbox.StateDir), "listing does not create the directory");
    }

    // ---- show ----

    [Fact]
    public async Task Show_RendersTheStateSummaryAndResources_AsTableAndJson()
    {
        using var sandbox = new CliSandbox(nameof(Show_RendersTheStateSummaryAndResources_AsTableAndJson));
        await sandbox.WriteStateAsync(Sample("a-dep"));

        var table = await CliRunner.RunAsync("deployment", "show", "-n", "a-dep", "--state-dir", sandbox.StateDir);
        var json = await CliRunner.RunAsync("deployment", "show", "-n", "a-dep", "--state-dir", sandbox.StateDir, "-o", "json");

        Assert.Equal(0, table.ExitCode);
        Assert.Contains("Deployment:     a-dep", table.StandardOutput);
        Assert.Contains("State file:     " + sandbox.StatePath("a-dep"), table.StandardOutput);
        Assert.Contains("Tenant:         " + CliSandbox.Tenant, table.StandardOutput);
        Assert.Contains("Last status:    Failed", table.StandardOutput);
        Assert.Contains("Last mode:      Incremental", table.StandardOutput);
        Assert.Contains("Template hash:  sha256:template", table.StandardOutput);
        Assert.Contains("Lock:           none", table.StandardOutput);
        Assert.Contains("Resources:      2", table.StandardOutput);
        Assert.Contains("Resource", table.StandardOutput);
        Assert.Contains("Graph id", table.StandardOutput);
        var rows = table.StandardOutput.Split('\n').Select(l => l.TrimEnd()).ToList();
        Assert.Contains(rows, l => l.StartsWith(AppId, StringComparison.Ordinal) && l.Contains("  a1000000-0000-4000-8000-000000000001", StringComparison.Ordinal));
        Assert.Contains(rows, l => l.StartsWith(FicId, StringComparison.Ordinal) && l.Contains("  f1000000-0000-4000-8000-000000000001", StringComparison.Ordinal));
        Assert.Equal(string.Empty, table.StandardError);

        Assert.Equal(0, json.ExitCode);
        Assert.Equal("a-dep", json.Json["deploymentName"]!.GetValue<string>());
        Assert.Equal(sandbox.StatePath("a-dep"), json.Json["stateFile"]!.GetValue<string>());
        Assert.Null(json.Json["lock"]);
        Assert.Null(json.Json["drift"]);
        Assert.Equal("a1000000-0000-4000-8000-000000000001", json.Json["resources"]![AppId]!["id"]!.GetValue<string>());
        Assert.Equal("a1000000-0000-4000-8000-000000000001", json.Json["resources"]![FicId]!["parentId"]!.GetValue<string>());
        Assert.Equal(2, json.Json["operations"]!.AsArray().Count);
    }

    [Fact]
    public async Task Show_WithALock_ShowsTheHolder()
    {
        using var sandbox = new CliSandbox(nameof(Show_WithALock_ShowsTheHolder));
        await sandbox.WriteStateAsync(Sample("a-dep"));
        sandbox.WriteLock("a-dep", Start);

        var table = await CliRunner.RunAsync("deployment", "show", "-n", "a-dep", "--state-dir", sandbox.StateDir);
        var json = await CliRunner.RunAsync("deployment", "show", "-n", "a-dep", "--state-dir", sandbox.StateDir, "-o", "json");

        Assert.Contains("Lock:           process 424242 on other-host (someone) since 2026-10-14 10:00:00Z", table.StandardOutput);
        Assert.Equal(424242, json.Json["lock"]!["processId"]!.GetValue<int>());
        Assert.Equal("other-host", json.Json["lock"]!["host"]!.GetValue<string>());
    }

    [Fact]
    public async Task Show_MissingDeploymentOrName_ExitsOne()
    {
        using var sandbox = new CliSandbox(nameof(Show_MissingDeploymentOrName_ExitsOne));

        var missing = await CliRunner.RunAsync("deployment", "show", "-n", "nope", "--state-dir", sandbox.StateDir);
        var noName = await CliRunner.RunAsync("deployment", "show", "--state-dir", sandbox.StateDir);
        var badName = await CliRunner.RunAsync("deployment", "show", "-n", "bad name", "--state-dir", sandbox.StateDir);

        Assert.Equal(1, missing.ExitCode);
        Assert.Contains("No deployment named 'nope' in " + sandbox.StateDir, missing.StandardError);
        Assert.Equal(1, noName.ExitCode);
        Assert.Contains("--name", noName.StandardError);
        Assert.Equal(1, badName.ExitCode);
        Assert.Contains("is not valid", badName.StandardError);
    }

    [Fact]
    public async Task Show_UnreadableState_ExitsOne()
    {
        using var sandbox = new CliSandbox(nameof(Show_UnreadableState_ExitsOne));
        sandbox.WriteRawState("a-dep", "{ \"schemaVersion\": 2 }");

        var result = await CliRunner.RunAsync("deployment", "show", "-n", "a-dep", "--state-dir", sandbox.StateDir);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("schemaVersion 2", result.StandardError);
    }

    // ---- operation list ----

    [Fact]
    public async Task OperationList_RendersTheLastRun_AsTableAndJson()
    {
        using var sandbox = new CliSandbox(nameof(OperationList_RendersTheLastRun_AsTableAndJson));
        await sandbox.WriteStateAsync(Sample("a-dep"));

        var table = await CliRunner.RunAsync("deployment", "operation", "list", "-n", "a-dep", "--state-dir", sandbox.StateDir);
        var json = await CliRunner.RunAsync("deployment", "operation", "list", "-n", "a-dep", "--state-dir", sandbox.StateDir, "-o", "json");
        var missing = await CliRunner.RunAsync("deployment", "operation", "list", "-n", "nope", "--state-dir", sandbox.StateDir);

        Assert.Equal(0, table.ExitCode);
        Assert.Contains("Deployment 'a-dep': last run Failed (Incremental) on 2026-10-14 10:01:00Z; 2 operation(s).", table.StandardOutput);
        Assert.Contains("+  " + AppId, table.StandardOutput);
        Assert.Contains("!  " + FicId, table.StandardOutput);
        Assert.Contains("Succeeded", table.StandardOutput);
        Assert.Contains("2000 ms", table.StandardOutput);
        Assert.Contains("Authorization_RequestDenied", table.StandardOutput);

        Assert.Equal(0, json.ExitCode);
        Assert.Equal("a-dep", json.Json["deploymentName"]!.GetValue<string>());
        Assert.Equal("Failed", json.Json["lastStatus"]!.GetValue<string>());
        var operations = json.Json["operations"]!.AsArray();
        Assert.Equal(2, operations.Count);
        Assert.Equal("Succeeded", operations[0]!["status"]!.GetValue<string>());
        Assert.Equal("Create", operations[0]!["changeType"]!.GetValue<string>());
        Assert.Equal(1, operations[0]!["batch"]!.GetValue<int>());
        Assert.Equal(2000, operations[0]!["durationMs"]!.GetValue<long>());
        Assert.Equal("a1000000-0000-4000-8000-000000000001", operations[0]!["graphId"]!.GetValue<string>());
        Assert.Equal("ResourceApplyFailed", operations[1]!["errorCode"]!.GetValue<string>());

        Assert.Equal(1, missing.ExitCode);
    }

    // ---- state unlock ----

    [Fact]
    public async Task StateUnlock_BreaksTheLock_OrReportsNone()
    {
        using var sandbox = new CliSandbox(nameof(StateUnlock_BreaksTheLock_OrReportsNone));
        sandbox.WriteLock("a-dep", Start);

        var unlocked = await CliRunner.RunAsync("state", "unlock", "-n", "a-dep", "--state-dir", sandbox.StateDir);
        var none = await CliRunner.RunAsync("state", "unlock", "-n", "a-dep", "--state-dir", sandbox.StateDir);

        Assert.Equal(0, unlocked.ExitCode);
        Assert.Contains("Unlocked deployment 'a-dep'; the lock was held by process 424242 on other-host (someone)", unlocked.StandardOutput);
        Assert.False(File.Exists(sandbox.LockPath("a-dep")));
        Assert.Equal(0, none.ExitCode);
        Assert.Contains("Deployment 'a-dep' was not locked (" + sandbox.LockPath("a-dep") + ")", none.StandardOutput);
    }

    // ---- state forget ----

    [Fact]
    public async Task StateForget_RemovesOneEntry_AndRefusesUnknownOrLockedOnes()
    {
        using var sandbox = new CliSandbox(nameof(StateForget_RemovesOneEntry_AndRefusesUnknownOrLockedOnes));
        await sandbox.WriteStateAsync(Sample("a-dep"));

        var unknown = await CliRunner.RunAsync("state", "forget", "-n", "a-dep", "--resource-id", "/providers/Microsoft.Entra/groups/nope", "-y", "--state-dir", sandbox.StateDir);
        var forgotten = await CliRunner.RunAsync("state", "forget", "-n", "a-dep", "--resource-id", FicId, "-y", "--state-dir", sandbox.StateDir);
        sandbox.WriteLock("a-dep", DateTimeOffset.UtcNow);
        var locked = await CliRunner.RunAsync("state", "forget", "-n", "a-dep", "--resource-id", AppId, "-y", "--state-dir", sandbox.StateDir);
        var missing = await CliRunner.RunAsync("state", "forget", "-n", "nope", "--resource-id", AppId, "-y", "--state-dir", sandbox.StateDir);

        Assert.Equal(1, unknown.ExitCode);
        Assert.Contains("has no state entry '/providers/Microsoft.Entra/groups/nope'", unknown.StandardError);
        Assert.Equal(0, forgotten.ExitCode);
        Assert.Contains("Forgot '" + FicId + "' from deployment 'a-dep'; 1 resource(s) remain in the state.", forgotten.StandardOutput);
        var loaded = await sandbox.Store.LoadAsync("a-dep", Ct);
        Assert.Equal([AppId], loaded!.Resources.Keys.ToList());
        Assert.Equal(4, locked.ExitCode);
        Assert.Contains("is locked by", locked.StandardError);
        Assert.Equal(1, missing.ExitCode);
        Assert.Single((await sandbox.Store.LoadAsync("a-dep", Ct))!.Resources);
    }

    [Fact]
    public async Task StateForget_DeclinedAtThePrompt_ChangesNothing()
    {
        using var sandbox = new CliSandbox(nameof(StateForget_DeclinedAtThePrompt_ChangesNothing));
        await sandbox.WriteStateAsync(Sample("a-dep"));
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        int exitCode = await StateCommand.RunForgetAsync(Invocation(sandbox, "a-dep"), AppId, stdout, stderr, () => "n", Ct);

        Assert.Equal(1, exitCode);
        Assert.Contains("Nothing forgotten.", stderr.ToString());
        Assert.Contains("Forget '" + AppId + "' (Graph id a1000000-0000-4000-8000-000000000001)?", stderr.ToString());
        Assert.Equal(2, (await sandbox.Store.LoadAsync("a-dep", Ct))!.Resources.Count);
        Assert.False(File.Exists(sandbox.LockPath("a-dep")));
    }
}
