using System;
using System.Linq;
using System.Threading.Tasks;
using EArm.Core.Diagnostics;
using EArm.Core.Types;
using EArm.Engine.Execution;
using EArm.Engine.Permissions;
using EArm.Engine.Planning;
using Xunit;
using static EArm.Tests.Engine.NestedDeploymentTests;

namespace EArm.Tests.Engine;

/// <summary>
/// A nested template in the outer scope is evaluated in the parent's context, as in ARM: <c>copyIndex()</c> is the deployment's own iteration,
/// <c>reference()</c> finds the parent's resources by name, and its outputs may not use <c>reference()</c>.
/// </summary>
public sealed class NestedOuterScopeTests
{
    private static string GroupId(string name) => "/providers/Microsoft.Entra/groups/" + name;

    private static string[] Ids(DeploymentPlan plan) => plan.Instances.Where(i => !i.IsDeployment).Select(i => i.PlannedId.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();

    private static string OuterLoop(string body, int count = 2)
    {
        return Deployment("[format('nested-{0}', copyIndex())]", Inline(body), $", \"copy\": {{ \"name\": \"teams\", \"count\": {count} }}");
    }

    [Fact]
    public async Task CopyIndex_in_an_outer_scope_template_is_the_iteration_of_the_deployment()
    {
        var document = Templates.Parse($$"""
            "resources": [ {{OuterLoop($"\"resources\": [ {Group("[format('team-{0}', copyIndex())]", "[format('n{0}', copyIndex('teams', 1))]")} ]")}} ]
            """);

        var plan = await new EngineHost().ExpandAsync(document);

        Assert.Equal([GroupId("team-0"), GroupId("team-1")], Ids(plan));
        Assert.Equal("nested-1", EngineHost.Instance(plan, GroupId("team-1")).DeploymentPath);
        Assert.Equal("n2", EngineHost.Evaluated(plan, GroupId("team-1")).Properties!["description"]!.GetValue<string>());
    }

    [Fact]
    public async Task CopyIndex_in_the_outputs_of_an_outer_scope_template_is_the_iteration_of_the_deployment()
    {
        string body = $$"""
            "resources": [ {{Group("[format('team-{0}', copyIndex())]")}} ],
            "outputs": { "index": { "type": "string", "value": "[format('i{0}', copyIndex())]" } }
            """;
        var document = Templates.Parse($$"""
            "resources": [ {{OuterLoop(body)}}, {{Group("reader", "[reference('nested-1').outputs.index.value]")}} ]
            """);

        var host = new EngineHost();
        var (_, result) = await host.CreateAsync(document);

        Assert.Equal(DeploymentStatus.Succeeded, result.Status);
        Assert.Equal("i1", host.Directory.Find("groups", "displayName", "reader")?["description"]?.GetValue<string>());
    }

    [Fact]
    public async Task CopyIndex_in_an_outer_scope_template_needs_a_copy_loop_on_the_deployment()
    {
        var document = Templates.Parse($$"""
            "resources": [ {{Deployment("nested", Inline($"\"resources\": [ {Group("[format('team-{0}', copyIndex())]")} ]"))}} ]
            """);

        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => new EngineHost().ExpandAsync(document));

        Assert.Contains(ex.Diagnostics, d => d.Code == DiagnosticCodes.CopyIndexOutsideCopy && d.Path?.ToString() == "resources[0].properties.template.resources[0].name");
    }

    [Fact]
    public async Task An_inner_scope_template_still_does_not_see_the_iteration_of_the_deployment()
    {
        var document = Templates.Parse($$"""
            "resources": [ {{Deployment("[format('nested-{0}', copyIndex())]", Inline($"\"resources\": [ {Group("[format('team-{0}', copyIndex())]")} ]", "inner"), ", \"copy\": { \"name\": \"teams\", \"count\": 2 }")}} ]
            """);

        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => new EngineHost().ExpandAsync(document));

