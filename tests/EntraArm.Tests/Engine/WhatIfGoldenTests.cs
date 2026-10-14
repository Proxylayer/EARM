using System;
using System.Linq;
using System.Threading.Tasks;
using EntraArm.Engine.Providers;
using EntraArm.Engine.WhatIf;
using Xunit;

namespace EntraArm.Tests.Engine;

/// <summary>Golden-file tests over tests/fixtures/whatif (docs/whatif.md): the text and JSON renderings of every scenario, byte for byte.</summary>
public sealed class WhatIfGoldenTests
{
    private const string Secret = "Hunter2-Secret-Value!";

    public static TheoryData<string> Scenarios()
    {
        var data = new TheoryData<string>();
        foreach (var scenario in WhatIfScenario.All())
        {
            data.Add(scenario.Name);
        }

        return data;
    }

    /// <summary>Compares two renderings line by line so a failure names the first differing line instead of dumping both files.</summary>
    private static void AssertSameLines(string expected, string actual, string what)
    {
        var expectedLines = expected.Split('\n');
        var actualLines = actual.Split('\n');
        for (int i = 0; i < Math.Min(expectedLines.Length, actualLines.Length); i++)
        {
            if (!string.Equals(expectedLines[i], actualLines[i], StringComparison.Ordinal))
            {
                Assert.Fail($"{what}: line {i + 1} differs.\n  expected: {expectedLines[i]}\n  actual:   {actualLines[i]}");
            }
        }

        Assert.True(expectedLines.Length == actualLines.Length, $"{what}: expected {expectedLines.Length} lines, got {actualLines.Length}.");
    }

    [Fact]
    public void Manifest_ListsTheDocumentedScenarios_AndEveryFileExists()
    {
        var all = WhatIfScenario.All();

        Assert.Equal(["create-incremental", "modify-incremental", "modify-resource-id-only", "delete-complete", "detach-incremental"], all.Select(s => s.Name).ToList());
        foreach (var scenario in all)
        {
            Assert.True(System.IO.File.Exists(scenario.PathOf(scenario.Template)), scenario.Template);
            Assert.True(System.IO.File.Exists(scenario.PathOf(scenario.Seed)), scenario.Seed);
            Assert.True(System.IO.File.Exists(scenario.PathOf(scenario.ExpectedText)), scenario.ExpectedText);
            Assert.True(System.IO.File.Exists(scenario.PathOf(scenario.ExpectedJson)), scenario.ExpectedJson);
            if (scenario.State is not null)
            {
                Assert.True(System.IO.File.Exists(scenario.PathOf(scenario.State)), scenario.State);
            }
        }

        Assert.Equal(ResultFormat.ResourceIdOnly, all.Single(s => s.Name == "modify-resource-id-only").ResultFormat);
        Assert.Equal(EntraArm.Engine.Planning.DeploymentMode.Complete, all.Single(s => s.Name == "delete-complete").Mode);
    }

