using System;
using System.Linq;
using System.Threading.Tasks;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Engine;
using EArm.Engine.Execution;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using Xunit;

namespace EArm.Tests.Engine;

/// <summary>
/// Nested deployments (<c>Microsoft.Resources/deployments</c> with an inline <c>template</c>): outer and inner expression scope, parameters,
/// outputs read back with <c>reference()</c>, dependencies on and from a deployment, copy loops, nesting, ids across templates.
/// </summary>
public sealed class NestedDeploymentTests
{
    internal const string Api = "2026-10-01-preview";

    internal static string Group(string name, string? description = null, string? extra = null)
    {
        string described = description is null ? string.Empty : $", \"description\": \"{description}\"";
        return $$"""{ "type": "Microsoft.Entra/groups", "apiVersion": "{{Api}}", "name": "{{name}}"{{extra ?? string.Empty}}, "properties": { "displayName": "{{name}}", "mailEnabled": false, "securityEnabled": true{{described}} } }""";
    }

    internal static string Deployment(string name, string properties, string? extra = null)
    {
        return $$"""{ "type": "Microsoft.Resources/deployments", "apiVersion": "2025-04-01", "name": "{{name}}"{{extra ?? string.Empty}}, "properties": {{properties}} }""";
    }

    internal static string Inline(string body, string? scope = null, string? parameters = null)
    {
        string scoped = scope is null ? string.Empty : $", \"expressionEvaluationOptions\": {{ \"scope\": \"{scope}\" }}";
        string passed = parameters is null ? string.Empty : $", \"parameters\": {parameters}";
        return $$"""{ "mode": "Incremental"{{scoped}}{{passed}}, "template": { "$schema": "https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#", "contentVersion": "1.0.0.0", {{body}} } }""";
    }

    private static string GroupId(string name) => "/providers/Microsoft.Entra/groups/" + name;

