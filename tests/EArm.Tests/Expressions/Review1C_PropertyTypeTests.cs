using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Validation;
using EArm.Expressions.Values;
using Xunit;

namespace EArm.Tests;

/// <summary>
/// Review 1C, finding I-09: <c>validate</c> accepted a property whose expression evaluates to the wrong JSON type
/// (<c>"description": "[createArray(...)]"</c> on a group) because the template schema can only judge the text of the file, and the failure came
/// later at deployment time. The properties of every resource that holds an expression are now evaluated offline and their <c>type</c> checked
/// against the per-type schema (<c>PropertyTypeMismatch</c>), skipping every value that is not known offline.
/// </summary>
public sealed class Review1C_PropertyTypeTests
{
    private static List<Diagnostic> ValidateGroup(string properties, string extraBody = "", ParameterSet? parameters = null)
    {
        return Review1C.Validate(Review1C.Template(extraBody + "\"resources\": [ " + Review1C.Group("\"g\"", properties) + " ]"), parameters);
    }

    private static string GroupProperties(string extra)
    {
        return "{ \"displayName\": \"g\", \"mailEnabled\": false, \"securityEnabled\": true, " + extra + " }";
    }

    [Fact]
    public void AnArrayWhereAStringIsExpected_IsAPropertyTypeMismatch_AtTheExpression()
    {
        var diagnostics = ValidateGroup(GroupProperties("\"description\": \"[createArray('a', 'b')]\""));

        var error = Assert.Single(diagnostics, d => d.Code == DiagnosticCodes.PropertyTypeMismatch);
        Assert.Equal("resources[0].properties.description", error.Path!.ToString());
        Assert.True(error.IsError);
        Assert.Contains("wrong type", error.Message, StringComparison.Ordinal);
        Assert.NotNull(error.Location);
    }

    [Theory]
    [InlineData("\"description\": \"[createObject('a', 1)]\"")]
    [InlineData("\"description\": \"[add(1, 2)]\"")]
    [InlineData("\"description\": \"[true()]\"")]
    [InlineData("\"mailNickname\": \"[createArray(1)]\"")]
    public void AWrongTypeIsReported_ForStringProperties(string property)
    {
        var diagnostics = ValidateGroup(GroupProperties(property));

        Assert.Contains(diagnostics, d => d.Code == DiagnosticCodes.PropertyTypeMismatch && d.IsError);
    }

    [Fact]
    public void AStringWhereABooleanIsExpected_IsReported_AndABooleanIsNot()
    {
        var wrong = Review1C.Validate(Review1C.Template("\"resources\": [ " + Review1C.Group("\"g\"", "{ \"displayName\": \"g\", \"mailEnabled\": \"[string(false())]\", \"securityEnabled\": true }") + " ]"));
        var right = Review1C.Validate(Review1C.Template("\"resources\": [ " + Review1C.Group("\"g\"", "{ \"displayName\": \"g\", \"mailEnabled\": \"[equals(1, 2)]\", \"securityEnabled\": \"[not(false())]\" }") + " ]"));

        var error = Assert.Single(wrong, d => d.Code == DiagnosticCodes.PropertyTypeMismatch);
        Assert.Equal("resources[0].properties.mailEnabled", error.Path!.ToString());
        Assert.DoesNotContain(right, d => d.Code == DiagnosticCodes.PropertyTypeMismatch);
    }

    [Fact]
    public void ARightType_FromAnExpression_IsNotReported()
    {
        var diagnostics = ValidateGroup(GroupProperties("\"description\": \"[concat('a', 'b')]\", \"mailNickname\": \"[toLower('Nick')]\""));

        Assert.DoesNotContain(diagnostics, d => d.Code == DiagnosticCodes.PropertyTypeMismatch);
    }

    [Fact]
    public void AValueFromAVariable_OrAParameter_IsChecked()
    {
        var fromVariable = ValidateGroup(
            GroupProperties("\"description\": \"[variables('list')]\""),
            "\"variables\": { \"list\": [ \"a\" ] }, ");
        var fromParameter = ValidateGroup(
            GroupProperties("\"description\": \"[parameters('p')]\""),
            "\"parameters\": { \"p\": { \"type\": \"object\" } }, ",
            ParameterSet.Parse("{ \"parameters\": { \"p\": { \"value\": { \"k\": 1 } } } }"));

        Assert.Contains(fromVariable, d => d.Code == DiagnosticCodes.PropertyTypeMismatch);
        Assert.Contains(fromParameter, d => d.Code == DiagnosticCodes.PropertyTypeMismatch);
    }

