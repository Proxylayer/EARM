using System;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Validation;
using EArm.Expressions.Evaluation;
using Xunit;

namespace EArm.Tests;

/// <summary>
/// ARM fidelity of parameters and variables: a value shaped like <c>[...]</c> is data and never bypasses a type check (only template text is an
/// expression); an expression <c>defaultValue</c> is checked against the type and the constraints once evaluated; <c>minValue</c>/<c>maxValue</c>/
/// <c>minLength</c>/<c>maxLength</c> may not be expressions; <c>allowedValues</c> of an array parameter constrains its elements; and a <c>copy</c>
/// block in <c>variables</c> is refused instead of being read as a variable named <c>copy</c>.
/// </summary>
public sealed class ArmFidelityParameterTests
{
    private static string Template(string parameters, string extra = "")
    {
        return Review1C.Template("\"parameters\": { " + parameters + " }, " + extra + "\"resources\": []");
    }

    private static ParameterSet File(string name, string valueJson)
    {
        return ParameterSet.Parse("{ \"parameters\": { \"" + name + "\": { \"value\": " + valueJson + " } } }", "p.json");
    }

    // ---- B8: supplied and evaluated values are data ----

    [Theory]
    [InlineData("int", "\"[1]\"")]
    [InlineData("bool", "\"[true()]\"")]
    [InlineData("object", "\"[createObject()]\"")]
    [InlineData("array", "\"[createArray()]\"")]
    public void ASuppliedValue_ShapedLikeAnExpression_DoesNotPassANonStringType(string type, string valueJson)
    {
        var diagnostics = Review1C.Validate(Template("\"p\": { \"type\": \"" + type + "\" }"), File("p", valueJson));

        Assert.Contains(diagnostics, d => d.Code == DiagnosticCodes.ParameterValueTypeMismatch);
    }

    [Fact]
    public void ASuppliedStringValue_ShapedLikeAnExpression_IsAcceptedAsAString()
    {
        var diagnostics = Review1C.Validate(Template("\"p\": { \"type\": \"string\" }"), File("p", "\"[not an expression]\""));

        Assert.DoesNotContain(diagnostics, d => d.IsError);
    }

    [Fact]
    public void Matches_TreatsAValueAsData_MatchesTemplateText_AcceptsAnExpression()
    {
        var text = JsonValue.Create("[parameters('x')]");

        Assert.False(TemplateParameterTypes.Matches(text, TemplateParameterType.Int));
        Assert.True(TemplateParameterTypes.Matches(text, TemplateParameterType.String));
        Assert.True(TemplateParameterTypes.MatchesTemplateText(text, TemplateParameterType.Int));
        Assert.False(TemplateParameterTypes.MatchesTemplateText(JsonValue.Create("1"), TemplateParameterType.Int));
    }

    private const string Functions = """
        "functions": [ { "namespace": "f", "members": {
          "twice":   { "parameters": [ { "name": "n", "type": "int" } ], "output": { "type": "int", "value": "[mul(parameters('n'), 2)]" } },
          "fakeInt": { "output": { "type": "int", "value": "[concat('[', '1]')]" } },
          "fakeArr": { "output": { "type": "array", "value": "[concat('[', ']')]" } }
        } } ],
        "resources": []
        """;

    private static EvaluationContext FunctionContext()
    {
        return Expr.Context(userFunctions: Expr.ParseBody(Functions).Functions);
    }

    [Fact]
    public void AUserFunctionArgument_ShapedLikeAnExpression_IsNotAnInt()
    {
        Expr.AssertJson("4", Expr.Eval("[f.twice(2)]", FunctionContext()));
        Expr.Fails("[f.twice(concat('[', '2]'))]", DiagnosticCodes.InvalidFunctionArguments, FunctionContext());
    }

    [Theory]
    [InlineData("[f.fakeInt()]")]
    [InlineData("[f.fakeArr()]")]
    public void AUserFunctionResult_ShapedLikeAnExpression_DoesNotMatchTheDeclaredOutputType(string expression)
    {
        Expr.Fails(expression, DiagnosticCodes.ExpressionEvaluationFailed, FunctionContext());
    }

