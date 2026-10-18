using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Cli;
using EArm.Cli.Commands;
using EArm.Cli.Output;
using EArm.Core.Json;
using EArm.Core.Secrets;
using EArm.Engine.Providers;
using EArm.Engine.WhatIf;
using Microsoft.Extensions.Logging;
using Xunit;

namespace EArm.Tests.Engine;

/// <summary>Unit tests of the what-if model and renderers (docs/whatif.md): <see cref="WhatIfReport"/>, <see cref="WhatIfText"/>, <see cref="WhatIfJson"/>, the masking helpers and the CLI's <c>WhatIfWriter</c>.</summary>
public sealed class WhatIfRenderingTests
{
    private const string Esc = "\u001b[";

    private static JsonPath P(string text)
    {
        return JsonPath.Parse(text);
    }

    private static string Text(WhatIfReport report, WhatIfTextOptions? options = null)
    {
        using var writer = new StringWriter { NewLine = "\n" };
        WhatIfText.Render(report, writer, options ?? WhatIfTextOptions.Default);
        return writer.ToString();
    }

    private static DeploymentInvocation Invocation(OutputFormat output, ResultFormat format = ResultFormat.FullResourcePayloads, bool noPrettyPrint = false, LogLevel level = LogLevel.Information)
    {
        return new DeploymentInvocation
        {
            TemplateFile = "x.entra.json",
            Output = output,
            ResultFormat = format,
            NoPrettyPrint = noPrettyPrint,
            LogLevel = level,
            Connection = new EArm.Cli.Hosting.ConnectionSettings(),
            WhatIf = true,
        };
    }

    // ---- report ----

    [Fact]
    public async Task Report_OrdersRowsByBatchThenIdThenTheOrphanReport()
    {
        var run = await WhatIfScenario.Find("create-incremental").PlanAsync();

        var ids = run.Report.Rows.Select(r => r.ResourceId).ToList();
        var skipped = run.Report.Skipped.Select(r => r.ResourceId).ToHashSet(StringComparer.Ordinal);
        var batchOrder = run.Plan.Batches.SelectMany(b => b.Instances.OrderBy(i => i.PlannedId.Id, StringComparer.Ordinal)).Select(i => i.PlannedId.Id).Where(id => !skipped.Contains(id)).ToList();
        Assert.Equal(batchOrder, ids);
        Assert.All(run.Report.Rows, r => Assert.False(r.IsOrphan));
        Assert.Equal(run.Plan.Changes.Count, run.Report.Rows.Count + run.Report.Skipped.Count);
        Assert.Single(skipped);

        var orphans = await WhatIfScenario.Find("orphans-complete").PlanAsync();
        var rows = orphans.Report.Rows;
        int firstOrphan = rows.ToList().FindIndex(r => r.IsOrphan);
        Assert.True(firstOrphan > 0);
        Assert.All(rows.Take(firstOrphan), r => Assert.False(r.IsOrphan));
        Assert.All(rows.Skip(firstOrphan), r => Assert.True(r.IsOrphan));
        var tail = rows.Skip(firstOrphan).Select(r => (r.ResourceType, r.ResourceId)).ToList();
        Assert.Equal(tail.OrderBy(t => t.ResourceType, StringComparer.Ordinal).ThenBy(t => t.ResourceId, StringComparer.Ordinal), tail);
    }

    [Fact]
    public async Task Report_CarriesTheNamesTenantModePermissionsAndDiagnostics()
    {
        var run = await WhatIfScenario.Find("orphans-complete").PlanAsync();

        Assert.Equal("orphans-complete", run.Report.DeploymentName);
        Assert.Equal("11111111-1111-1111-1111-111111111111", run.Report.TenantId);
        Assert.Equal(EArm.Engine.Planning.DeploymentMode.Complete, run.Report.Mode);
        Assert.Equal(run.Plan.RequiredPermissions.Order(StringComparer.Ordinal), run.Report.RequiredPermissions);
        Assert.Equal(run.Plan.Diagnostics, run.Report.Diagnostics);
        Assert.Equal(EdgeSummary.Empty, run.Report.Edges);
        Assert.False(EdgeSummary.Empty.HasChanges);
        Assert.Equal(new EdgeSummary(3, 2), new EdgeSummary(1, 2) + new EdgeSummary(2, 0));
    }

