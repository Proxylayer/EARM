using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using EArm.Cli;
using EArm.Cli.Output;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Engine.Execution;
using EArm.Engine.Providers;
using EArm.Tests.Engine;
using Xunit;

namespace EArm.Tests.Cli;

/// <summary>
/// The CLI result renderer in-process (no pwsh, no command line): <see cref="DeploymentResultWriter"/> (every operation status,
/// diagnostics routed to stdout / stderr, outputs, secure-outputs hint, orphan report rows).
/// </summary>
public sealed class OutputWriterTests
{
    // ---- DeploymentResultWriter ----

    private static ResourceOperation Op(string name, OperationStatus status, ChangeType? change = null, string? error = null)
    {
        return new ResourceOperation(ResourceIdentifier.Create(Fixture.AppType, name), Fixture.AppType, 1, status, change, null, "G-" + name, error, error is null ? null : "ResourceApplyFailed", TimeSpan.FromMilliseconds(250));
    }

    private static DeploymentResult Result(IReadOnlyList<ResourceOperation> operations, JsonObject? outputs = null, IReadOnlyList<Diagnostic>? diagnostics = null, string? secureFile = null, DeploymentStatus status = DeploymentStatus.Succeeded, IReadOnlyList<ResourceChange>? orphans = null)
    {
        return new DeploymentResult("demo", status, operations, [], outputs ?? new JsonObject(), new JsonObject(), diagnostics ?? [], secureFile, TimeSpan.FromMilliseconds(2500), orphans);
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
            ],
            status: DeploymentStatus.Failed,
            orphans: [ResourceChange.OrphanRow(ResourceIdentifier.Create(Fixture.AppType, "legacy"), Fixture.AppType, "G-legacy", OrphanReason.Undeclared, "not declared in the template")]);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        DeploymentResultWriter.Write(result, OutputFormat.Table, true, stdout, stderr);

        string o = stdout.ToString();
        string e = stderr.ToString();
        Assert.Contains("Deployment 'demo' Failed in 2.5 s: 2 succeeded, 1 failed, 1 skipped, 1 ignored, 1 orphan(s) reported.", o, StringComparison.Ordinal);
        Assert.Contains("(Create)", o, StringComparison.Ordinal);
        Assert.Contains("(condition false)", o, StringComparison.Ordinal);
        Assert.Contains("Not declared in the template (reported only; never deleted):", o, StringComparison.Ordinal);
        Assert.Contains("* /providers/Microsoft.Entra/applications/legacy [Microsoft.Entra/applications] (orphan: undeclared) Graph id G-legacy", o, StringComparison.Ordinal);
        var json = DeploymentResultWriter.ToJson(result);
        Assert.False(json.ContainsKey("detached"));
        Assert.False(json.ContainsKey("deleted"));
        var orphan = Assert.Single(json["orphans"]!.AsArray())!;
        Assert.Equal("undeclared", orphan["orphan"]!.GetValue<string>());
        Assert.Equal("G-legacy", orphan["graphId"]!.GetValue<string>());
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