    // ---- B9: expression defaults are checked once evaluated ----

    [Theory]
    [InlineData("\"type\": \"int\", \"defaultValue\": \"[concat('a')]\"", DiagnosticCodes.DefaultValueTypeMismatch)]
    [InlineData("\"type\": \"int\", \"defaultValue\": \"[concat('[', '1]')]\"", DiagnosticCodes.DefaultValueTypeMismatch)]
    [InlineData("\"type\": \"int\", \"defaultValue\": \"[add(5, 5)]\", \"maxValue\": 9", DiagnosticCodes.DefaultValueNotAllowed)]
    [InlineData("\"type\": \"int\", \"defaultValue\": \"[add(1, 1)]\", \"minValue\": 3", DiagnosticCodes.DefaultValueNotAllowed)]
    [InlineData("\"type\": \"string\", \"defaultValue\": \"[toUpper('a')]\", \"allowedValues\": [ \"a\", \"b\" ]", DiagnosticCodes.DefaultValueNotAllowed)]
    [InlineData("\"type\": \"string\", \"defaultValue\": \"[concat('abc', 'def')]\", \"maxLength\": 5", DiagnosticCodes.DefaultValueNotAllowed)]
    [InlineData("\"type\": \"array\", \"defaultValue\": \"[createArray(1, 2, 3)]\", \"maxLength\": 2", DiagnosticCodes.DefaultValueNotAllowed)]
    [InlineData("\"type\": \"object\", \"defaultValue\": \"[createArray()]\"", DiagnosticCodes.DefaultValueTypeMismatch)]
    [InlineData("\"type\": \"array\", \"defaultValue\": [ \"[toUpper('c')]\" ], \"allowedValues\": [ \"a\", \"b\" ]", DiagnosticCodes.DefaultValueNotAllowed)]
    public void AnEvaluatedExpressionDefault_IsCheckedLikeALiteralDefault(string definition, string code)
    {
        var diagnostics = Review1C.Validate(Template("\"p\": { " + definition + " }"));

        var diagnostic = Assert.Single(diagnostics, d => d.Code == code);
        Assert.StartsWith("Parameter 'p': the default value", diagnostic.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"type\": \"int\", \"defaultValue\": \"[add(4, 4)]\", \"minValue\": 1, \"maxValue\": 9")]
    [InlineData("\"type\": \"string\", \"defaultValue\": \"[toLower('A')]\", \"allowedValues\": [ \"a\", \"b\" ]")]
    [InlineData("\"type\": \"array\", \"defaultValue\": \"[createArray('a', 'b')]\", \"allowedValues\": [ \"a\", \"b\", \"c\" ]")]
    [InlineData("\"type\": \"string\", \"defaultValue\": \"[concat('[', 'x]')]\"")]
    public void AnEvaluatedExpressionDefault_WithinTheConstraints_IsAccepted(string definition)
    {
        Assert.DoesNotContain(Review1C.Validate(Template("\"p\": { " + definition + " }")), d => d.IsError);
    }