    [Fact]
    public async Task Row_ExposesTheArmPayloadShape()
    {
        var run = await WhatIfScenario.Find("modify-incremental").PlanAsync();

        var created = run.Row("/providers/Microsoft.Entra/groups/contoso-dev-orders-auditors");
        Assert.Equal(["name", "type", "apiVersion", "properties"], created.After!.Select(p => p.Key).ToList());
        Assert.Equal("contoso-dev-orders-auditors", created.After!["name"]!.GetValue<string>());
        Assert.Equal("Microsoft.Entra/groups", created.After["type"]!.GetValue<string>());
        Assert.Equal("2026-10-01-preview", created.After["apiVersion"]!.GetValue<string>());
        Assert.Null(created.Before);
        Assert.All(created.Deltas, d => Assert.Equal(PropertyChangeType.Create, d.Kind));

        var modified = run.Row("/providers/Microsoft.Entra/applications/contoso-dev-api");
        Assert.NotNull(modified.Before);
        Assert.NotNull(modified.After);
        Assert.Contains(modified.Deltas, d => d.Path.ToString() == "properties.displayName");

        var unchanged = run.Row("/providers/Microsoft.Entra/groups/contoso-dev-orders-ops");
        Assert.NotNull(unchanged.Before);
        Assert.NotNull(unchanged.After);
        Assert.Empty(unchanged.Deltas);
        Assert.Equal("=", unchanged.Marker);
    }

    // ---- text ----

