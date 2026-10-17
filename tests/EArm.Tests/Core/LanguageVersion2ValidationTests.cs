using System.Collections.Generic;
using System.Linq;
using EArm.Core;
using EArm.Core.Diagnostics;
using Xunit;

namespace EArm.Tests;

/// <summary>
/// languageVersion 2.0 offline (https://learn.microsoft.com/azure/azure-resource-manager/templates/syntax#languageversion-20): the envelope,
/// symbolic names, <c>existing</c>, <c>definitions</c> with <c>$ref</c> and the aggregate type constraints, and the symbolic forms of
/// <c>dependsOn</c>, <c>reference()</c> and <c>references()</c>.
/// </summary>
public sealed class LanguageVersion2ValidationTests
{
    private const string Lv2 = "\"languageVersion\": \"2.0\", ";

    private static string Group(string name, string extra = "")
    {
        return "{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"" + name + "\", \"properties\": { \"displayName\": \"G\", \"mailEnabled\": false, \"securityEnabled\": true }" + extra + " }";
    }

    private static List<Diagnostic> Validate(string body, ParameterSet? parameters = null)
    {
        return Review1C.Validate(Review1C.Template(body), parameters);
    }

    private static List<Diagnostic> Errors(string body, ParameterSet? parameters = null)
    {
        return Validate(body, parameters).Where(d => d.IsError).ToList();
    }

    // ---- envelope and symbolic names ----

    [Fact]
    public void ResourcesKeyedBySymbolicName_AreLoaded_WithTheirSymbolicNames()
    {
        var document = Expr.ParseBody(Lv2 + "\"resources\": { \"a\": " + Group("a") + ", \"b\": " + Group("b", ", \"dependsOn\": [ \"a\" ]") + " }");

        Assert.True(document.UsesSymbolicNames);
        Assert.Equal(["a", "b"], document.Resources.Select(r => r.SymbolicName));
        Assert.Equal("resources.b", document.Resources[1].Path.ToString());
        Assert.Empty(Errors(Lv2 + "\"resources\": { \"a\": " + Group("a") + ", \"b\": " + Group("b", ", \"dependsOn\": [ \"a\" ]") + " }"));
    }

    [Fact]
    public void SymbolicNames_AreCaseSensitive()
    {
        Assert.Empty(Errors(Lv2 + "\"resources\": { \"a\": " + Group("a") + ", \"A\": " + Group("b") + " }"));
    }

    [Theory]
    [InlineData("\"resources\": { \"a\": GROUP }", DiagnosticCodes.InvalidLanguageVersion)]
    [InlineData("\"languageVersion\": \"2.0\", \"resources\": [ GROUP ]", DiagnosticCodes.InvalidLanguageVersion)]
    [InlineData("\"languageVersion\": \"3.0\", \"resources\": { \"a\": GROUP }", DiagnosticCodes.InvalidLanguageVersion)]
    [InlineData("\"languageVersion\": \"2.0\", \"resources\": { \"a-b\": GROUP }", DiagnosticCodes.InvalidSymbolicName)]
    public void AMalformedEnvelope_IsReported(string body, string code)
    {
        Assert.Contains(Errors(body.Replace("GROUP", Group("a"), System.StringComparison.Ordinal)), d => d.Code == code);
    }

    [Fact]
    public void AnExperimentalLanguageVersion_IsAWarning_AndReadAs2()
    {
        var diagnostics = Validate("\"languageVersion\": \"2.1-experimental\", \"resources\": { \"a\": " + Group("a") + " }");

        Assert.Contains(diagnostics, d => d.Code == DiagnosticCodes.InvalidLanguageVersion && d.Severity == DiagnosticSeverity.Warning);
        Assert.DoesNotContain(diagnostics, d => d.IsError);
    }

    // ---- existing ----

    [Fact]
    public void AnExistingResource_NeedsOnlyTypeApiVersionAndName()
    {
        string existing = "{ \"existing\": true, \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"admins\" }";

        Assert.Empty(Errors(Lv2 + "\"resources\": { \"admins\": " + existing + " }"));
        Assert.True(Expr.ParseBody(Lv2 + "\"resources\": { \"admins\": " + existing + " }").Resources[0].IsExisting);
    }

