using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Core;
using EArm.Core.Loading;
using EArm.Engine;
using EArm.Engine.Execution;
using EArm.Engine.Providers;
using Xunit;

namespace EArm.Tests.Engine;

/// <summary>
/// languageVersion 2.0 in the engine: resources keyed by symbolic name, <c>dependsOn</c> and <c>reference()</c> by symbolic name,
/// <c>"existing": true</c> resources read (NoChange rows) and never written, <c>references()</c> and <c>reference(..., 'Full')</c>.
/// </summary>
public sealed class LanguageVersion2EngineTests
{
    private const string AdminGroup = "/providers/Microsoft.Entra/groups/portal-admins";

    private static TemplateDocument Template(string resources, string outputs = "")
    {
        string json = "{ \"$schema\": \"" + Templates.SchemaUri + "\", \"languageVersion\": \"2.0\", \"contentVersion\": \"1.0.0.0\", "
            + "\"resources\": { " + resources + " }, \"outputs\": { " + outputs + " } }";
        return new TemplateLoader().Parse(json, "lv2.entra.json", ParameterSet.Empty);
    }

    private const string Existing = "\"admins\": { \"existing\": true, \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"portal-admins\" }";

    private const string Teams = "\"teams\": { \"copy\": { \"name\": \"teams\", \"count\": 2 }, \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", "
        + "\"name\": \"[format('team-{0}', copyIndex())]\", \"properties\": { \"displayName\": \"[format('Team {0}', copyIndex())]\", \"mailEnabled\": false, \"securityEnabled\": true, "
        + "\"description\": \"[format('Admins: {0}', reference('admins').id)]\" } }";

    [Fact]
    public async Task WhatIf_AnExistingResource_IsReadAsANoChangeRow_AndItsDependentsSeeItsLiveObject()
    {
        var host = new EngineHost();
        var admins = host.Directory.AddGroup("portal-admins", "Portal admins");

        var plan = await host.WhatIfAsync(Template(Existing + ", " + Teams));

        Assert.Empty(host.Fake.Writes);
        var row = Assert.Single(plan.Changes, c => c.Id.Id == AdminGroup);
        Assert.Equal(ChangeType.NoChange, row.Type);
        Assert.False(row.HasError);
        Assert.Contains(ResourceChange.ExistingReason, row.Reasons);
        Assert.Equal(2, plan.Changes.Count(c => c.Type == ChangeType.Create));
        var team = EngineHost.Evaluated(plan, "/providers/Microsoft.Entra/groups/team-1");
        Assert.Equal($"Admins: {admins["id"]!.GetValue<string>()}", team.Properties!["description"]!.GetValue<string>());
    }

    [Fact]
    public async Task WhatIf_AMissingExistingResource_IsAnUnsupportedRow()
    {
        var host = new EngineHost();

        var plan = await host.WhatIfAsync(Template(Existing));

        var row = Assert.Single(plan.Changes, c => c.Id.Id == AdminGroup);
        Assert.True(row.HasError);
        Assert.Equal(EngineDiagnosticCodes.ExistingResourceNotFound, row.UnsupportedCode);
        Assert.Empty(host.Fake.Writes);
    }

