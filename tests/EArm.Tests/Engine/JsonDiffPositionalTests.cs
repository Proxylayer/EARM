using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Core.Types;
using EArm.Engine.Providers;
using Xunit;

namespace EArm.Tests.Engine;

/// <summary>
/// Arrays of objects whose position is their identity (<see cref="JsonDiffOptions.PositionalMemberKey"/>): the approval stages of a PIM policy rule have
/// no key, so a stage whose one property changed is shown as that property changed inside the stage, not as the whole stage removed and added.
/// </summary>
public sealed class JsonDiffPositionalTests
{
    private static JsonObject J(string json)
    {
        return JsonNode.Parse(json)!.AsObject();
    }

    private static string Rule(string timeout, string stage2 = "")
    {
        return "{ \"rules\": [ { \"id\": \"Approval_EndUser_Assignment\", \"setting\": { \"isApprovalRequired\": true, \"approvalStages\": [ "
            + "{ \"approvalStageTimeOutInDays\": " + timeout + ", \"isApproverJustificationRequired\": true, \"primaryApprovers\": [ { \"id\": \"a1\" } ] }"
            + stage2 + " ] } } ] }";
    }

    private static PropertyDelta StagesOf(IReadOnlyList<PropertyDelta> deltas)
    {
        var rules = Assert.Single(deltas);
        var rule = Assert.Single(rules.Children!);
        var setting = Assert.Single(rule.Children!);
        var stages = Assert.Single(setting.Children!);
        Assert.Equal(PropertyChangeType.Array, stages.Kind);
        return stages;
    }

    private static JsonDiffOptions PolicyOptions()
    {
        Assert.True(ResourceTypeCatalog.Default.TryGet("Microsoft.Entra/roleManagementPolicies", out var definition));
        return JsonDiffOptions.ForDefinition(definition, existing: true);
    }

    [Fact]
    public void AStageWhosePropertyChanged_IsShownAsThatPropertyInsideTheStage()
    {
        var stages = StagesOf(JsonDiff.Diff(J(Rule("2")), J(Rule("1")), PolicyOptions()));

        var stage = Assert.Single(stages.Children!);
        Assert.Equal(PropertyChangeType.Modify, stage.Kind);
        Assert.Equal("properties.rules.Approval_EndUser_Assignment.setting.approvalStages[0]", stage.Path.ToString());
        var property = Assert.Single(stage.Children!);
        Assert.Equal("approvalStageTimeOutInDays", property.Path.Last.Name);
        Assert.Equal("1", property.Before!.ToJsonString());
        Assert.Equal("2", property.After!.ToJsonString());
    }

    [Fact]
    public void AnAddedStage_IsACreate_AndTheUnchangedStageIsNotShown()
    {
        string second = ", { \"approvalStageTimeOutInDays\": 3, \"isApproverJustificationRequired\": false, \"primaryApprovers\": [ { \"id\": \"a2\" } ] }";

        var stages = StagesOf(JsonDiff.Diff(J(Rule("1", second)), J(Rule("1")), PolicyOptions()));

        var added = Assert.Single(stages.Children!);
        Assert.Equal(PropertyChangeType.Create, added.Kind);
        Assert.Equal(1, added.Path.Last.Index);
    }

    [Fact]
    public void ARemovedStage_IsADelete()
    {
        string second = ", { \"approvalStageTimeOutInDays\": 3, \"isApproverJustificationRequired\": false, \"primaryApprovers\": [ { \"id\": \"a2\" } ] }";

        var stages = StagesOf(JsonDiff.Diff(J(Rule("1")), J(Rule("1", second)), PolicyOptions()));

        Assert.Equal(PropertyChangeType.Delete, Assert.Single(stages.Children!).Kind);
    }

    [Fact]
    public void StagesThatAreEqual_ProduceNoDelta()
    {
        Assert.Empty(JsonDiff.Diff(J(Rule("1")), J(Rule("1")), PolicyOptions()));
    }

    [Fact]
    public void WithoutThePositionalKey_AnArrayOfObjects_IsStillASet()
    {
        var options = new JsonDiffOptions { MemberKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["rules"] = "id" } };

        var stages = StagesOf(JsonDiff.Diff(J(Rule("2")), J(Rule("1")), options));

        Assert.Equal([PropertyChangeType.Create, PropertyChangeType.Delete], stages.Children!.Select(c => c.Kind).Order());
    }
}