    [Theory]
    [InlineData("\"languageVersion\": \"2.0\", \"resources\": { \"a\": { \"existing\": true, \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"a\", \"properties\": { \"displayName\": \"x\" } } }")]
    [InlineData("\"resources\": [ { \"existing\": true, \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"a\" } ]")]
    [InlineData("\"languageVersion\": \"2.0\", \"resources\": { \"a\": { \"existing\": \"yes\", \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"a\" } }")]
    public void AMisusedExisting_IsReported(string body)
    {
        Assert.Contains(Errors(body), d => d.Code == DiagnosticCodes.InvalidExistingResource);
    }

    // ---- dependsOn, reference(), references() ----

    private const string Loop = "\"loop\": { \"copy\": { \"name\": \"loop\", \"count\": 2 }, \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"[format('l{0}', copyIndex())]\", \"properties\": { \"displayName\": \"L\", \"mailEnabled\": false, \"securityEnabled\": true } }";

    [Fact]
    public void SymbolicReferences_ResolveToTemplateResources()
    {
        string body = Lv2 + "\"resources\": { \"a\": " + Group("a") + ", " + Loop + ", \"b\": " + Group("b", ", \"dependsOn\": [ \"a\", \"loop\", \"loop[1]\" ]") + " }, "
            + "\"outputs\": { \"one\": { \"type\": \"string\", \"value\": \"[reference('a').id]\" }, \"each\": { \"type\": \"array\", \"value\": \"[references('loop')]\" }, "
            + "\"iteration\": { \"type\": \"object\", \"value\": \"[reference('loop[0]', '2026-10-01-preview', 'Full')]\" } }";

        Assert.Empty(Errors(body));
    }

    [Theory]
    [InlineData("\"dependsOn\": [ \"nope\" ]", DiagnosticCodes.DependsOnTargetNotFound)]
    [InlineData("\"dependsOn\": [ \"a[0]\" ]", DiagnosticCodes.InvalidSymbolicName)]
    [InlineData("\"properties\": { \"displayName\": \"[reference('loop').displayName]\", \"mailEnabled\": false, \"securityEnabled\": true }", DiagnosticCodes.ReferenceTargetNotFound)]
    [InlineData("\"properties\": { \"displayName\": \"[string(references('a'))]\", \"mailEnabled\": false, \"securityEnabled\": true }", DiagnosticCodes.ReferenceTargetNotFound)]
    [InlineData("\"properties\": { \"displayName\": \"[reference('a', '2026-10-01-preview', 'Partial').displayName]\", \"mailEnabled\": false, \"securityEnabled\": true }", DiagnosticCodes.InvalidFunctionArguments)]
    public void AWrongSymbolicReference_IsReported(string member, string code)
    {
        string b = "{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"b\", " + member
            + (member.StartsWith("\"dependsOn\"", System.StringComparison.Ordinal) ? ", \"properties\": { \"displayName\": \"B\", \"mailEnabled\": false, \"securityEnabled\": true }" : string.Empty) + " }";

        var errors = Errors(Lv2 + "\"resources\": { \"a\": " + Group("a") + ", " + Loop + ", \"b\": " + b + " }");

        Assert.Contains(errors, d => d.Code == code);
    }

    [Fact]
    public void ReferencesInAVariable_OrInsideTheLoopItNames_IsRefused()
    {
        var inVariable = Errors(Lv2 + "\"variables\": { \"v\": \"[references('loop')]\" }, \"resources\": { " + Loop + " }");
        string selfLoop = Loop.Replace("\"displayName\": \"L\"", "\"displayName\": \"[string(length(references('loop')))]\"", System.StringComparison.Ordinal);
        var inLoop = Errors(Lv2 + "\"resources\": { " + selfLoop + " }");

        Assert.Contains(inVariable, d => d.Code == DiagnosticCodes.ReferenceNotAllowedHere);
        Assert.Contains(inLoop, d => d.Code == DiagnosticCodes.ReferenceNotAllowedHere);
    }

    [Fact]
    public void LanguageVersion1_ReferenceByApiVersionInAVariable_IsStillAllowed()
    {
        string body = "\"variables\": { \"g\": \"[reference(resourceId('Microsoft.Entra/groups', 'existing'), '2026-10-01-preview').id]\" }, \"resources\": [ " + Group("a") + " ]";

        Assert.DoesNotContain(Errors(body), d => d.Code == DiagnosticCodes.ReferenceNotAllowedHere);
    }

    // ---- definitions and typed parameters ----