    [Fact]
    public async Task Create_NeverWritesAnExistingResource_AndResolvesReferencesAndReferencesToIt()
    {
        var host = new EngineHost();
        var admins = host.Directory.AddGroup("portal-admins", "Portal admins");
        string outputs = "\"adminId\": { \"type\": \"string\", \"value\": \"[reference('admins').id]\" }, "
            + "\"teamNames\": { \"type\": \"array\", \"value\": \"[map(references('teams'), lambda('t', lambdaVariables('t').displayName))]\" }, "
            + "\"secondTeam\": { \"type\": \"object\", \"value\": \"[reference('teams[1]', '2026-10-01-preview', 'full')]\" }";

        var (_, result) = await host.CreateAsync(Template(Existing + ", " + Teams, outputs));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.Message)));
        Assert.DoesNotContain(host.Fake.Writes, w => w.Path.Contains(admins["id"]!.GetValue<string>(), StringComparison.Ordinal));
        Assert.Equal(2, host.Fake.Writes.Count(w => w.Path.Contains("team-", StringComparison.Ordinal)));
        var operation = Assert.Single(result.Operations, o => o.Id.Id == AdminGroup);
        Assert.Equal(OperationStatus.Succeeded, operation.Status);
        Assert.Equal(ChangeType.NoChange, operation.ChangeType);
        Assert.Equal(admins["id"]!.GetValue<string>(), result.Outputs["adminId"]!["value"]!.GetValue<string>());
        Assert.Equal("[\"Team 0\",\"Team 1\"]", result.Outputs["teamNames"]!["value"]!.ToJsonString());
        var full = result.Outputs["secondTeam"]!["value"]!.AsObject();
        Assert.Equal("/providers/Microsoft.Entra/groups/team-1", full["resourceId"]!.GetValue<string>());
        Assert.Equal("2026-10-01-preview", full["apiVersion"]!.GetValue<string>());
        Assert.Equal("teams[1]", full["symbolicName"]!.GetValue<string>());
        Assert.Equal("Team 1", full["properties"]!["displayName"]!.GetValue<string>());
    }

    [Fact]
    public async Task Create_AMissingExistingResource_FailsIt_AndSkipsItsDependents()
    {
        var host = new EngineHost();

        var (_, result) = await host.CreateAsync(Template(Existing + ", " + Teams));

        Assert.False(result.Succeeded);
        var failed = Assert.Single(result.Operations, o => o.Status == OperationStatus.Failed);
        Assert.Equal(EngineDiagnosticCodes.ExistingResourceNotFound, failed.ErrorCode);
        Assert.Empty(host.Fake.Writes);
    }

    [Fact]
    public async Task DependsOn_BySymbolicName_OrdersTheBatches()
    {
        var host = new EngineHost();
        string resources = "\"second\": { \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"second\", \"properties\": { \"displayName\": \"Second\", \"mailEnabled\": false, \"securityEnabled\": true }, \"dependsOn\": [ \"first\", \"loop[1]\" ] }, "
            + "\"first\": { \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"first\", \"properties\": { \"displayName\": \"First\", \"mailEnabled\": false, \"securityEnabled\": true } }, "
            + "\"loop\": { \"copy\": { \"name\": \"loop\", \"count\": 2 }, \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"[format('l{0}', copyIndex())]\", \"properties\": { \"displayName\": \"L\", \"mailEnabled\": false, \"securityEnabled\": true } }";

        var plan = await host.ExpandAsync(Template(resources));

        var second = EngineHost.Instance(plan, "/providers/Microsoft.Entra/groups/second");
        Assert.Contains(second.Dependencies, d => d.Id == "/providers/Microsoft.Entra/groups/first");
        Assert.Contains(second.Dependencies, d => d.Id == "/providers/Microsoft.Entra/groups/l1");
        Assert.DoesNotContain(second.Dependencies, d => d.Id == "/providers/Microsoft.Entra/groups/l0");
    }

    [Fact]
    public async Task References_LeavesOutAnIterationWhoseConditionIsFalse()
    {
        var host = new EngineHost();
        string loop = "\"loop\": { \"copy\": { \"name\": \"loop\", \"count\": 3 }, \"condition\": \"[not(equals(copyIndex(), 1))]\", \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", "
            + "\"name\": \"[format('l{0}', copyIndex())]\", \"properties\": { \"displayName\": \"[format('L{0}', copyIndex())]\", \"mailEnabled\": false, \"securityEnabled\": true } }";
        string outputs = "\"names\": { \"type\": \"array\", \"value\": \"[map(references('loop'), lambda('g', lambdaVariables('g').displayName))]\" }";

        var (_, result) = await host.CreateAsync(Template(loop, outputs));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.Message)));
        Assert.Equal("[\"L0\",\"L2\"]", result.Outputs["names"]!["value"]!.ToJsonString());
    }

    [Fact]
    public async Task Delete_LeavesAnExistingResourceAlone()
    {
        var host = new EngineHost();
        host.Directory.AddGroup("portal-admins", "Portal admins");

        var plan = await host.WhatIfAsync(Template(Existing + ", " + Teams));
        var declared = TemplateDeleter.FindDeclaredObjects(plan);

        Assert.DoesNotContain(declared, d => d.Instance.Id.Id == AdminGroup);
    }
}
