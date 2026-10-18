using System.Collections.Immutable;
using System.Linq;
using EArm.Core.Diagnostics;
using EArm.Core.Json;
using EArm.Expressions.Evaluation;
using EArm.Expressions.Validation;
using Xunit;

namespace EArm.Tests;

/// <summary>
/// <see cref="ExpressionValidator.Analyze(EArm.Core.TemplateDocument, OuterTemplateScope?)"/> for the template of an outer-scope nested deployment:
/// the deployment's copy iteration and the parent's resources are visible, and <c>reference()</c> is refused in the outputs.
/// </summary>
public sealed class OuterTemplateScopeTests
{
    private static readonly ExpressionValidator Validator = new();

    private static string Group(string name, string? description = null)
    {
        string described = description is null ? string.Empty : ", \"description\": \"" + description + "\"";
        return "{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"" + name + "\", \"properties\": { \"displayName\": \"x\", \"mailEnabled\": false, \"securityEnabled\": true" + described + " } }";
    }

    private static TemplateResourceIndex ParentResources()
    {
        return Validator.Analyze(Expr.ParseBody("\"resources\": [ " + Group("parent-g") + " ]")).Static.Index;
    }

    private static OuterTemplateScope Outer(long? index)
    {
        var stack = index is null ? ImmutableStack<CopyFrame>.Empty : ImmutableStack.Create(new CopyFrame("teams", index.Value, 3));
        return new OuterTemplateScope(stack, ParentResources());
    }

    [Fact]
    public void CopyIndex_is_legal_and_concrete_when_the_deployment_is_an_iteration()
    {
        var document = Expr.ParseBody("\"resources\": [ " + Group("[format('team-{0}', copyIndex())]") + " ], \"outputs\": { \"i\": { \"type\": \"int\", \"value\": \"[copyIndex('teams', 1)]\" } }");

        var analysis = Validator.Analyze(document, Outer(2));

        Assert.DoesNotContain(analysis.Diagnostics, d => d.IsError);
        Assert.Equal("team-2", analysis.Static.Resources[0].Id?.Name);
        Assert.Equal(3, analysis.Static.Outputs["i"].Value?.GetValue<long>());
    }

    [Fact]
    public void CopyIndex_is_still_refused_without_a_deployment_copy_loop()
    {
        var document = Expr.ParseBody("\"resources\": [ " + Group("[format('team-{0}', copyIndex())]") + " ]");

        var analysis = Validator.Analyze(document, Outer(null));

        Assert.Contains(analysis.Diagnostics, d => d.Code == DiagnosticCodes.CopyIndexOutsideCopy && d.Path == JsonPath.Parse("resources[0].name"));
    }

    [Fact]
    public void Reference_finds_a_parent_resource_by_name_after_the_template_own()
    {
        var document = Expr.ParseBody("\"resources\": [ " + Group("own-g") + ", " + Group("inside", "[concat(reference('parent-g').id, reference('own-g').id)]") + " ]");

        var analysis = Validator.Analyze(document, Outer(null));

        Assert.DoesNotContain(analysis.Diagnostics, d => d.IsError);
        var targets = analysis.Static.Resources[1].References.Select(r => r.Target?.Name).ToList();
        Assert.Contains("parent-g", targets);
        Assert.Contains("own-g", targets);

        // Without the outer scope the parent's resource is not part of the template.
        Assert.Contains(Validator.Analyze(document).Diagnostics, d => d.Code == DiagnosticCodes.ReferenceTargetNotFound);
    }

    [Fact]
    public void Reference_is_refused_in_the_outputs()
    {
        var document = Expr.ParseBody("\"resources\": [ " + Group("own-g") + " ], \"outputs\": { \"id\": { \"type\": \"string\", \"value\": \"[reference('own-g').id]\" } }");

        var analysis = Validator.Analyze(document, Outer(null));

        var error = Assert.Single(analysis.Diagnostics, d => d.Code == DiagnosticCodes.ReferenceNotAllowedHere);
        Assert.Equal(JsonPath.Parse("outputs.id.value"), error.Path);
        Assert.DoesNotContain(Validator.Analyze(document).Diagnostics, d => d.Code == DiagnosticCodes.ReferenceNotAllowedHere);
    }
}