    private const string Definitions = "\"definitions\": { "
        + "\"team\": { \"type\": \"object\", \"properties\": { \"key\": { \"type\": \"string\", \"minLength\": 2 }, \"note\": { \"type\": \"string\", \"nullable\": true } }, \"additionalProperties\": false, \"metadata\": { \"__bicep_export!\": true } }, "
        + "\"shape\": { \"type\": \"object\", \"discriminator\": { \"propertyName\": \"kind\", \"mapping\": { \"circle\": { \"type\": \"object\", \"properties\": { \"radius\": { \"type\": \"int\", \"minValue\": 1 } } } } } }, "
        + "\"pair\": { \"type\": \"array\", \"prefixItems\": [ { \"type\": \"string\" }, { \"type\": \"int\" } ], \"items\": false } }, ";

    private static string Typed(string parameters)
    {
        return Lv2 + Definitions + "\"parameters\": { " + parameters + " }, \"resources\": {}";
    }

    private static ParameterSet Value(string name, string json)
    {
        return ParameterSet.Parse("{ \"parameters\": { \"" + name + "\": { \"value\": " + json + " } } }", "p.json");
    }

    [Theory]
    [InlineData("[ { \"key\": \"ab\" }, { \"key\": \"cd\", \"note\": \"x\" } ]", true)]
    [InlineData("[ { \"key\": \"a\" } ]", false)]
    [InlineData("[ { \"note\": \"x\" } ]", false)]
    [InlineData("[ { \"key\": \"ab\", \"extra\": 1 } ]", false)]
    [InlineData("[ { \"key\": 12 } ]", false)]
    public void AnArrayOfAUserDefinedType_IsChecked(string value, bool accepted)
    {
        var diagnostics = Validate(Typed("\"teams\": { \"type\": \"array\", \"items\": { \"$ref\": \"#/definitions/team\" } }"), Value("teams", value));

        Assert.Equal(accepted, !diagnostics.Any(d => d.Code == DiagnosticCodes.ParameterValueNotAllowed));
        Assert.DoesNotContain(diagnostics, d => d.Code is DiagnosticCodes.InvalidTypeDefinition or DiagnosticCodes.SchemaViolation or DiagnosticCodes.InvalidParameterType);
    }

    [Theory]
    [InlineData("shape", "{ \"kind\": \"circle\", \"radius\": 2 }", true)]
    [InlineData("shape", "{ \"kind\": \"circle\", \"radius\": 0 }", false)]
    [InlineData("shape", "{ \"kind\": \"square\" }", false)]
    [InlineData("pair", "[ \"a\", 1 ]", true)]
    [InlineData("pair", "[ \"a\", \"b\" ]", false)]
    [InlineData("pair", "[ \"a\", 1, 2 ]", false)]
    public void ADiscriminatedOrTupleType_IsChecked(string type, string value, bool accepted)
    {
        var diagnostics = Validate(Typed("\"p\": { \"$ref\": \"#/definitions/" + type + "\" }"), Value("p", value));

        Assert.Equal(accepted, !diagnostics.Any(d => d.Code == DiagnosticCodes.ParameterValueNotAllowed));
    }

    [Fact]
    public void ANullableParameter_MayBeOmitted()
    {
        var diagnostics = Validate(Typed("\"p\": { \"type\": \"string\", \"nullable\": true }"));

        Assert.DoesNotContain(diagnostics, d => d.Code == DiagnosticCodes.MissingParameterValue);
        Assert.DoesNotContain(diagnostics, d => d.IsError);
    }

    [Theory]
    [InlineData("\"p\": { \"$ref\": \"#/definitions/missing\" }")]
    [InlineData("\"p\": { \"type\": \"string\", \"properties\": { } }")]
    [InlineData("\"p\": { \"type\": \"object\", \"properties\": { \"x\": { \"type\": \"strings\" } } }")]
    public void AMalformedTypeDefinition_IsReported(string parameters)
    {
        Assert.Contains(Errors(Typed(parameters)), d => d.Code == DiagnosticCodes.InvalidTypeDefinition);
    }

    [Theory]
    [InlineData("\"definitions\": { \"t\": { \"type\": \"string\" } }, \"resources\": []")]
    [InlineData("\"parameters\": { \"p\": { \"type\": \"string\", \"nullable\": true } }, \"resources\": []")]
    public void Version2Keywords_WithoutLanguageVersion2_AreReported(string body)
    {
        Assert.Contains(Errors(body), d => d.Code == DiagnosticCodes.InvalidLanguageVersion);
    }
}