    [Fact]
    public async Task Text_RendersHeaderLegendAndFooter()
    {
        var text = Text((await WhatIfScenario.Find("create-incremental").PlanAsync()).Report);
        var lines = text.Split('\n');

        Assert.Equal("Deployment 'create-incremental' (Incremental) on tenant 11111111-1111-1111-1111-111111111111", lines[0]);
        Assert.Equal("", lines[1]);
        Assert.Equal("Resource and property changes are indicated with these symbols:", lines[2]);
        Assert.Equal(["  + Create", "  ~ Modify", "  - Delete", "  = NoChange", "  * Ignore", "  ? Value only known at deployment time", "  ! Change cannot be applied as planned"], lines.Skip(3).Take(7).ToList());
        Assert.Contains("The deployment would make the following changes:", lines);
        Assert.DoesNotContain("\u001b", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Text_ResourceIdOnly_PrintsRowsReasonsAndFooterOnly()
    {
        var run = await WhatIfScenario.Find("modify-incremental").PlanAsync();

        string full = Text(run.Report);
        string idOnly = Text(run.Report, new WhatIfTextOptions(ResultFormat.ResourceIdOnly));

        Assert.Contains("properties.displayName", full);
        Assert.DoesNotContain("properties:", idOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("~ properties.", idOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("name:", idOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("apiVersion:", idOnly, StringComparison.Ordinal);
        // ARM's ResourceIdOnly: an existing declared resource is "Deploy" ('!'); a change that cannot be applied adds '!' after the symbol.
        Assert.Contains("  ! Deploy", idOnly);
        Assert.DoesNotContain("  ~ Modify", idOnly, StringComparison.Ordinal);
        Assert.Contains("  ! /providers/Microsoft.Entra/applications/contoso-dev-api [Microsoft.Entra/applications]", idOnly);
        Assert.Contains("  !! /providers/Microsoft.Entra/groups/contoso-legacy [Microsoft.Entra/groups]", idOnly);
        Assert.Contains("! Immutable property", idOnly);
        Assert.Contains("Resource changes: 1 to create, 5 to deploy, 0 to delete, 0 ignored.", idOnly);
        Assert.Contains("1 change(s) cannot be applied as planned; see the rows marked '!' after the symbol.", idOnly);
    }

    [Fact]
    public async Task Text_Verbose_AddsTheNoChangePayload()
    {
        var run = await WhatIfScenario.Find("modify-incremental").PlanAsync();

        string quiet = Text(run.Report);
        string verbose = Text(run.Report, new WhatIfTextOptions(Verbose: true));

        string row = "  = /providers/Microsoft.Entra/groups/contoso-dev-orders-ops [Microsoft.Entra/groups]";
        Assert.Contains(row + "\n\n", quiet);
        Assert.Contains(row + "\n      name: \"contoso-dev-orders-ops\"", verbose);
        Assert.True(verbose.Length > quiet.Length);
    }

    [Fact]
    public async Task Text_Colors_WrapRowsInAnsiCodes_ByChangeType()
    {
        var run = await WhatIfScenario.Find("modify-incremental").PlanAsync();

        string plain = Text(run.Report);
        string colored = Text(run.Report, new WhatIfTextOptions(Colors: true));

        Assert.DoesNotContain(Esc, plain, StringComparison.Ordinal);
        Assert.Contains(Esc + "32m  + /providers/Microsoft.Entra/groups/contoso-dev-orders-auditors", colored);
        Assert.Contains(Esc + "35m  ~ /providers/Microsoft.Entra/applications/contoso-dev-api", colored);
        Assert.Contains(Esc + "90m  = /providers/Microsoft.Entra/groups/contoso-dev-orders-ops", colored);
        Assert.Contains(Esc + "1;31m      ! Immutable property", colored);
        Assert.Contains(Esc + "0m", colored);
        Assert.Equal(plain, System.Text.RegularExpressions.Regex.Replace(colored, "\u001b\\[[0-9;]*m", string.Empty));
    }

    [Fact]
    public async Task Text_WithoutResources_SaysSo()
    {
        var host = new EngineHost();
        var plan = await host.WhatIfAsync(Templates.Resources(string.Empty));

        string text = Text(WhatIfReport.From(plan));

        Assert.Contains("The template declares no resources.", text);
        Assert.DoesNotContain("Orphans:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("The deployment would make the following changes:", text, StringComparison.Ordinal);
        Assert.Contains("Resource changes: 0 to create, 0 to modify, 0 to delete, 0 unchanged, 0 ignored.", text);
        Assert.Contains("Edges: 0 to add, 0 to remove.", text);
        Assert.Contains("Required permissions: none.", text);
        Assert.False(WhatIfReport.From(plan).HasChanges);
    }

    [Fact]
    public async Task Text_WithoutATenant_SaysUnknown_AndListsUnresolvedReferences()
    {
        var host = new EngineHost();
        var document = Templates.Resources("{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"g\", \"properties\": { \"displayName\": \"G\", \"mailEnabled\": false, \"securityEnabled\": true, \"description\": \"[reference(resourceId('Microsoft.Entra/appRoleAssignments', '11111111-1111-1111-1111-111111111111'), '2026-10-01-preview').id]\" } }");
        var plan = await host.WhatIfAsync(document, options: host.Options(tenantId: null));

        string text = Text(WhatIfReport.From(plan));

        Assert.Contains("on tenant (unknown)", text);
        Assert.Contains("warning UnresolvedReference: reference to existing resource '/providers/Microsoft.Entra/appRoleAssignments/11111111-1111-1111-1111-111111111111' (apiVersion 2026-10-01-preview) could not be resolved", text);
        Assert.Contains("  +? /providers/Microsoft.Entra/groups/g", text);
        Assert.Contains("description: \"(deploy-time)\"", text);
        Assert.Null(WhatIfJson.Build(plan)["tenantId"]);
    }

    [Fact]
    public async Task Text_PrintsDiagnosticsAfterTheFooter()
    {
        var text = Text((await WhatIfScenario.Find("retained-incremental").PlanAsync()).Report);

        int footer = text.IndexOf("Required permissions:", StringComparison.Ordinal);
        int warning = text.IndexOf("warning AssignmentRetained:", StringComparison.Ordinal);
        Assert.True(footer > 0 && warning > footer);
        Assert.Contains("[Microsoft.Entra/roleAssignments] (orphan: retained)", text);
        Assert.Contains("Orphans: 1 live object(s) not declared in the template (reported only; never deleted).", text);
    }

    // ---- JSON ----

    [Fact]
    public async Task Json_HasAFixedMemberOrder_AndTheDocumentedSummary()
    {
        var run = await WhatIfScenario.Find("modify-incremental").PlanAsync();

        var json = WhatIfJson.Build(run.Report);

        Assert.Equal(["status", "deploymentName", "tenantId", "mode", "changes", "diagnostics", "requiredPermissions", "unresolvedReferences", "skipped", "summary"], json.Select(p => p.Key).ToList());
        var summary = json["summary"]!.AsObject();
        Assert.Equal(1, summary["create"]!.GetValue<int>());
        Assert.Equal(4, summary["modify"]!.GetValue<int>());
        Assert.Equal(0, summary["delete"]!.GetValue<int>());
        Assert.Equal(1, summary["noChange"]!.GetValue<int>());
        Assert.Equal(0, summary["ignore"]!.GetValue<int>());
        Assert.Equal(1, summary["unsupported"]!.GetValue<int>());
        Assert.Equal(3, summary["edges"]!["add"]!.GetValue<int>());
        Assert.Equal(1, summary["edges"]!["remove"]!.GetValue<int>());
        Assert.Equal(["Application.ReadWrite.All", "Directory.Read.All", "Group.ReadWrite.All", "GroupMember.ReadWrite.All", "User.ReadWrite.All"], json["requiredPermissions"]!.AsArray().Select(p => p!.GetValue<string>()).ToList());
    }

    [Fact]
    public async Task Json_FollowsTheArmPayloadRulesPerChangeType()
    {
        var modify = WhatIfJson.Build((await WhatIfScenario.Find("modify-incremental").PlanAsync()).Report)["changes"]!.AsArray().Select(c => c!.AsObject()).ToList();
        var orphans = WhatIfJson.Build((await WhatIfScenario.Find("orphans-complete").PlanAsync()).Report)["changes"]!.AsArray().Select(c => c!.AsObject()).ToList();
        var create = WhatIfJson.Build((await WhatIfScenario.Find("create-incremental").PlanAsync()).Report)["changes"]!.AsArray().Select(c => c!.AsObject()).ToList();

        JsonObject Of(System.Collections.Generic.List<JsonObject> rows, string type) => rows.First(r => r["changeType"]!.GetValue<string>() == type);

        var created = Of(modify, "Create");
        Assert.NotNull(created["after"]);
        Assert.Null(created["before"]);
        Assert.Null(created["delta"]);

        var modified = Of(modify, "Modify");
        Assert.NotNull(modified["before"]);
        Assert.NotNull(modified["after"]);
        Assert.NotNull(modified["delta"]);

        var noChange = Of(modify, "NoChange");
        Assert.NotNull(noChange["before"]);
        Assert.NotNull(noChange["after"]);
        Assert.Null(noChange["delta"]);

        Assert.DoesNotContain(orphans, r => r["changeType"]!.GetValue<string>() == "Delete");
        var undeclared = orphans.Single(r => r["orphan"]?.GetValue<string>() == "undeclared");
        Assert.Equal("Ignore", undeclared["changeType"]!.GetValue<string>());
        Assert.Null(undeclared["before"]);
        Assert.Null(undeclared["after"]);
        Assert.Null(undeclared["delta"]);
        Assert.Equal("22222222-0000-0000-0000-000000000050", undeclared["graphId"]!.GetValue<string>());
        Assert.StartsWith("not declared in the template", undeclared["reasons"]![0]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("retained", orphans.Single(r => r["resourceId"]!.GetValue<string>().Contains("roleAssignments", StringComparison.Ordinal) && r["orphan"] is not null)["orphan"]!.GetValue<string>());
        Assert.All(orphans.Where(r => r["orphan"] is null), r => Assert.False(r.ContainsKey("graphId")));

        // A resource whose condition is false is not a change: ARM's what-if does not list it.
        Assert.DoesNotContain(create, r => r["changeType"]!.GetValue<string>() == "Ignore");

        var unsupported = modify.Single(c => c["unsupportedReason"] is not null);
        Assert.Equal("/providers/Microsoft.Entra/groups/contoso-legacy", unsupported["resourceId"]!.GetValue<string>());
        Assert.Equal("Unsupported", unsupported["changeType"]!.GetValue<string>());
        Assert.All(modify.Where(c => !ReferenceEquals(c, unsupported)), c => Assert.True(c.ContainsKey("unsupportedReason") && c["unsupportedReason"] is null));
    }

    [Fact]
    public async Task Json_ResourceIdOnly_DropsPayloadsAndDeltas()
    {
        var run = await WhatIfScenario.Find("modify-incremental").PlanAsync();

        var changes = WhatIfJson.Build(run.Report, ResultFormat.ResourceIdOnly)["changes"]!.AsArray();

        Assert.Equal(run.Report.Rows.Count, changes.Count);
        Assert.All(changes, c =>
        {
            Assert.Equal(["resourceId", "changeType", "unsupportedReason"], c!.AsObject().Select(p => p.Key).Where(k => k != "reasons").ToList());
            Assert.Null(c["before"]);
            Assert.Null(c["after"]);
            Assert.Null(c["delta"]);
        });
    }

    [Fact]
    public async Task Json_DeltaPaths_AreRootedAtTheResource_AndChildrenRelative()
    {
        var run = await WhatIfScenario.Find("modify-incremental").PlanAsync();

        var app = WhatIfJson.Build(run.Report)["changes"]!.AsArray().Single(c => c!["resourceId"]!.GetValue<string>().EndsWith("/contoso-dev-api", StringComparison.Ordinal))!;
        var roles = app["delta"]!.AsArray().Single(d => d!["path"]!.GetValue<string>() == "properties.appRoles")!;

        Assert.Equal("Array", roles["propertyChangeType"]!.GetValue<string>());
        Assert.Equal(["['6d2f1c0e-2b7a-4c1e-9a1b-4f3f1f6a0002']", "['6d2f1c0e-2b7a-4c1e-9a1b-4f3f1f6a0003']"], roles["children"]!.AsArray().Select(c => c!["path"]!.GetValue<string>()).ToList());
        var web = app["delta"]!.AsArray().Single(d => d!["path"]!.GetValue<string>() == "properties.web")!;
        Assert.Equal("redirectUris", web["children"]![0]!["path"]!.GetValue<string>());
        Assert.Equal(["[1]", "[0]"], web["children"]![0]!["children"]!.AsArray().Select(c => c!["path"]!.GetValue<string>()).ToList());
    }

    [Fact]
    public void Serialize_WritesRelaxedEscaping_PrettyOrCompact()
    {
        var document = new JsonObject { ["a"] = "ünï \"q\" it's <b>", ["n"] = new JsonArray(1, 2) };

        string compact = WhatIfJson.Serialize(document, prettyPrint: false);
        string pretty = WhatIfJson.Serialize(document);

        Assert.DoesNotContain("\n", compact, StringComparison.Ordinal);
        Assert.Equal("{\"a\":\"ünï \\\"q\\\" it's <b>\",\"n\":[1,2]}", compact);
        Assert.Contains("\n", pretty, StringComparison.Ordinal);
        Assert.Contains("  \"a\": ", pretty);
        Assert.DoesNotContain("\\u0027", pretty, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u00", compact, StringComparison.Ordinal);
    }

    // ---- masking ----

    [Fact]
    public void MaskProperties_ReplacesTaintedLeavesAndSecurePaths()
    {
        var properties = new JsonObject
        {
            ["displayName"] = "x",
            ["tainted"] = SecretJson.CreateString("t0p"),
            ["passwordProfile"] = new JsonObject { ["password"] = "plain-but-secure-by-overlay", ["forceChangePasswordNextSignIn"] = true },
            ["credentials"] = new JsonArray(new JsonObject { ["secretText"] = "a" }, new JsonObject { ["secretText"] = "b", ["name"] = "n" }),
            ["list"] = new JsonArray("keep", "me"),
        };

        var masked = WhatIfMasking.MaskProperties(properties, [P("passwordProfile.password"), P("credentials.secretText"), P("absent.path"), P("list[1]")]);

        Assert.Equal("***", masked["tainted"]!.GetValue<string>());
        Assert.Equal("***", masked["passwordProfile"]!["password"]!.GetValue<string>());
        Assert.True(masked["passwordProfile"]!["forceChangePasswordNextSignIn"]!.GetValue<bool>());
        Assert.Equal(["***", "***"], masked["credentials"]!.AsArray().Select(c => c!["secretText"]!.GetValue<string>()).ToList());
        Assert.Equal("n", masked["credentials"]![1]!["name"]!.GetValue<string>());
        Assert.Equal(["keep", "***"], masked["list"]!.AsArray().Select(c => c!.GetValue<string>()).ToList());
        Assert.False(masked.ContainsKey("absent"));
        Assert.Equal("x", masked["displayName"]!.GetValue<string>());
        Assert.Equal("plain-but-secure-by-overlay", properties["passwordProfile"]!["password"]!.GetValue<string>());
    }

    [Fact]
    public void MaskDeltas_MasksSecretsAndSecurePathsOnBothSides_Recursively()
    {
        var secretDelta = new PropertyDelta(P("properties.token"), PropertyChangeType.Modify, JsonValue.Create("old"), JsonValue.Create("new"), IsSecret: true);
        var byPath = new PropertyDelta(P("properties.passwordProfile.password"), PropertyChangeType.Create, null, JsonValue.Create("pw"));
        var parent = new PropertyDelta(P("properties.passwordProfile"), PropertyChangeType.Modify, null, null, Children: [byPath]);
        var plain = new PropertyDelta(P("properties.displayName"), PropertyChangeType.Modify, JsonValue.Create("a"), JsonValue.Create("b"));
        var deltas = new[] { secretDelta, parent, plain };

        var masked = WhatIfMasking.MaskDeltas(deltas, [P("passwordProfile.password")]);

        Assert.Equal("***", masked[0].Before!.GetValue<string>());
        Assert.Equal("***", masked[0].After!.GetValue<string>());
        var child = Assert.Single(masked[1].Children!);
        Assert.True(child.IsSecret);
        Assert.Null(child.Before);
        Assert.Equal("***", child.After!.GetValue<string>());
        Assert.False(masked[1].IsSecret);
        Assert.Same(plain, masked[2]);
        Assert.True(WhatIfMasking.MaskDeltas(deltas, [])[0].IsSecret, "an IsSecret delta is masked even when the type has no secure paths");
        Assert.Equal("***", WhatIfMasking.MaskDeltas(deltas, [])[0].After!.GetValue<string>());
        Assert.Empty(WhatIfMasking.MaskDeltas([], [P("x")]));
    }

    [Fact]
    public void ContainsUnknown_LooksIntoChildren()
    {
        var leaf = new PropertyDelta(P("properties.a[0]"), PropertyChangeType.Create, null, JsonValue.Create("(deploy-time)"), IsUnknown: true);
        var parent = new PropertyDelta(P("properties.a"), PropertyChangeType.Array, null, null, Children: [leaf]);
        var known = new PropertyDelta(P("properties.b"), PropertyChangeType.Modify, JsonValue.Create(1), JsonValue.Create(2));

        Assert.True(WhatIfMasking.ContainsUnknown(parent));
        Assert.True(WhatIfMasking.ContainsUnknown(leaf));
        Assert.False(WhatIfMasking.ContainsUnknown(known));
    }

    [Theory]
    [InlineData("properties.members[0]", "properties.members", "[0]")]
    [InlineData("properties.appRoles['6d2f1c0e-2b7a-4c1e-9a1b-4f3f1f6a0002']", "properties.appRoles", "['6d2f1c0e-2b7a-4c1e-9a1b-4f3f1f6a0002']")]
    [InlineData("properties.appRoles['simple']", "properties.appRoles", "simple")]
    [InlineData("properties.web.redirectUris", "properties.web", "redirectUris")]
    [InlineData("properties.a", "", "properties.a")]
    [InlineData("properties.a", "properties.a", "properties.a")]
    [InlineData("properties.a", "properties.b", "properties.a")]
    [InlineData("[0]", "properties.members", "[0]")]
    public void RelativePaths_NestUnderTheirParent(string path, string parent, string expected)
    {
        Assert.Equal(expected, WhatIfPaths.Relative(P(path), parent.Length == 0 ? JsonPath.Root : P(parent)));
    }

    // ---- the CLI writer ----

    [Fact]
    public async Task Writer_RendersTextJsonOrNothing()
    {
        var report = (await WhatIfScenario.Find("modify-incremental").PlanAsync()).Report;
        using var table = new StringWriter { NewLine = "\n" };
        using var pretty = new StringWriter { NewLine = "\n" };
        using var compact = new StringWriter { NewLine = "\n" };
        using var none = new StringWriter();
        using var idOnly = new StringWriter { NewLine = "\n" };

        WhatIfWriter.Write(report, Invocation(OutputFormat.Table), table);
        WhatIfWriter.Write(report, Invocation(OutputFormat.Json), pretty);
        WhatIfWriter.Write(report, Invocation(OutputFormat.Json, noPrettyPrint: true), compact);
        WhatIfWriter.Write(report, Invocation(OutputFormat.None), none);
        WhatIfWriter.Write(report, Invocation(OutputFormat.Json, ResultFormat.ResourceIdOnly, noPrettyPrint: true), idOnly);

        Assert.Equal(Text(report), table.ToString());
        Assert.Equal(WhatIfJson.Serialize(WhatIfJson.Build(report), prettyPrint: true) + "\n", pretty.ToString());
        Assert.Single(compact.ToString().TrimEnd('\n').Split('\n'));
        Assert.Equal(WhatIfJson.Build(report).ToJsonString(), JsonNode.Parse(compact.ToString())!.ToJsonString());
        Assert.Equal(string.Empty, none.ToString());
        Assert.DoesNotContain("\"delta\"", idOnly.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Writer_Verbose_PrintsTheNoChangePayload()
    {
        var report = (await WhatIfScenario.Find("modify-incremental").PlanAsync()).Report;
        using var normal = new StringWriter { NewLine = "\n" };
        using var verbose = new StringWriter { NewLine = "\n" };

        WhatIfWriter.Write(report, Invocation(OutputFormat.Table), normal);
        WhatIfWriter.Write(report, Invocation(OutputFormat.Table, level: LogLevel.Debug), verbose);

        Assert.True(verbose.ToString().Length > normal.ToString().Length);
        Assert.Contains("name: \"contoso-dev-orders-ops\"", verbose.ToString());
    }

    [Fact]
    public async Task Writer_FromAPlan_ReturnsTheReport()
    {
        var run = await WhatIfScenario.Find("retained-incremental").PlanAsync();
        using var writer = new StringWriter();

        var report = WhatIfWriter.Write(run.Plan, Invocation(OutputFormat.None), writer);

        Assert.True(report.HasChanges);
        Assert.Equal(run.Report.Rows.Count, report.Rows.Count);
    }

    [Fact]
    public void Writer_NeverUsesColours_ForCapturedOutput()
    {
        using var captured = new StringWriter();

        Assert.False(WhatIfWriter.UseColors(captured));
        Assert.Equal("NO_COLOR", WhatIfWriter.NoColorVariable);
        Assert.Equal("AZURE_CORE_NO_COLOR", WhatIfWriter.AzureNoColorVariable);
    }
}
