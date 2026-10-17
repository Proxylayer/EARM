using System;
using System.Linq;
using System.Threading.Tasks;
using EArm.Core.Diagnostics;
using EArm.Core.Secrets;
using EArm.Engine.Execution;
using EArm.Engine.Planning;
using Xunit;
using static EArm.Tests.Engine.NestedDeploymentTests;

namespace EArm.Tests.Engine;

/// <summary>
/// Nested deployments with the other template features: languageVersion 2.0 symbolic names and <c>references()</c>, user-defined functions,
/// parameter types, secure outputs, <c>deployment()</c> and copy loops inside an inner-scope template.
/// </summary>
public sealed class NestedDeploymentInteractionTests
{
    private static string GroupId(string name) => "/providers/Microsoft.Entra/groups/" + name;

    [Fact]
    public async Task A_symbolic_deployment_is_read_with_reference_of_its_symbolic_name()
    {
        string inner = $$"""
            "resources": [ {{Group("sym-inner")}} ],
            "outputs": { "label": { "type": "string", "value": "from-inner" } }
            """;
        var document = Templates.Parse($$"""
            "languageVersion": "2.0",
            "resources": {
              "net": {{Deployment("network", Inline(inner))}},
              "reader": {{Group("reader", "[reference('net').outputs.label.value]", ", \"dependsOn\": [ \"net\" ]")}}
            }
            """);

        var host = new EngineHost();
        var (plan, result) = await host.CreateAsync(document);

        Assert.Equal(DeploymentStatus.Succeeded, result.Status);
        var reader = EngineHost.Instance(plan, GroupId("reader"));
        Assert.Contains(plan.Graph.DependenciesOf(reader), i => i.IsDeployment);
        Assert.Equal("from-inner", host.Directory.Find("groups", "displayName", "reader")?["description"]?.GetValue<string>());
    }

    [Fact]
    public async Task References_collects_the_outputs_of_a_deployment_loop()
    {
        string inner = """
            "parameters": { "i": { "type": "int" } },
            "resources": [],
            "outputs": { "n": { "type": "int", "value": "[parameters('i')]" } }
            """;
        var document = Templates.Parse($$"""
            "languageVersion": "2.0",
            "resources": {
              "loop": {{Deployment("[format('d{0}', copyIndex())]", Inline(inner, "inner", "{ \"i\": { \"value\": \"[copyIndex()]\" } }"), ", \"copy\": { \"name\": \"loop\", \"count\": 2 }")}},
              "reader": {{Group("reader", "[string(map(references('loop'), lambda('d', lambdaVariables('d').outputs.n.value)))]")}}
            }
            """);

        var host = new EngineHost();
        var (_, result) = await host.CreateAsync(document);

        Assert.Equal(DeploymentStatus.Succeeded, result.Status);
        Assert.Equal("[0,1]", host.Directory.Find("groups", "displayName", "reader")?["description"]?.GetValue<string>());
    }

    [Fact]
    public async Task Symbolic_names_resolve_within_their_own_template()
    {
        // The nested template reuses the parent's symbolic name 'g' for another resource: each template sees its own.
        string inner = $$"""
            "languageVersion": "2.0",
            "resources": { "g": {{Group("inner-g")}}, "h": {{Group("inner-h", "[reference('g').displayName]", ", \"dependsOn\": [ \"g\" ]")}} }
            """;
        var document = Templates.Parse($$"""
            "languageVersion": "2.0",
            "resources": { "g": {{Group("outer-g")}}, "nested": {{Deployment("nested", Inline(inner))}} }
            """);

        var plan = await new EngineHost().ExpandAsync(document);

        var h = EngineHost.Instance(plan, GroupId("inner-h"));
        Assert.Equal([GroupId("inner-g")], plan.Graph.DependenciesOf(h).Select(i => i.PlannedId.Id));
    }

