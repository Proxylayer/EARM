using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Cli;
using EArm.Cli.Output;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Engine.Execution;
using EArm.Engine.Providers;
using EArm.Engine.State;
using EArm.Tests.Engine;
using Xunit;

namespace EArm.Tests.Cli;

/// <summary>
/// The CLI renderers in-process (no pwsh, no command line): <see cref="StateWriter"/> (show / list / operations ×
/// table / json / none, drift column, empty states, unreadable entries) and <see cref="DeploymentResultWriter"/>
/// (every operation status, diagnostics routed to stdout / stderr, outputs, secure-outputs hint, mode-pass counts).
/// </summary>
public sealed class OutputWriterTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 14, 10, 0, 0, TimeSpan.Zero);

    private static StateFile Sample(int resources = 2, int operations = 2)
    {
        var state = StateFile.CreateNew("sample", CliSandbox.Tenant, Start);
        for (int i = 0; i < resources; i++)
        {
            state.Upsert(new StateEntry
            {
                Id = ResourceIdentifier.Create(Fixture.AppType, "app" + i),
                Type = Fixture.AppType,
                ApiVersion = "2026-10-01-preview",
                Key = new Dictionary<string, string> { ["uniqueName"] = "app" + i },
                GraphId = "G" + i,
                LastWriteOn = Start,
                DependsOn = i == 0 ? [] : ["/providers/Microsoft.Entra/applications/app0"],
            });
        }

        for (int i = 0; i < operations; i++)
        {
            state.Operations.Add(new OperationRecord
            {
                ResourceId = "/providers/Microsoft.Entra/applications/app" + i,
                Type = Fixture.AppType,
                Status = i == 1 ? OperationStatus.Failed : OperationStatus.Succeeded,
                ChangeType = i == 0 ? ChangeType.Create : null,
                Batch = i == 0 ? 1 : null,
                StartedOn = Start,
                FinishedOn = i == 0 ? Start.AddMilliseconds(1500) : null,
                GraphId = i == 1 ? null : "G" + i,
                Error = i == 1 ? "boom" : null,
                ErrorCode = i == 1 ? "ResourceApplyFailed" : null,
            });
        }

        state.CompleteRun(operations == 0, "Incremental", "sha256:t", Start.AddMinutes(1));
        return state;
    }

    private static string Render(Action<StringWriter> write)
    {
        using var writer = new StringWriter();
        write(writer);
        return writer.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    // ---- StateWriter.WriteShow ----

    [Fact]
    public void Show_OutputNone_WritesNothing_AndANullStateIsRejected()
    {
        Assert.Equal(string.Empty, Render(w => StateWriter.WriteShow(Sample(), "p.json", null, null, OutputFormat.None, true, w)));
        Assert.Throws<ArgumentNullException>(() => StateWriter.WriteShow(null!, "p.json", null, null, OutputFormat.Table, true, TextWriter.Null));
    }

    [Fact]
    public void Show_Table_WithDrift_AddsALiveColumn_AndMarksUnreportedRowsWithADash()
    {
        var drift = new Dictionary<string, DriftReport>
        {
            ["/providers/Microsoft.Entra/applications/app0"] = new(DriftReport.InSync, "G0", null),
            ["/providers/Microsoft.Entra/applications/app1"] = new(DriftReport.Replaced, "G9", "the key now belongs to G9"),
        };

        string withDrift = Render(w => StateWriter.WriteShow(Sample(3), "p.json", null, drift, OutputFormat.Table, true, w));
        string without = Render(w => StateWriter.WriteShow(Sample(3), "p.json", null, null, OutputFormat.Table, true, w));

        Assert.Contains("Live", withDrift, StringComparison.Ordinal);
        Assert.DoesNotContain("Live", without, StringComparison.Ordinal);
        var rows = withDrift.Split('\n');
        Assert.Contains(rows, r => r.StartsWith("/providers/Microsoft.Entra/applications/app0", StringComparison.Ordinal) && r.EndsWith("InSync", StringComparison.Ordinal));
        Assert.Contains(rows, r => r.StartsWith("/providers/Microsoft.Entra/applications/app1", StringComparison.Ordinal) && r.EndsWith("Replaced (the key now belongs to G9)", StringComparison.Ordinal));
        Assert.Contains(rows, r => r.StartsWith("/providers/Microsoft.Entra/applications/app2", StringComparison.Ordinal) && r.EndsWith(" -", StringComparison.Ordinal));
    }

    [Fact]
    public void Show_Table_WithoutResources_StopsAfterTheSummary()
    {
        string text = Render(w => StateWriter.WriteShow(Sample(0, 0), "p.json", null, null, OutputFormat.Table, true, w));

        Assert.Contains("Resources:      0", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Graph id", text, StringComparison.Ordinal);
        Assert.Contains("Lock:           none", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Show_Json_IsCompactOrIndented_AndCarriesLockAndDrift()
    {
        var holder = new StateLockInfo(77, "build-host", "svc", Start);
        var drift = new Dictionary<string, DriftReport> { ["/providers/Microsoft.Entra/applications/app1"] = new(DriftReport.Missing, null, "not found in the tenant"), ["/providers/Microsoft.Entra/applications/app0"] = new(DriftReport.InSync, "G0", null) };

        string pretty = Render(w => StateWriter.WriteShow(Sample(), "p.json", holder, drift, OutputFormat.Json, true, w));
        string compact = Render(w => StateWriter.WriteShow(Sample(), "p.json", holder, drift, OutputFormat.Json, false, w));

        Assert.Contains("\n  ", pretty, StringComparison.Ordinal);
        Assert.Single(compact.TrimEnd('\n').Split('\n'));
        var json = JsonNode.Parse(compact)!.AsObject();
        Assert.Equal("p.json", json["stateFile"]!.GetValue<string>());
        Assert.Equal(77, json["lock"]!["processId"]!.GetValue<int>());
        Assert.Equal("build-host", json["lock"]!["host"]!.GetValue<string>());
        var entries = json["drift"]!.AsArray();
        Assert.Equal(["/providers/Microsoft.Entra/applications/app0", "/providers/Microsoft.Entra/applications/app1"], entries.Select(e => e!["resourceId"]!.GetValue<string>()));
        Assert.Equal("Missing", entries[1]!["status"]!.GetValue<string>());
        Assert.Null(entries[1]!["liveGraphId"]);
        Assert.Equal("not found in the tenant", entries[1]!["detail"]!.GetValue<string>());
    }

    // ---- StateWriter.WriteList ----

    [Fact]
    public void List_Table_ShowsEveryColumn_TheLockFlag_AndUnreadableDeployments()
    {
        var items = new List<DeploymentListItem>
        {
            new("a-dep", Sample(), null, null),
            new("b-dep", Sample(1), new StateLockInfo(1, "h", "u", Start), null),
            new("c-dep", null, null, "schemaVersion 2 is not supported"),
        };

        string text = Render(w => StateWriter.WriteList(items, "state-dir", OutputFormat.Table, true, w));

        Assert.StartsWith("State directory: state-dir", text, StringComparison.Ordinal);
        var rows = text.Split('\n');
        Assert.Contains(rows, r => r.StartsWith("a-dep", StringComparison.Ordinal) && r.Contains("Failed", StringComparison.Ordinal) && r.EndsWith("-", StringComparison.Ordinal));
        Assert.Contains(rows, r => r.StartsWith("b-dep", StringComparison.Ordinal) && r.EndsWith("locked", StringComparison.Ordinal));
        Assert.Contains(rows, r => r.StartsWith("c-dep", StringComparison.Ordinal) && r.Contains("unreadable", StringComparison.Ordinal));
        Assert.Contains("  c-dep: schemaVersion 2 is not supported", text, StringComparison.Ordinal);
    }

    [Fact]
    public void List_Empty_SaysSo_InTableAndJson_AndNoneIsSilent()
    {
        Assert.Contains("No deployments.", Render(w => StateWriter.WriteList([], "d", OutputFormat.Table, true, w)), StringComparison.Ordinal);
        var json = JsonNode.Parse(Render(w => StateWriter.WriteList([], "d", OutputFormat.Json, false, w)))!;
        Assert.Equal("d", json["stateDirectory"]!.GetValue<string>());
        Assert.Empty(json["deployments"]!.AsArray());
        Assert.Equal(string.Empty, Render(w => StateWriter.WriteList([], "d", OutputFormat.None, true, w)));
        Assert.Throws<ArgumentNullException>(() => StateWriter.WriteList(null!, "d", OutputFormat.Table, true, TextWriter.Null));
    }

    [Fact]
    public void List_Json_CarriesNullsForUnreadableDeployments_AndTheLockHolder()
    {
        var items = new List<DeploymentListItem>
        {
            new("ok", Sample(), new StateLockInfo(5, "h", "u", Start), null),
            new("bad", null, null, "unreadable: boom"),
        };

        var json = JsonNode.Parse(Render(w => StateWriter.WriteList(items, "d", OutputFormat.Json, false, w)))!["deployments"]!.AsArray();

        Assert.Equal(2, json[0]!["resourceCount"]!.GetValue<int>());
        Assert.True(json[0]!["locked"]!.GetValue<bool>());
        Assert.Contains("process 5", json[0]!["lockHolder"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Null(json[1]!["resourceCount"]);
        Assert.False(json[1]!["locked"]!.GetValue<bool>());
        Assert.Equal("unreadable: boom", json[1]!["error"]!.GetValue<string>());
    }

    // ---- StateWriter.WriteOperations ----

    [Fact]
    public void Operations_Table_ShowsDurationsDashesAndErrors()
    {
        string text = Render(w => StateWriter.WriteOperations(Sample(), OutputFormat.Table, true, w));

        Assert.Contains("last run Failed (Incremental)", text, StringComparison.Ordinal);
        Assert.Contains("1500 ms", text, StringComparison.Ordinal);
        var failedRow = text.Split('\n').Single(r => r.Contains("app1", StringComparison.Ordinal));
        Assert.Contains("boom", failedRow, StringComparison.Ordinal);
        Assert.Contains(" - ", failedRow, StringComparison.Ordinal);
    }

    [Fact]
    public void Operations_WithNoOperations_PrintsOnlyTheHeaderLine_AndJsonIsAnEmptyArray()
    {
        string table = Render(w => StateWriter.WriteOperations(Sample(1, 0), OutputFormat.Table, true, w));
        var json = JsonNode.Parse(Render(w => StateWriter.WriteOperations(Sample(1, 0), OutputFormat.Json, false, w)))!;

        Assert.Single(table.TrimEnd('\n').Split('\n'));
        Assert.Empty(json["operations"]!.AsArray());
        Assert.Equal(string.Empty, Render(w => StateWriter.WriteOperations(Sample(), OutputFormat.None, true, w)));
        Assert.Throws<ArgumentNullException>(() => StateWriter.WriteOperations(null!, OutputFormat.Table, true, TextWriter.Null));
    }

    [Fact]
    public void Operations_Json_UsesNullsForMissingTimes_AndMillisecondsForDurations()
    {
        var operations = JsonNode.Parse(Render(w => StateWriter.WriteOperations(Sample(), OutputFormat.Json, false, w)))!["operations"]!.AsArray();

        Assert.Equal(1500, operations[0]!["durationMs"]!.GetValue<long>());
        Assert.Null(operations[1]!["durationMs"]);
        Assert.Null(operations[1]!["finishedOn"]);
        Assert.Equal("ResourceApplyFailed", operations[1]!["errorCode"]!.GetValue<string>());
    }

    // ---- DeploymentResultWriter ----

    private static ResourceOperation Op(string name, OperationStatus status, ChangeType? change = null, string? error = null)
    {
        return new ResourceOperation(ResourceIdentifier.Create(Fixture.AppType, name), Fixture.AppType, 1, status, change, null, "G-" + name, error, error is null ? null : "ResourceApplyFailed", TimeSpan.FromMilliseconds(250));
    }

    private static DeploymentResult Result(IReadOnlyList<ResourceOperation> operations, JsonObject? outputs = null, IReadOnlyList<Diagnostic>? diagnostics = null, string? secureFile = null, DeploymentStatus status = DeploymentStatus.Succeeded)
    {
        return new DeploymentResult("demo", status, operations, [], outputs ?? new JsonObject(), new JsonObject(), diagnostics ?? [], secureFile, TimeSpan.FromMilliseconds(2500));
    }

    [Fact]
    public void Result_Table_DescribesEveryOperationStatus_AndRoutesFailuresToStderr()
    {
        var result = Result(
            [
                Op("created", OperationStatus.Succeeded, ChangeType.Create),
                Op("noop", OperationStatus.Succeeded),
                Op("failed", OperationStatus.Failed, error: "Graph 403 denied"),
                Op("skipped", OperationStatus.Skipped),
                Op("ignored", OperationStatus.Ignored),
                Op("detached", OperationStatus.Detached),
                Op("removed", OperationStatus.Succeeded, ChangeType.Delete),
            ],
            status: DeploymentStatus.Failed);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        DeploymentResultWriter.Write(result, OutputFormat.Table, true, stdout, stderr);

        string o = stdout.ToString();
        string e = stderr.ToString();
        Assert.Contains("Deployment 'demo' Failed in 2.5 s: 2 succeeded, 1 failed, 1 skipped, 1 ignored, 1 detached, 1 deleted.", o, StringComparison.Ordinal);
        Assert.Contains("(Create)", o, StringComparison.Ordinal);
        Assert.Contains("(condition false)", o, StringComparison.Ordinal);
        Assert.Contains("(not in template; detached from state)", o, StringComparison.Ordinal);
        Assert.Contains("(Skipped)", o, StringComparison.Ordinal);
        Assert.Contains("! /providers/Microsoft.Entra/applications/failed (Graph 403 denied)", e, StringComparison.Ordinal);
        Assert.DoesNotContain("failed (Graph", o, StringComparison.Ordinal);
    }

    [Fact]
    public void Result_Table_SplitsDiagnosticsBySeverity_ListsOutputs_AndPointsAtTheSecureFile()
    {
        var outputs = new JsonObject
        {
            ["greeting"] = new JsonObject { ["type"] = "string", ["value"] = "hello" },
            ["nothing"] = new JsonObject { ["type"] = "string" },
            ["bare"] = JsonValue.Create(3),
        };
        var diagnostics = new[] { Diagnostic.Error("E1", "an error"), Diagnostic.Warning("W1", "a warning"), Diagnostic.Info("I1", "a note") };
        var result = Result([Op("a", OperationStatus.Succeeded, ChangeType.NoChange)], outputs, diagnostics, "C:\\secure\\out.json");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        DeploymentResultWriter.Write(result, OutputFormat.Table, false, stdout, stderr);

        string o = stdout.ToString();
        Assert.Contains("Outputs:", o, StringComparison.Ordinal);
        Assert.Contains("  greeting: \"hello\"", o, StringComparison.Ordinal);
        Assert.Contains("  nothing: null", o, StringComparison.Ordinal);
        Assert.Contains("  bare: null", o, StringComparison.Ordinal);
        Assert.Contains("W1", o, StringComparison.Ordinal);
        Assert.Contains("I1", o, StringComparison.Ordinal);
        Assert.DoesNotContain("E1", o, StringComparison.Ordinal);
        Assert.Contains("E1", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("Secure outputs written to C:\\secure\\out.json.", o, StringComparison.Ordinal);
    }

    [Fact]
    public void Result_Json_HasEveryCounterAndTheDiagnostics_AndNoneWritesNothing()
    {
        var result = Result([Op("a", OperationStatus.Succeeded, ChangeType.Create), Op("b", OperationStatus.Failed, error: "x")], new JsonObject { ["o"] = new JsonObject { ["type"] = "string", ["value"] = "v" } }, [Diagnostic.Warning("W1", "careful")], "f.json", DeploymentStatus.Failed);
        using var stdout = new StringWriter();

        DeploymentResultWriter.Write(result, OutputFormat.Json, false, stdout, TextWriter.Null);
        var json = JsonNode.Parse(stdout.ToString())!;
        using var silent = new StringWriter();
        DeploymentResultWriter.Write(result, OutputFormat.None, true, silent, silent);

        Assert.Equal("Failed", json["status"]!.GetValue<string>());
        Assert.Equal(2500, json["durationMs"]!.GetValue<long>());
        Assert.Equal(1, json["succeeded"]!.GetValue<int>());
        Assert.Equal(1, json["failed"]!.GetValue<int>());
        Assert.Equal("f.json", json["secureOutputsFile"]!.GetValue<string>());
        Assert.Equal("W1", json["diagnostics"]![0]!["code"]!.GetValue<string>());
        Assert.Equal("Create", json["operations"]![0]!["changeType"]!.GetValue<string>());
        Assert.Equal("ResourceApplyFailed", json["operations"]![1]!["errorCode"]!.GetValue<string>());
        Assert.Equal("v", json["outputs"]!["o"]!["value"]!.GetValue<string>());
        Assert.Equal(string.Empty, silent.ToString());
        Assert.Throws<ArgumentNullException>(() => DeploymentResultWriter.Write(null!, OutputFormat.Table, true, TextWriter.Null, TextWriter.Null));
    }
}