    private static string[] Ids(DeploymentPlan plan) => plan.Instances.Where(i => !i.IsDeployment).Select(i => i.PlannedId.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();

    private static async Task<ValidationFailedException> ExpandFailsAsync(TemplateDocument document)
    {
        return await Assert.ThrowsAsync<ValidationFailedException>(() => new EngineHost().ExpandAsync(document));
    }

    [Fact]
    public async Task Outer_scope_sees_the_parent_parameters_and_variables()
    {
        var document = Templates.Parse($$"""
            "parameters": { "prefix": { "type": "string" } },
            "variables": { "suffix": "team" },
            "resources": [ {{Deployment("nested", Inline($"\"resources\": [ {Group("[concat(parameters('prefix'), '-', variables('suffix'))]")} ]"))}} ]
            """, ("prefix", "ops"));

        var plan = await new EngineHost().ExpandAsync(document);

        Assert.Equal([GroupId("ops-team")], Ids(plan));
        var group = plan.Instances.Single(i => !i.IsDeployment);
        Assert.Equal("nested", group.DeploymentPath);
    }

    [Fact]
    public async Task Inner_scope_uses_its_own_parameters_and_variables()
    {
        string inner = """
            "parameters": { "prefix": { "type": "string" } },
            "variables": { "suffix": "inner" },
            "resources": [ GROUP ]
            """.Replace("GROUP", Group("[concat(parameters('prefix'), '-', variables('suffix'))]"), StringComparison.Ordinal);
        var document = Templates.Parse($$"""
            "parameters": { "prefix": { "type": "string" } },
            "variables": { "suffix": "parent" },
            "resources": [ {{Deployment("nested", Inline(inner, "inner", "{ \"prefix\": { \"value\": \"[concat(parameters('prefix'), '2')]\" } }"))}} ]
            """, ("prefix", "ops"));

        var plan = await new EngineHost().ExpandAsync(document);

        Assert.Equal([GroupId("ops2-inner")], Ids(plan));
    }

    [Fact]
    public async Task Inner_scope_does_not_see_the_parent_parameters()
    {
        var document = Templates.Parse($$"""
            "parameters": { "prefix": { "type": "string" } },
            "resources": [ {{Deployment("nested", Inline($"\"resources\": [ {Group("[parameters('prefix')]")} ]", "inner"))}} ]
            """, ("prefix", "ops"));

        var ex = await ExpandFailsAsync(document);

        var error = ex.Diagnostics.First(d => d.IsError && d.Code == DiagnosticCodes.UndefinedParameter);

        // The finding points at the place in the parent file that holds the inline template.
        Assert.Equal("resources[0].properties.template.resources[0].name", error.Path?.ToString());
        Assert.Equal("inline.entra.json", error.Source);
        Assert.NotNull(error.Location);
    }

    [Fact]
    public async Task Outputs_are_read_back_with_reference_and_order_the_reader_after_the_nested_resources()
    {
        string inner = $$"""
            "parameters": { "name": { "type": "string" } },
            "resources": [ {{Group("[parameters('name')]")}} ],
            "outputs": { "groupName": { "type": "string", "value": "[concat(parameters('name'), '-out')]" } }
            """;
        var document = Templates.Parse($$"""
            "resources": [
              {{Deployment("nested", Inline(inner, "inner", "{ \"name\": { \"value\": \"inner-g\" } }"))}},
              {{Group("reader", "[reference('nested').outputs.groupName.value]")}}
            ]
            """);

        var host = new EngineHost();
        var (plan, result) = await host.CreateAsync(document);

        Assert.Equal(DeploymentStatus.Succeeded, result.Status);
        var reader = EngineHost.Instance(plan, GroupId("reader"));
        var innerGroup = EngineHost.Instance(plan, GroupId("inner-g"));
        int Batch(ResourceInstance instance) => plan.Batches.Single(b => b.Instances.Contains(instance)).Index;
        Assert.True(Batch(reader) > Batch(innerGroup));
        Assert.Equal("inner-g-out", host.Directory.Find("groups", "displayName", "reader")?["description"]?.GetValue<string>());
    }

    [Fact]
    public async Task DependsOn_a_deployment_waits_for_every_resource_inside_it()
    {
        var document = Templates.Parse($$"""
            "resources": [
              {{Deployment("nested", Inline($"\"resources\": [ {Group("a")}, {Group("b")} ]"))}},
              {{Group("after", extra: ", \"dependsOn\": [ \"[resourceId('Microsoft.Resources/deployments', 'nested')]\" ]")}}
            ]
            """);

        var plan = await new EngineHost().ExpandAsync(document);

        var after = EngineHost.Instance(plan, GroupId("after"));
        Assert.Contains(plan.Graph.DependenciesOf(after), i => i.IsDeployment);
        var deployment = plan.Instances.Single(i => i.IsDeployment);
        Assert.Equal([GroupId("a"), GroupId("b")], plan.Graph.DependenciesOf(deployment).Select(i => i.PlannedId.Id).OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Resources_inside_a_deployment_wait_for_what_the_deployment_depends_on()
    {
        var document = Templates.Parse($$"""
            "resources": [
              {{Group("first")}},
              {{Deployment("nested", Inline($"\"resources\": [ {Group("inside")} ]"), ", \"dependsOn\": [ \"first\" ]")}}
            ]
            """);

        var plan = await new EngineHost().ExpandAsync(document);

        var inside = EngineHost.Instance(plan, GroupId("inside"));
        Assert.Contains(plan.Graph.DependenciesOf(inside), i => i.PlannedId.Id == GroupId("first"));
    }

    [Fact]
    public async Task A_copy_loop_on_a_deployment_expands_its_template_per_iteration()
    {
        string inner = $$"""
            "parameters": { "index": { "type": "int" } },
            "resources": [ {{Group("[format('g-{0}', parameters('index'))]")}} ]
            """;
        var document = Templates.Parse($$"""
            "resources": [ {{Deployment("[format('nested-{0}', copyIndex())]", Inline(inner, "inner", "{ \"index\": { \"value\": \"[copyIndex()]\" } }"), ", \"copy\": { \"name\": \"loop\", \"count\": 3 }")}} ]
            """);

        var plan = await new EngineHost().ExpandAsync(document);

        Assert.Equal([GroupId("g-0"), GroupId("g-1"), GroupId("g-2")], Ids(plan));
        Assert.Equal(["nested-0", "nested-1", "nested-2"], plan.Instances.Where(i => !i.IsDeployment).Select(i => i.DeploymentPath!).OrderBy(p => p, StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_deployment_inside_a_deployment_is_expanded_with_its_path()
    {
        string innermost = Deployment("level2", Inline($"\"resources\": [ {Group("deep")} ]", "inner"));
        var document = Templates.Parse($$"""
            "resources": [ {{Deployment("level1", Inline($"\"resources\": [ {innermost} ]", "inner"))}} ]
            """);

        var plan = await new EngineHost().ExpandAsync(document);

        var deep = EngineHost.Instance(plan, GroupId("deep"));
        Assert.Equal("level1/level2", deep.DeploymentPath);
        var level1 = plan.Instances.Single(i => i.IsDeployment && i.Id.Name == "level1");
        Assert.Contains(deep, plan.Graph.DependenciesOf(level1));
    }

    [Fact]
    public async Task Two_templates_declaring_the_same_object_are_a_duplicate()
    {
        var document = Templates.Parse($$"""
            "resources": [
              {{Group("same")}},
              {{Deployment("nested", Inline($"\"resources\": [ {Group("same")} ]"))}}
            ]
            """);

        var ex = await ExpandFailsAsync(document);

        Assert.Contains(ex.Diagnostics, d => d.Code == DiagnosticCodes.DuplicateResourceId && d.Message.Contains("nested deployment 'nested'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_false_condition_skips_the_template()
    {
        var document = Templates.Parse($$"""
            "resources": [ {{Deployment("nested", Inline($"\"resources\": [ {Group("never")} ]"), ", \"condition\": false")}} ]
            """);

        var plan = await new EngineHost().ExpandAsync(document);

        Assert.Empty(Ids(plan));
    }

    [Fact]
    public async Task Nesting_deeper_than_the_limit_is_refused()
    {
        string body = $"\"resources\": [ {Group("bottom")} ]";
        for (int level = 11; level >= 1; level--)
        {
            body = $"\"resources\": [ {Deployment($"level{level}", Inline(body, "inner"))} ]";
        }

        var ex = await ExpandFailsAsync(Templates.Parse(body));

        Assert.Contains(ex.Diagnostics, d => d.Code == EngineDiagnosticCodes.NestedDeploymentLimitExceeded);
    }

    [Fact]
    public async Task Mode_complete_on_a_nested_deployment_is_refused()
    {
        var document = Templates.Parse($$"""
            "resources": [ {{Deployment("nested", Inline($"\"resources\": [ {Group("g")} ]").Replace("\"Incremental\"", "\"Complete\"", StringComparison.Ordinal))}} ]
            """);

        var ex = await ExpandFailsAsync(document);

        Assert.Contains(ex.Diagnostics, d => d.Code == DiagnosticCodes.InvalidNestedDeployment && d.Message.Contains("Incremental", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_template_spec_id_is_refused()
    {
        var document = Templates.Parse($$"""
            "resources": [ {{Deployment("nested", "{ \"mode\": \"Incremental\", \"templateLink\": { \"id\": \"/subscriptions/x/resourceGroups/y/providers/Microsoft.Resources/templateSpecs/z/versions/1\" } }")}} ]
            """);

        var ex = await ExpandFailsAsync(document);

        Assert.Contains(ex.Diagnostics, d => d.Code == DiagnosticCodes.NestedDeploymentFeatureNotSupported && d.Message.Contains("template spec", StringComparison.Ordinal));
    }
}
