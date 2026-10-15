using System;
using System.Linq;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Validation;
using Xunit;

namespace EArm.Tests;

/// <summary>
/// Review 3J, finding 7: <c>allowedValues</c> was not enforced on secure parameters (the check skipped them), and <c>minValue</c>, <c>maxValue</c>,
/// <c>minLength</c> and <c>maxLength</c> were not enforced on any value. Every constraint is now checked for supplied values and literal defaults, secure
/// parameters included (on the revealed value); the messages name the parameter and the constraint, never a value. <see cref="ParameterConstraints"/> is
/// public so the deployment can check a value it resolves from Key Vault the same way.
/// </summary>
public sealed class Review3J_ParameterConstraintTests
{
    private const string Secret = "Sup3r-Secret-Value!";

    private static string Template(string parameters)
    {
        return Review1C.Template("\"parameters\": { " + parameters + " }, \"resources\": []");
    }

    private static Diagnostic Single(string template, ParameterSet parameters, string code)
    {
        var diagnostics = Review1C.Validate(template, parameters);
        return Assert.Single(diagnostics, d => d.Code == code);
    }

    // ---- secure parameters ----

    [Fact]
    public void ASecureString_OutsideAllowedValues_IsRefused_WithoutTheValueInTheMessage()
    {
        string template = Template("\"pw\": { \"type\": \"securestring\", \"allowedValues\": [ \"alpha\", \"beta\" ] }");

        var diagnostic = Single(template, ParameterSet.Empty.WithInline("pw", Secret), DiagnosticCodes.ParameterValueNotAllowed);

        Assert.Contains("allowedValues of parameter 'pw'", diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, diagnostic.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("alpha", diagnostic.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ASecureString_InsideAllowedValues_IsAccepted()
    {
        string template = Template("\"pw\": { \"type\": \"securestring\", \"allowedValues\": [ \"alpha\", \"beta\" ] }");

        var diagnostics = Review1C.Validate(template, ParameterSet.Empty.WithInline("pw", "beta"));

        Assert.DoesNotContain(diagnostics, d => d.Code == DiagnosticCodes.ParameterValueNotAllowed);
    }

    [Fact]
    public void ASecureObject_FromAFile_OutsideAllowedValues_IsRefused()
    {
        string template = Template("\"cfg\": { \"type\": \"secureObject\", \"allowedValues\": [ { \"k\": 1 } ] }");
        var parameters = ParameterSet.Parse("{ \"parameters\": { \"cfg\": { \"value\": { \"k\": 2, \"token\": \"" + Secret + "\" } } } }", "p.json");

        var diagnostic = Single(template, parameters, DiagnosticCodes.ParameterValueNotAllowed);

        Assert.Equal("parameters.cfg.value", diagnostic.Path!.ToString());
        Assert.DoesNotContain(Secret, diagnostic.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ASecureDefault_OutsideAllowedValues_IsRefused()
    {
        string template = Template("\"pw\": { \"type\": \"securestring\", \"defaultValue\": \"" + Secret + "\", \"allowedValues\": [ \"alpha\" ] }");

        var diagnostics = Review1C.Validate(template, ParameterSet.Empty);

        var diagnostic = Assert.Single(diagnostics, d => d.Code == DiagnosticCodes.DefaultValueNotAllowed);
        Assert.Contains("allowedValues of parameter 'pw'", diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, string.Join('\n', diagnostics.Select(d => d.ToString())), StringComparison.Ordinal);
    }

    // ---- lengths and values, secure or not ----

    [Theory]
    [InlineData("string", "ab", 3, null, true)]
    [InlineData("string", "abc", 3, null, false)]
    [InlineData("string", "abcd", null, 3, true)]
    [InlineData("string", "abc", null, 3, false)]
    [InlineData("securestring", "ab", 3, null, true)]
    [InlineData("securestring", "abc", 3, null, false)]
    [InlineData("securestring", "abcd", null, 3, true)]
    [InlineData("securestring", "", 1, null, true)]
    public void MinLengthAndMaxLength_AreEnforced_ForStringsAndSecureStrings(string type, string value, int? min, int? max, bool violates)
    {
        string constraints = (min is null ? string.Empty : $", \"minLength\": {min}") + (max is null ? string.Empty : $", \"maxLength\": {max}");
        string template = Template($"\"p\": {{ \"type\": \"{type}\"{constraints} }}");

        var diagnostics = Review1C.Validate(template, ParameterSet.Empty.WithInline("p", value));

        Assert.Equal(violates, diagnostics.Any(d => d.Code == DiagnosticCodes.ParameterValueNotAllowed));
        Assert.DoesNotContain("abcd", string.Join('\n', diagnostics.Select(d => d.ToString())), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(4, 5, 10, true)]
    [InlineData(5, 5, 10, false)]
    [InlineData(10, 5, 10, false)]
    [InlineData(11, 5, 10, true)]
    public void MinValueAndMaxValue_AreEnforced_ToTheValue(int value, int min, int max, bool violates)
    {
        string template = Template($"\"n\": {{ \"type\": \"int\", \"minValue\": {min}, \"maxValue\": {max} }}");

        var diagnostics = Review1C.Validate(template, ParameterSet.Empty.WithInline("n", value.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        Assert.Equal(violates, diagnostics.Any(d => d.Code == DiagnosticCodes.ParameterValueNotAllowed));
    }

    [Theory]
    [InlineData("[1,2]", 1, true)]
    [InlineData("[1,2]", 2, false)]
    [InlineData("[1,2,3]", 2, true)]
    public void ForAnArray_TheLengthsCountElements(string json, int maxLength, bool violates)
    {
        string template = Template($"\"a\": {{ \"type\": \"array\", \"maxLength\": {maxLength} }}");

        var diagnostics = Review1C.Validate(template, ParameterSet.Empty.WithInline("a", json));

        Assert.Equal(violates, diagnostics.Any(d => d.Code == DiagnosticCodes.ParameterValueNotAllowed));
    }

    [Fact]
    public void ALiteralDefault_OutsideTheConstraints_IsDefaultValueNotAllowed_AnExpressionDefaultIsNotChecked()
    {
        string tooShort = Template("\"p\": { \"type\": \"string\", \"defaultValue\": \"ab\", \"minLength\": 3 }");
        string expression = Template("\"p\": { \"type\": \"string\", \"defaultValue\": \"[concat('a', 'b')]\", \"minLength\": 3 }");

        Assert.Contains(Review1C.Validate(tooShort), d => d.Code == DiagnosticCodes.DefaultValueNotAllowed && d.Message.Contains("minLength (3)", StringComparison.Ordinal));
        Assert.DoesNotContain(Review1C.Validate(expression), d => d.Code == DiagnosticCodes.DefaultValueNotAllowed);
    }

    [Fact]
    public void ASuppliedValue_ThatLooksLikeAnExpression_IsStillChecked_ItIsData()
    {
        string template = Template("\"p\": { \"type\": \"string\", \"allowedValues\": [ \"a\" ] }");

        var diagnostics = Review1C.Validate(template, ParameterSet.Empty.WithInline("p", "[concat('a')]"));

        Assert.Contains(diagnostics, d => d.Code == DiagnosticCodes.ParameterValueNotAllowed);
    }

    // ---- the public checker (for values resolved at deployment time) ----

    [Fact]
    public void TheChecker_IsPublic_AndComparesTheRevealedValue()
    {
        var template = Review1C.Validate(Template("\"pw\": { \"type\": \"securestring\", \"minLength\": 8, \"allowedValues\": [ \"12345678\", \"abcdefgh\" ] }"));
        Assert.DoesNotContain(template, d => d.IsError);
        var document = Expr.ParseBody("\"parameters\": { \"pw\": { \"type\": \"securestring\", \"minLength\": 8, \"allowedValues\": [ \"12345678\", \"abcdefgh\" ] } }, \"resources\": []");
        var definition = document.Parameters["pw"];

        Assert.Null(ParameterConstraints.Violation(definition, EArm.Core.Secrets.SecretJson.CreateString("abcdefgh")));
        var violation = ParameterConstraints.Violation(definition, EArm.Core.Secrets.SecretJson.CreateString(Secret));
        Assert.NotNull(violation);
        Assert.DoesNotContain(Secret, violation, StringComparison.Ordinal);
        Assert.Contains("parameter 'pw'", violation, StringComparison.Ordinal);
    }
}