    [Fact]
    public async Task User_defined_functions_belong_to_their_template()
    {
        string functions = """
            "functions": [ { "namespace": "names", "members": { "make": { "parameters": [ { "name": "s", "type": "string" } ], "output": { "type": "string", "value": "[concat('inner-', parameters('s'))]" } } } } ]
            """;
        string inner = functions + ", \"resources\": [ " + Group("[names.make('x')]") + " ]";
        var document = Templates.Parse($$"""
            "functions": [ { "namespace": "names", "members": { "make": { "parameters": [ { "name": "s", "type": "string" } ], "output": { "type": "string", "value": "[concat('outer-', parameters('s'))]" } } } } ],
            "resources": [ {{Group("[names.make('y')]")}}, {{Deployment("nested", Inline(inner, "inner"))}} ]
            """);

        var plan = await new EngineHost().ExpandAsync(document);

        Assert.Contains(plan.Instances, i => i.PlannedId.Id == GroupId("outer-y"));
        Assert.Contains(plan.Instances, i => i.PlannedId.Id == GroupId("inner-x"));
    }

    [Fact]
    public async Task A_passed_value_of_the_wrong_type_is_refused()
    {
        string inner = $$"""
            "parameters": { "count": { "type": "int" } },
            "resources": [ {{Group("[format('g{0}', parameters('count'))]")}} ]
            """;
        var document = Templates.Parse($"\"resources\": [ {Deployment("nested", Inline(inner, "inner", "{ \"count\": { \"value\": \"not a number\" } }"))} ]");

        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => new EngineHost().ExpandAsync(document));

        Assert.Contains(ex.Diagnostics, d => d.Code == DiagnosticCodes.ParameterValueTypeMismatch);
    }

    [Fact]
    public async Task A_parameter_the_parent_does_not_pass_is_an_error()
    {
        string inner = $$"""
            "parameters": { "name": { "type": "string" } },
            "resources": [ {{Group("[parameters('name')]")}} ]
            """;
        var document = Templates.Parse($"\"resources\": [ {Deployment("nested", Inline(inner, "inner"))} ]");

        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => new EngineHost().ExpandAsync(document));

        Assert.Contains(ex.Diagnostics, d => d.IsError && d.Code == DiagnosticCodes.MissingParameterValue && d.Message.Contains("nested deployment 'nested'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_value_for_an_undeclared_parameter_is_an_error()
    {
        var document = Templates.Parse($"\"resources\": [ {Deployment("nested", Inline($"\"resources\": [ {Group("g")} ]", "inner", "{ \"nope\": { \"value\": 1 } }"))} ]");

        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => new EngineHost().ExpandAsync(document));

        Assert.Contains(ex.Diagnostics, d => d.Code == DiagnosticCodes.UnknownParameter && d.Path?.ToString() == "resources[0].properties.parameters.nope");
    }

    [Fact]
    public async Task A_secure_output_stays_masked_in_the_parent()
    {
        string inner = """
            "parameters": { "secret": { "type": "securestring" } },
            "resources": [],
            "outputs": { "token": { "type": "securestring", "value": "[parameters('secret')]" } }
            """;
        var document = Templates.Parse($$"""
            "parameters": { "secret": { "type": "securestring" } },
            "resources": [ {{Deployment("nested", Inline(inner, "inner", "{ \"secret\": { \"value\": \"[parameters('secret')]\" } }"))}} ],
            "outputs": { "fromNested": { "type": "securestring", "value": "[reference('nested').outputs.token.value]" } }
            """, ("secret", "s3cr3t-value"));

        var host = new EngineHost();
        var (plan, result) = await host.CreateAsync(document);

        Assert.Equal(DeploymentStatus.Succeeded, result.Status);
        Assert.True(plan.Resolver.TryGetLive(plan.Instances.Single(i => i.IsDeployment).Id, out var live, out _));
        Assert.True(SecretJson.IsSecret(live["outputs"]!["token"]!["value"]));
        Assert.DoesNotContain("s3cr3t-value", result.Outputs.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal("s3cr3t-value", result.SecureOutputs["fromNested"]!["value"]!.GetValue<string>());
    }

    [Fact]
    public async Task Deployment_returns_the_nested_deployment_in_the_inner_scope()
    {
        string inner = $"\"resources\": [ {Group("[concat('g-', deployment().name)]")} ]";
        var document = Templates.Parse($"\"resources\": [ {Deployment("child", Inline(inner, "inner"))} ]");

        var plan = await new EngineHost().ExpandAsync(document);

        Assert.Contains(plan.Instances, i => i.PlannedId.Id == GroupId("g-child"));
    }

    [Fact]
    public async Task A_copy_loop_inside_an_inner_scope_template_uses_its_own_index()
    {
        string inner = $$"""
            "parameters": { "prefix": { "type": "string" } },
            "resources": [ {{Group("[format('{0}-{1}', parameters('prefix'), copyIndex())]", extra: ", \"copy\": { \"name\": \"groups\", \"count\": 2 }")}} ]
            """;
        var document = Templates.Parse($$"""
            "resources": [ {{Deployment("[format('d{0}', copyIndex())]", Inline(inner, "inner", "{ \"prefix\": { \"value\": \"[format('p{0}', copyIndex())]\" } }"), ", \"copy\": { \"name\": \"deployments\", \"count\": 2 }")}} ]
            """);

        var plan = await new EngineHost().ExpandAsync(document);

        Assert.Equal([GroupId("p0-0"), GroupId("p0-1"), GroupId("p1-0"), GroupId("p1-1")], plan.Instances.Where(i => !i.IsDeployment).Select(i => i.PlannedId.Id).OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_copy_loop_inside_an_outer_scope_template_is_refused()
    {
        string inner = $"\"resources\": [ {Group("[format('g{0}', copyIndex())]", extra: ", \"copy\": { \"name\": \"groups\", \"count\": 2 }")} ]";
        var document = Templates.Parse($"\"resources\": [ {Deployment("nested", Inline(inner))} ]");

        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => new EngineHost().ExpandAsync(document));

        Assert.Contains(ex.Diagnostics, d => d.Code == DiagnosticCodes.InvalidNestedDeployment && d.Message.Contains("inner scope", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Outer_scope_in_a_language_version_2_template_is_refused()
    {
        var document = Templates.Parse($$"""
            "languageVersion": "2.0",
            "resources": { "nested": {{Deployment("nested", Inline($"\"resources\": [ {Group("g")} ]", "outer"))}} }
            """);

        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => new EngineHost().ExpandAsync(document));

        Assert.Contains(ex.Diagnostics, d => d.Code == DiagnosticCodes.InvalidNestedDeployment && d.Message.Contains("blocks 'outer'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_existing_resource_in_a_nested_template_is_read_not_written()
    {
        string inner = $$"""
            "languageVersion": "2.0",
            "resources": {
              "ops": { "type": "Microsoft.Entra/groups", "apiVersion": "{{Api}}", "name": "ops", "existing": true },
              "copy": {{Group("ops-copy", "[reference('ops').id]")}}
            }
            """;
        var document = Templates.Parse($"\"resources\": [ {Deployment("nested", Inline(inner, "inner"))} ]");
        var host = new EngineHost();
        var ops = host.Directory.AddGroup("ops", "ops", g => { g["mailEnabled"] = false; g["securityEnabled"] = true; });

        var (_, result) = await host.CreateAsync(document);

        Assert.Equal(DeploymentStatus.Succeeded, result.Status);
        Assert.Equal(ops["id"]!.GetValue<string>(), host.Directory.Find("groups", "displayName", "ops-copy")?["description"]?.GetValue<string>());
    }

    [Fact]
    public async Task One_nested_deployment_passes_the_output_of_another_as_a_parameter()
    {
        string producer = """
            "resources": [],
            "outputs": { "value": { "type": "string", "value": "produced" } }
            """;
        string consumer = $$"""
            "parameters": { "label": { "type": "string" } },
            "resources": [ {{Group("consumer-g", "[parameters('label')]")}} ]
            """;
        var document = Templates.Parse($$"""
            "resources": [
              {{Deployment("producer", Inline(producer, "inner"))}},
              {{Deployment("consumer", Inline(consumer, "inner", "{ \"label\": { \"value\": \"[reference('producer').outputs.value.value]\" } }"))}}
            ]
            """);

        var host = new EngineHost();
        var (plan, result) = await host.CreateAsync(document);

        Assert.Equal(DeploymentStatus.Succeeded, result.Status);
        var consumerGroup = EngineHost.Instance(plan, GroupId("consumer-g"));
        Assert.Contains(plan.Graph.DependenciesOf(consumerGroup), i => i.IsDeployment && i.Id.Name == "producer");
        Assert.Equal("produced", host.Directory.Find("groups", "displayName", "consumer-g")?["description"]?.GetValue<string>());
    }
}