    [Fact]
    public void AnEvaluatedExpressionDefault_IsCheckedWhenTheParameterIsRead()
    {
        var document = Expr.ParseBody("\"parameters\": { \"p\": { \"type\": \"int\", \"defaultValue\": \"[add(5, 5)]\", \"maxValue\": 9 } }, \"resources\": []");
        var parameters = new LazyParameters(document.ParameterValues);
        var context = Expr.Context(parameters: parameters);

        var error = Expr.Fails("[parameters('p')]", DiagnosticCodes.DefaultValueNotAllowed, context);
        Assert.Contains("maxValue (9)", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASecureExpressionDefault_ThatViolatesAConstraint_IsReportedWithoutTheValue()
    {
        const string value = "TopSecretDefault";
        var diagnostics = Review1C.Validate(Template("\"pw\": { \"type\": \"securestring\", \"defaultValue\": \"[concat('TopSecret', 'Default')]\", \"maxLength\": 4 }"));

        var diagnostic = Assert.Single(diagnostics, d => d.Code == DiagnosticCodes.DefaultValueNotAllowed);
        Assert.DoesNotContain(value, diagnostic.ToString(), StringComparison.Ordinal);
    }

    // ---- B10: constraint properties may not be expressions ----

    [Theory]
    [InlineData("\"type\": \"int\", \"minValue\": \"[add(1, 1)]\"", "minValue")]
    [InlineData("\"type\": \"int\", \"maxValue\": \"[add(1, 1)]\"", "maxValue")]
    [InlineData("\"type\": \"string\", \"minLength\": \"[add(1, 1)]\"", "minLength")]
    [InlineData("\"type\": \"array\", \"maxLength\": \"[add(1, 1)]\"", "maxLength")]
    public void AnExpressionConstraint_IsInvalidParameterConstraint(string definition, string key)
    {
        var diagnostics = Review1C.Validate(Template("\"p\": { " + definition + " }"));

        var diagnostic = Assert.Single(diagnostics, d => d.Code == DiagnosticCodes.InvalidParameterConstraint);
        Assert.Contains($"'{key}' must be a literal integer", diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal($"parameters.p.{key}", diagnostic.Path!.ToString());
        Assert.DoesNotContain(diagnostics, d => d.Code == DiagnosticCodes.SchemaViolation);
    }

    // ---- B11: allowedValues of an array parameter constrain its elements ----

    [Theory]
    [InlineData("[ \"a\", \"c\" ]", false)]
    [InlineData("[ \"a\" ]", false)]
    [InlineData("[]", false)]
    [InlineData("[ \"a\", \"d\" ]", true)]
    [InlineData("[ [ \"a\" ] ]", true)]
    public void AnArrayValue_MustBeASubsetOfAllowedValues(string valueJson, bool violates)
    {
        string template = Template("\"p\": { \"type\": \"array\", \"allowedValues\": [ \"a\", \"b\", \"c\" ] }");

        var diagnostics = Review1C.Validate(template, File("p", valueJson));

        Assert.DoesNotContain(diagnostics, d => d.Code == DiagnosticCodes.AllowedValueTypeMismatch);
        Assert.Equal(violates, diagnostics.Any(d => d.Code == DiagnosticCodes.ParameterValueNotAllowed));
    }

    [Fact]
    public void AnArrayLiteralDefault_OutsideAllowedValues_IsDefaultValueNotAllowed()
    {
        var diagnostics = Review1C.Validate(Template("\"p\": { \"type\": \"array\", \"defaultValue\": [ \"a\", \"z\" ], \"allowedValues\": [ \"a\", \"b\" ] }"));

        Assert.Single(diagnostics, d => d.Code == DiagnosticCodes.DefaultValueNotAllowed);
    }

    // ---- variable copy ----

    [Fact]
    public void ACopyBlockInVariables_IsRefused_NotReadAsAVariableNamedCopy()
    {
        const string variables = "\"variables\": { \"copy\": [ { \"name\": \"items\", \"count\": 2, \"input\": \"[copyIndex('items')]\" } ], \"other\": 1 }, ";
        string template = Template("\"p\": { \"type\": \"string\", \"defaultValue\": \"x\" }", variables);

        var diagnostics = Review1C.Validate(template);

        var diagnostic = Assert.Single(diagnostics, d => d.Code == DiagnosticCodes.InvalidCopy);
        Assert.Equal("variables.copy", diagnostic.Path!.ToString());
        Assert.Contains("not supported", diagnostic.Message, StringComparison.Ordinal);
        var document = Expr.ParseBody(variables + "\"resources\": []");
        Assert.False(document.Variables.ContainsKey("copy"));
        Assert.True(document.Variables.ContainsKey("other"));
    }
}