    [Fact]
    public void GoldenFiles_AreUtf8WithoutBomAndLf()
    {
        foreach (var scenario in WhatIfScenario.All())
        {
            foreach (var file in new[] { scenario.ExpectedText, scenario.ExpectedJson })
            {
                byte[] bytes = System.IO.File.ReadAllBytes(scenario.PathOf(file));
                Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, file + " has a BOM");
                Assert.DoesNotContain(13, bytes.Select(b => (int)b));
            }
        }
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task Text_MatchesTheGoldenFile(string name)
    {
        var run = await WhatIfScenario.Find(name).PlanAsync();

        AssertSameLines(run.Scenario.ReadExpectedText(), run.RenderText(), name + ".expected.txt");
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task Json_MatchesTheGoldenFile(string name)
    {
        var run = await WhatIfScenario.Find(name).PlanAsync();

        AssertSameLines(run.Scenario.ReadExpectedJson(), run.RenderJson(), name + ".expected.json");
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task WhatIf_WritesNothing_AndNeverLeaksASecret(string name)
    {
        var run = await WhatIfScenario.Find(name).PlanAsync();

        Assert.Empty(run.Client.Writes);
        Assert.DoesNotContain(Secret, run.RenderText(), StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, run.RenderJson(), StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, WhatIfJson.Serialize(WhatIfJson.Build(run.Report), prettyPrint: false), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("create-incremental", true, 4, 0, 0, 0, 1)]
    [InlineData("modify-incremental", true, 1, 4, 0, 1, 0)]
    [InlineData("modify-resource-id-only", true, 1, 4, 0, 1, 0)]
    [InlineData("delete-complete", true, 0, 0, 1, 1, 0)]
    [InlineData("detach-incremental", false, 0, 0, 0, 1, 1)]
    public async Task Summary_AndHasChanges_FollowTheScenario(string name, bool hasChanges, int create, int modify, int delete, int noChange, int ignore)
    {
        var report = (await WhatIfScenario.Find(name).PlanAsync()).Report;

        Assert.Equal(hasChanges, report.HasChanges);
        Assert.Equal((create, modify, delete, noChange, ignore), (report.Summary.Create, report.Summary.Modify, report.Summary.Delete, report.Summary.NoChange, report.Summary.Ignore));
    }

    [Fact]
    public async Task ModifyScenario_CoversEveryMarkerAndTheEdgeCounts()
    {
        var run = await WhatIfScenario.Find("modify-incremental").PlanAsync();

        Assert.Equal(["~", "~", "+", "=", "~!", "~?"], run.Report.Rows.Select(r => r.Marker).ToList());
        Assert.Equal(new EdgeSummary(3, 1), run.Report.Edges);
        Assert.Equal(1, run.Report.Summary.Unsupported);
        Assert.Equal(["Application.ReadWrite.All", "Group.ReadWrite.All", "GroupMember.ReadWrite.All", "User.ReadWrite.All"], run.Report.RequiredPermissions);

        var legacy = run.Row("/providers/Microsoft.Entra/groups/contoso-legacy");
        Assert.True(legacy.IsUnsupported);
        Assert.Contains("Immutable property properties.mailNickname", legacy.UnsupportedReason);
        Assert.DoesNotContain(legacy.UnsupportedReason!, legacy.Reasons);

        var user = run.Row("/providers/Microsoft.Entra/users/bg-orders@contoso.com");
        Assert.True(user.IsUnknown);
        Assert.Equal(new EdgeSummary(2, 0), user.Edges);
        Assert.True(user.IsWrite);
        var admins = run.Row("/providers/Microsoft.Entra/groups/contoso-dev-orders-admins");
        Assert.Equal(new EdgeSummary(1, 1), admins.Edges);
        Assert.Equal(ChangeType.NoChange, run.Row("/providers/Microsoft.Entra/groups/contoso-dev-orders-ops").Type);
        Assert.False(run.Row("/providers/Microsoft.Entra/groups/contoso-dev-orders-ops").IsWrite);
    }

    [Fact]
    public async Task CreateScenario_MasksThePassword_AndMarksTheSymbolicServicePrincipal()
    {
        var run = await WhatIfScenario.Find("create-incremental").PlanAsync();

        var user = Assert.Single(run.Report.Rows, r => r.ResourceType == "Microsoft.Entra/users");
        Assert.Equal("***", user.After!["properties"]!["passwordProfile"]!["password"]!.GetValue<string>());
        var sp = Assert.Single(run.Report.Rows, r => r.ResourceType == "Microsoft.Entra/servicePrincipals");
        Assert.Equal("+?", sp.Marker);
        Assert.True(sp.Change.IsSymbolicId);
        Assert.Contains(sp.Reasons, r => r.Contains("the name is only known once", StringComparison.Ordinal));
        Assert.Contains(run.Report.Rows, r => r.Type == ChangeType.Ignore && r.Reasons.Contains("condition evaluated to false"));
    }

    [Fact]
    public async Task DeleteAndDetachScenarios_AreTheSameStateInTwoModes()
    {
        var delete = await WhatIfScenario.Find("delete-complete").PlanAsync();
        var detach = await WhatIfScenario.Find("detach-incremental").PlanAsync();

        var deleted = Assert.Single(delete.Report.Rows, r => r.IsModePass);
        var detached = Assert.Single(detach.Report.Rows, r => r.IsModePass);
        Assert.Equal(deleted.ResourceId, detached.ResourceId);
        Assert.Equal(ChangeType.Delete, deleted.Type);
        Assert.Equal(ChangeType.Ignore, detached.Type);
        Assert.NotNull(deleted.Before);
        Assert.Null(deleted.After);
        Assert.Null(deleted.Instance);
        Assert.False(deleted.Before!.ContainsKey("apiVersion"));
        Assert.Contains(delete.Report.Diagnostics, d => d.Code == EntraArm.Engine.EngineDiagnosticCodes.DestructiveDelete);
        Assert.Contains(detach.Report.Diagnostics, d => d.Code == EntraArm.Engine.EngineDiagnosticCodes.ResourceDetached);
    }
}
