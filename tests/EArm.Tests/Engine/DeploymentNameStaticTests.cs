using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using static EArm.Tests.Engine.NestedDeploymentTests;

namespace EArm.Tests.Engine;

/// <summary>
/// <c>deployment().name</c> is the real deployment name (<c>-n</c>, or the derived default) wherever the planner evaluates a template statically:
/// the main template's names, the ids <c>resourceId()</c> and <c>dependsOn</c> resolve against, and an outer-scope nested template (which sees
/// the parent's deployment). It used to be <c>validate</c>, the name of the offline context, so such names got wrong planned ids.
/// </summary>
public sealed class DeploymentNameStaticTests
{
    private static string GroupId(string name) => "/providers/Microsoft.Entra/groups/" + name;

    [Fact]
    public async Task A_name_built_from_deployment_name_gets_the_real_deployment_name()
    {
        var document = Templates.Parse($"\"resources\": [ {Group("[concat('g-', deployment().name)]")} ]");

        var plan = await new EngineHost().ExpandAsync(document);

        var group = Assert.Single(plan.Instances);
        Assert.Equal(GroupId("g-" + EngineHost.DeploymentName), group.PlannedId.Id);
    }

    [Fact]
    public async Task A_dependency_on_a_name_built_from_deployment_name_resolves_to_that_resource()
    {
        var document = Templates.Parse($$"""
            "resources": [
              {{Group("[concat('g-', deployment().name)]")}},
              {{Group("reader", extra: ", \"dependsOn\": [ \"[resourceId('Microsoft.Entra/groups', concat('g-', deployment().name))]\" ]")}}
            ]
            """);

        var plan = await new EngineHost().ExpandAsync(document);

        var reader = EngineHost.Instance(plan, GroupId("reader"));
        Assert.Contains(plan.Graph.DependenciesOf(reader), i => i.PlannedId.Id == GroupId("g-" + EngineHost.DeploymentName));
    }

    [Fact]
    public async Task Another_deployment_name_gives_another_id()
    {
        var host = new EngineHost();
        var document = Templates.Parse($"\"resources\": [ {Group("[concat('g-', deployment().name)]")} ]");

        var plan = await host.ExpandAsync(document, options: host.Options(deploymentName: "release-7"));

        Assert.Equal(GroupId("g-release-7"), Assert.Single(plan.Instances).PlannedId.Id);
    }

    [Fact]
    public async Task An_outer_scope_nested_template_sees_the_parent_deployment_name()
    {
        string inner = $"\"resources\": [ {Group("[concat('g-', deployment().name)]")} ]";
        var document = Templates.Parse($"\"resources\": [ {Deployment("child", Inline(inner))} ]");

        var plan = await new EngineHost().ExpandAsync(document);

        Assert.Contains(plan.Instances, i => i.PlannedId.Id == GroupId("g-" + EngineHost.DeploymentName));
        Assert.DoesNotContain(plan.Instances, i => i.PlannedId.Id.EndsWith("-validate", StringComparison.Ordinal));
    }
}