        Assert.Contains(ex.Diagnostics, d => d.Code == DiagnosticCodes.CopyIndexOutsideCopy);
    }

    [Fact]
    public async Task Reference_in_an_outer_scope_template_finds_a_parent_resource_by_name_and_waits_for_it()
    {
        var document = Templates.Parse($$"""
            "resources": [
              {{Group("parent-g")}},
              {{Deployment("nested", Inline($"\"resources\": [ {Group("inside", "[reference('parent-g').id]")} ]"))}}
            ]
            """);

        var host = new EngineHost();
        var (plan, result) = await host.CreateAsync(document);

        Assert.Equal(DeploymentStatus.Succeeded, result.Status);
        var inside = EngineHost.Instance(plan, GroupId("inside"));
        Assert.Contains(plan.Graph.DependenciesOf(inside), i => i.PlannedId.Id == GroupId("parent-g"));
        var parent = host.Directory.Find("groups", "displayName", "parent-g");
        Assert.Equal(parent?["id"]?.GetValue<string>(), host.Directory.Find("groups", "displayName", "inside")?["description"]?.GetValue<string>());
    }

    [Fact]
    public async Task Reference_in_an_outer_scope_template_finds_an_iteration_of_a_parent_copy_loop()
    {
        var document = Templates.Parse($$"""
            "resources": [
              {{Group("[format('src-{0}', copyIndex())]", extra: ", \"copy\": { \"name\": \"sources\", \"count\": 2 }")}},
              {{OuterLoop($"\"resources\": [ {Group("[format('team-{0}', copyIndex())]", "[reference(format('src-{0}', copyIndex())).id]")} ]")}}
            ]
            """);

        var plan = await new EngineHost().ExpandAsync(document);

        var team1 = EngineHost.Instance(plan, GroupId("team-1"));
        Assert.Contains(plan.Graph.DependenciesOf(team1), i => i.PlannedId.Id == GroupId("src-1"));
        Assert.DoesNotContain(plan.Graph.DependenciesOf(team1), i => i.PlannedId.Id == GroupId("src-0"));
    }

    [Fact]
    public async Task Reference_in_an_inner_scope_template_still_does_not_find_a_parent_resource()
    {
        var document = Templates.Parse($$"""
            "resources": [
              {{Group("parent-g")}},
              {{Deployment("nested", Inline($"\"resources\": [ {Group("inside", "[reference('parent-g').id]")} ]", "inner"))}}
            ]
            """);

        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => new EngineHost().ExpandAsync(document));

        Assert.Contains(ex.Diagnostics, d => d.Code == DiagnosticCodes.ReferenceTargetNotFound);
    }

    [Theory]
    [InlineData("[reference('inside').id]")]
    [InlineData("[reference('parent-g').id]")]
    public async Task Reference_in_the_outputs_of_an_outer_scope_template_is_refused(string output)
    {
        var document = Templates.Parse($$"""
            "resources": [
              {{Group("parent-g")}},
              {{Deployment("nested", Inline($"\"resources\": [ {Group("inside")} ], \"outputs\": {{ \"id\": {{ \"type\": \"string\", \"value\": \"{output}\" }} }}"))}}
            ]
            """);

        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => new EngineHost().ExpandAsync(document));

        var error = Assert.Single(ex.Diagnostics, d => d.Code == DiagnosticCodes.ReferenceNotAllowedHere);
        Assert.Equal("resources[1].properties.template.outputs.id.value", error.Path?.ToString());
        Assert.Contains("outer scope", error.Message, StringComparison.Ordinal);

        // Offline validation reports it too.
        var expansion = OfflineTemplateExpansion.Run(document, ResourceTypeCatalog.Default);
        Assert.Contains(expansion.Errors, d => d.Code == DiagnosticCodes.ReferenceNotAllowedHere);
    }

    [Fact]
    public async Task Reference_in_the_outputs_of_an_inner_scope_template_is_still_allowed()
    {
        var document = Templates.Parse($$"""
            "resources": [ {{Deployment("nested", Inline($"\"resources\": [ {Group("inside")} ], \"outputs\": {{ \"id\": {{ \"type\": \"string\", \"value\": \"[reference('inside').id]\" }} }}", "inner"))}} ]
            """);

        var plan = await new EngineHost().ExpandAsync(document);

        Assert.Equal([GroupId("inside")], Ids(plan));
    }
}
