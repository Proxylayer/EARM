using System.Linq;
using EArm.Core.Diagnostics;
using Xunit;

namespace EArm.Tests;

/// <summary>
/// Review 2E: only names, conditions, copy and dependsOn used to be evaluated offline, so an undefined variable or parameter inside a resource's
/// <c>properties</c> passed <c>validate</c> and failed at deployment. Each expression in the properties is now evaluated in validate mode and the
/// authoring errors (undefined variable or parameter, unknown function) are reported at its path; values that are only known at deployment are not.
/// </summary>
public sealed class Review2E_PropertyReferenceTests
{
    private static string Group(string properties, string variables = "")
    {
        return Review1C.Template(variables + "\"resources\": [ " + Review1C.Group("\"g1\"", properties) + " ]");
    }

    [Fact]
    public void AnUndefinedVariable_InAProperty_IsAnError_AtThePropertyPath()
    {
        var diagnostics = Review1C.Validate(Group("{ \"displayName\": \"[variables('nope')]\" }"));

        var error = Assert.Single(diagnostics, d => d.Code == DiagnosticCodes.UndefinedVariable);
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Equal("resources[0].properties.displayName", error.Path!.ToString());
        Assert.Contains("nope", error.Message, System.StringComparison.Ordinal);
        Assert.NotNull(error.Location);
    }

    [Fact]
    public void AnUndefinedParameter_InANestedArrayProperty_IsAnError_AtTheElementPath()
    {
        var diagnostics = Review1C.Validate(Group("{ \"displayName\": \"x\", \"groupTypes\": [ \"Unified\", \"[parameters('missing')]\" ] }"));

        var error = Assert.Single(diagnostics, d => d.Code == DiagnosticCodes.UndefinedParameter);
        Assert.Equal("resources[0].properties.groupTypes[1]", error.Path!.ToString());
    }

    [Fact]
    public void AnUnknownFunction_InAProperty_IsReportedOnce()
    {
        var diagnostics = Review1C.Validate(Group("{ \"displayName\": \"[bogus('a')]\" }"));

        var error = Assert.Single(diagnostics, d => d.Code == DiagnosticCodes.UnknownFunction);
        Assert.Equal("resources[0].properties.displayName", error.Path!.ToString());
    }

    [Fact]
    public void EachBrokenExpressionIsReported_NotOnlyTheFirstOfTheResource()
    {
        var diagnostics = Review1C.Validate(Group("{ \"displayName\": \"[variables('a')]\", \"description\": \"[parameters('b')]\", \"mailNickname\": \"[variables('c')]\" }"));

        Assert.Equal(["resources[0].properties.description", "resources[0].properties.displayName", "resources[0].properties.mailNickname"], diagnostics
            .Where(d => d.Code is DiagnosticCodes.UndefinedVariable or DiagnosticCodes.UndefinedParameter)
            .Select(d => d.Path!.ToString())
            .OrderBy(p => p, System.StringComparer.Ordinal)
            .ToArray());
    }

    [Fact]
    public void ADefinedVariableAndParameter_AndAReference_AreFine()
    {
        string template = Review1C.Template(
            "\"parameters\": { \"p\": { \"type\": \"string\", \"defaultValue\": \"pv\" } }, \"variables\": { \"v\": \"vv\" }, \"resources\": [ "
            + Review1C.Group("\"g1\"", "{ \"displayName\": \"[concat(variables('v'), parameters('p'))]\" }") + ", "
            + Review1C.Group("\"g2\"", "{ \"displayName\": \"g2\", \"description\": \"[reference('g1').id]\" }", ", \"dependsOn\": [ \"g1\" ]") + " ]");

        var diagnostics = Review1C.Validate(template);

        Assert.DoesNotContain(diagnostics, d => d.IsError);
    }

    [Fact]
    public void AParameterWithoutAValue_IsNotAnUndefinedParameter()
    {
        string template = Review1C.Template("\"parameters\": { \"p\": { \"type\": \"string\" } }, \"resources\": [ " + Review1C.Group("\"g1\"", "{ \"displayName\": \"[parameters('p')]\" }") + " ]");

        var diagnostics = Review1C.Validate(template);

        Assert.DoesNotContain(diagnostics, d => d.Code is DiagnosticCodes.UndefinedParameter or DiagnosticCodes.UndefinedVariable);
    }

    [Fact]
    public void AnUndefinedVariable_InsideALambdaInAProperty_IsReported()
    {
        var diagnostics = Review1C.Validate(Group("{ \"displayName\": \"x\", \"groupTypes\": \"[map(createArray('a'), lambda('s', concat(lambdaVariables('s'), variables('nope'))))]\" }"));

        Assert.Contains(diagnostics, d => d.Code == DiagnosticCodes.UndefinedVariable && d.Path!.ToString() == "resources[0].properties.groupTypes");
    }

    [Fact]
    public void ABrokenExpressionInPlainTextOrAnEscapedBracket_IsNotEvaluated()
    {
        var diagnostics = Review1C.Validate(Group("{ \"displayName\": \"[[variables('nope')]\", \"description\": \"variables('nope')\" }"));

        Assert.DoesNotContain(diagnostics, d => d.Code == DiagnosticCodes.UndefinedVariable);
    }
}