    [Fact]
    public void ValuesThatAreNotKnownOffline_AreSkipped()
    {
        // A parameter without a value, a reference() to another resource, a secure parameter and copyIndex() are unknown or secret offline.
        var diagnostics = Review1C.Validate(Review1C.Template(
            "\"parameters\": { \"missing\": { \"type\": \"array\" }, \"secret\": { \"type\": \"securestring\" } }, \"resources\": [ "
            + Review1C.Group("\"other\"", "{ \"displayName\": \"o\", \"mailEnabled\": false, \"securityEnabled\": true }") + ", "
            + Review1C.Group("\"g\"", GroupProperties("\"description\": \"[parameters('missing')]\", \"mailNickname\": \"[reference('other').id]\", \"uniqueName\": \"[parameters('secret')]\"")) + " ]"));

        Assert.DoesNotContain(diagnostics, d => d.Code == DiagnosticCodes.PropertyTypeMismatch);
    }

    [Fact]
    public void ASecureValueOfTheWrongTypeInsideAnArray_IsNotReported_AndNothingSecretIsEchoed()
    {
        string secret = "TopSecretValue-123";
        var diagnostics = Review1C.Validate(
            Review1C.Template("\"parameters\": { \"s\": { \"type\": \"securestring\" } }, \"resources\": [ "
                + Review1C.Group("\"g\"", GroupProperties("\"description\": \"[createArray(parameters('s'))]\"")) + " ]"),
            ParameterSet.Empty.WithInline("s", secret));

        foreach (var diagnostic in diagnostics)
        {
            Assert.DoesNotContain(secret, diagnostic.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ALiteralOfTheWrongType_IsReportedOnce_BySchemaValidation()
    {
        var diagnostics = ValidateGroup(GroupProperties("\"description\": 5"));

        Assert.DoesNotContain(diagnostics, d => d.Code == DiagnosticCodes.PropertyTypeMismatch);
        Assert.Contains(diagnostics, d => d.Code == DiagnosticCodes.SchemaViolation && d.Path!.ToString() == "resources[0].properties.description");
    }

    [Fact]
    public void AWrongValue_InsideAnArrayThatComesFromTheTemplate_IsReportedAtTheElementExpression()
    {
        var diagnostics = ValidateGroup(GroupProperties("\"groupTypes\": [ \"Unified\", \"[createArray('x')]\" ]"));

        var error = Assert.Single(diagnostics, d => d.Code == DiagnosticCodes.PropertyTypeMismatch);
        Assert.Equal("resources[0].properties.groupTypes[1]", error.Path!.ToString());
    }

    [Fact]
    public void AnEvaluatedValue_InsideAnEvaluatedArray_IsReportedAtTheExpressionThatProducedIt()
    {
        var diagnostics = ValidateGroup(GroupProperties("\"groupTypes\": \"[createArray('Unified', createArray('x'))]\""));

        var error = Assert.Single(diagnostics, d => d.Code == DiagnosticCodes.PropertyTypeMismatch);
        Assert.Equal("resources[0].properties.groupTypes", error.Path!.ToString());
        Assert.Contains("groupTypes[1]", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACopyLoopResource_IsEvaluatedWithAnUnknownIndex()
    {
        string resource = "{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"[format('g{0}', copyIndex())]\", \"copy\": { \"name\": \"c\", \"count\": 3 }, "
            + "\"properties\": { \"displayName\": \"[format('Group {0}', copyIndex())]\", \"mailEnabled\": false, \"securityEnabled\": true, \"description\": \"[createArray(copyIndex())]\" } }";

        var diagnostics = Review1C.Validate(Review1C.Template("\"resources\": [ " + resource + " ]"));

        var error = Assert.Single(diagnostics, d => d.Code == DiagnosticCodes.PropertyTypeMismatch);
        Assert.Equal("resources[0].properties.description", error.Path!.ToString());
    }

    [Fact]
    public void TheSamples_HaveNoPropertyTypeMismatch()
    {
        string[] files = Directory.GetFiles(TestPaths.SamplesDirectory, "*.entra.json");
        Assert.NotEmpty(files);

        foreach (string file in files)
        {
            var diagnostics = Review1C.Validate(File.ReadAllText(file, Encoding.UTF8));

            Assert.DoesNotContain(diagnostics, d => d.Code == DiagnosticCodes.PropertyTypeMismatch);
        }
    }

    [Fact]
    public void TheChecker_SkipsTypesWithoutASchema_AndNonObjects()
    {
        Func<System.Text.Json.Nodes.JsonNode?, bool> opaque = leaf => UnknownJson.IsUnknown(leaf);

        Assert.Empty(PropertyTypeChecker.Check("Microsoft.Entra/doesNotExist", new System.Text.Json.Nodes.JsonObject { ["x"] = 1 }, opaque));
        Assert.Empty(PropertyTypeChecker.Check("Microsoft.Entra/groups", System.Text.Json.Nodes.JsonValue.Create("text"), opaque));
        Assert.Empty(PropertyTypeChecker.Check("Microsoft.Entra/groups", null, opaque));

        var findings = PropertyTypeChecker.Check("Microsoft.Entra/groups", new System.Text.Json.Nodes.JsonObject { ["description"] = new System.Text.Json.Nodes.JsonArray("a") }, opaque);
        Assert.Equal(["description"], findings.Select(f => f.Path.ToString()).ToArray());
    }
}
