using System;
using System.Collections.Generic;
using System.Linq;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Loading;
using EArm.Core.Types;
using EArm.Core.Validation;
using Xunit;

namespace EArm.Tests;

/// <summary>
/// Table-driven shape errors for the semantic pass of the template validator (parameters, constraints, user functions, copy, names,
/// nested resources): every case is one malformed fragment and the diagnostic code and path it must produce, and no case may throw.
/// </summary>
public sealed class SemanticCheckerShapeTests
{
    private const string SchemaUri = "https://schema.earm.proxylayer.net/schemas/2026-10-01/deploymentTemplate.json#";

    private static readonly TemplateLoader Loader = new(ResourceTypeCatalog.Default);
    private static readonly TemplateValidator Validator = new(ResourceTypeCatalog.Default);

    private static IReadOnlyList<Diagnostic> Validate(string members)
    {
        string json = "{ \"$schema\": \"" + SchemaUri + "\", \"contentVersion\": \"1.0.0.0\", " + members + " }";
        return Validator.Validate(Loader.Parse(json, "case.entra.json", ParameterSet.Empty));
    }

    private static string Group(string name, string extra = "")
    {
        return "{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"" + name + "\", \"properties\": { \"displayName\": \"G\" }" + extra + " }";
    }

    private static void AssertReports(IReadOnlyList<Diagnostic> diagnostics, string code, string pathFragment, string messageFragment)
    {
        var match = diagnostics.FirstOrDefault(d => d.Code == code && (d.Path?.ToString() ?? string.Empty).Contains(pathFragment, StringComparison.Ordinal) && d.Message.Contains(messageFragment, StringComparison.Ordinal));
        Assert.True(match is not null, $"expected {code} at '{pathFragment}' containing '{messageFragment}'; got: " + string.Join(" | ", diagnostics.Select(d => $"{d.Code} [{d.Path}] {d.Message}")));
        Assert.Equal(DiagnosticSeverity.Error, match.Severity);
    }

    // ---- parameters ----

    [Theory]
    [InlineData("\"parameters\": { \"p\": { } }, \"resources\": []", DiagnosticCodes.InvalidParameterType, "parameters.p.type", "missing the required 'type'")]
    [InlineData("\"parameters\": { \"p\": { \"type\": \"decimal\" } }, \"resources\": []", DiagnosticCodes.InvalidParameterType, "parameters.p.type", "decimal")]
    [InlineData("\"parameters\": { \"p\": { \"type\": \"string\", \"minLength\": -1 } }, \"resources\": []", DiagnosticCodes.InvalidParameterConstraint, "parameters.p.minLength", "must not be negative")]
    [InlineData("\"parameters\": { \"p\": { \"type\": \"string\", \"maxLength\": -2 } }, \"resources\": []", DiagnosticCodes.InvalidParameterConstraint, "parameters.p.maxLength", "must not be negative")]
    [InlineData("\"parameters\": { \"p\": { \"type\": \"string\", \"minLength\": 9, \"maxLength\": 3 } }, \"resources\": []", DiagnosticCodes.InvalidParameterConstraint, "parameters.p", "")]
    [InlineData("\"parameters\": { \"p\": { \"type\": \"string\", \"minLength\": \"three\" } }, \"resources\": []", DiagnosticCodes.InvalidParameterConstraint, "parameters.p.minLength", "must be an integer")]
    [InlineData("\"parameters\": { \"p\": { \"type\": \"string\", \"minLength\": 1.5 } }, \"resources\": []", DiagnosticCodes.InvalidParameterConstraint, "parameters.p.minLength", "must be an integer")]
    public void ParameterShapeErrors_AreReported(string members, string code, string path, string message)
    {
        AssertReports(Validate(members), code, path, message);
    }

    // ---- user-defined functions ----

    [Theory]
    [InlineData("\"functions\": [ { \"members\": { \"f\": { \"output\": { \"type\": \"string\", \"value\": \"x\" } } } } ], \"resources\": []", "functions[0].namespace", "missing the required 'namespace'")]
    [InlineData("\"functions\": [ { \"namespace\": \"ns\" } ], \"resources\": []", "functions[0].members", "missing the required 'members'")]
    [InlineData("\"functions\": [ { \"namespace\": \"ns\", \"members\": { \"f\": { } } } ], \"resources\": []", "output", "missing the required 'output'")]
    [InlineData("\"functions\": [ { \"namespace\": \"ns\", \"members\": { \"f\": { \"output\": { \"value\": \"x\" } } } } ], \"resources\": []", "output.type", "missing the required 'type'")]
    [InlineData("\"functions\": [ { \"namespace\": \"ns\", \"members\": { \"f\": { \"output\": { \"type\": \"nope\", \"value\": \"x\" } } } } ], \"resources\": []", "output.type", "unknown type 'nope'")]
    [InlineData("\"functions\": [ { \"namespace\": \"ns\", \"members\": { \"f\": { \"output\": { \"type\": \"string\" } } } } ], \"resources\": []", "output.value", "missing the required 'value'")]
    [InlineData("\"functions\": [ { \"namespace\": \"ns\", \"members\": { \"f\": { \"parameters\": [ { \"type\": \"string\" } ], \"output\": { \"type\": \"string\", \"value\": \"x\" } } } } ], \"resources\": []", "parameters[0]", "missing 'name'")]
    [InlineData("\"functions\": [ { \"namespace\": \"ns\", \"members\": { \"f\": { \"parameters\": [ { \"name\": \"a\" } ], \"output\": { \"type\": \"string\", \"value\": \"x\" } } } } ], \"resources\": []", "parameters[0]", "missing 'type'")]
    [InlineData("\"functions\": [ { \"namespace\": \"ns\", \"members\": { \"f\": { \"parameters\": [ { \"name\": \"a\", \"type\": \"blob\" } ], \"output\": { \"type\": \"string\", \"value\": \"x\" } } } } ], \"resources\": []", "parameters[0].type", "unknown type 'blob'")]
    public void FunctionShapeErrors_AreReported(string members, string path, string message)
    {
        var diagnostics = Validate(members);

        Assert.Contains(diagnostics, d => d.IsError && (d.Path?.ToString() ?? string.Empty).Contains(path, StringComparison.Ordinal) && d.Message.Contains(message, StringComparison.Ordinal));
    }

    // ---- copy ----

    [Theory]
    [InlineData("{ \"count\": 2 }", "copy.name", "")]
    [InlineData("{ \"name\": \"\", \"count\": 2 }", "copy.name", "non-empty literal string")]
    [InlineData("{ \"name\": \"[concat('a')]\", \"count\": 2 }", "copy.name", "non-empty literal string")]
    [InlineData("{ \"name\": \"l\" }", "copy.count", "missing the required 'count'")]
    [InlineData("{ \"name\": \"l\", \"count\": \"two\" }", "copy.count", "integer or an expression")]
    [InlineData("{ \"name\": \"l\", \"count\": 1.5 }", "copy.count", "integer or an expression")]
    [InlineData("{ \"name\": \"l\", \"count\": -3 }", "copy.count", "must not be negative")]
    public void CopyShapeErrors_AreReported(string copy, string path, string message)
    {
        var diagnostics = Validate("\"resources\": [ " + Group("[concat('g', copyIndex('l'))]", ", \"copy\": " + copy) + " ]");

        Assert.Contains(diagnostics, d => d.IsError && (d.Path?.ToString() ?? string.Empty).Contains(path, StringComparison.Ordinal) && d.Message.Contains(message, StringComparison.Ordinal));
    }

    // ---- names and nesting ----

    [Theory]
    [InlineData("app//child")]
    [InlineData("/leading")]
    [InlineData("trailing/")]
    public void ANameWithAnEmptySegment_IsInvalid(string name)
    {
        var diagnostics = Validate("\"resources\": [ { \"type\": \"Microsoft.Entra/applications/federatedIdentityCredentials\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"" + name + "\", \"properties\": { \"issuer\": \"https://i\", \"subject\": \"s\", \"audiences\": [\"a\"] } } ]");

        Assert.Contains(diagnostics, d => d.IsError && d.Code == DiagnosticCodes.InvalidResourceName);
    }

    [Fact]
    public void ANestedResourceOfTheWrongParentType_IsAParentChildTypeMismatch()
    {
        string json = "\"resources\": [ { \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"g\", \"properties\": { \"displayName\": \"G\" }, \"resources\": [ "
            + "{ \"type\": \"federatedIdentityCredentials\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"gh\", \"properties\": { \"issuer\": \"https://i\", \"subject\": \"s\", \"audiences\": [\"a\"] } } ] } ]";

        var diagnostics = Validate(json);

        Assert.Contains(diagnostics, d => d.IsError && d.Code is DiagnosticCodes.ParentChildTypeMismatch or DiagnosticCodes.UnknownResourceType);
    }

    [Fact]
    public void ANestedUnknownChildType_IsUnknownResourceType_AndAChildTypeAtTheTopLevelIsRefused()
    {
        string nested = "\"resources\": [ { \"type\": \"Microsoft.Entra/applications\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"a\", \"properties\": { \"displayName\": \"A\" }, \"resources\": [ "
            + "{ \"type\": \"noSuchChild\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"c\", \"properties\": { } } ] } ]";
        string topLevel = "\"resources\": [ { \"type\": \"Microsoft.Entra/applications/federatedIdentityCredentials\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"onlyOneSegment\", \"properties\": { } } ]";

        Assert.Contains(Validate(nested), d => d.IsError && d.Code == DiagnosticCodes.UnknownResourceType);
        Assert.Contains(Validate(topLevel), d => d.IsError);
    }

    // ---- parameter types ----

    [Theory]
    [InlineData("string", "\"x\"", true)]
    [InlineData("string", "5", false)]
    [InlineData("securestring", "\"x\"", true)]
    [InlineData("int", "5", true)]
    [InlineData("int", "5.0", true)]
    [InlineData("int", "1.5e3", true)]
    [InlineData("int", "-0.0", true)]
    [InlineData("int", "1e19", false)]
    [InlineData("int", "9223372036854775808", false)]
    [InlineData("int", "5.5", false)]
    [InlineData("int", "\"5\"", false)]
    [InlineData("bool", "true", true)]
    [InlineData("bool", "\"true\"", false)]
    [InlineData("object", "{}", true)]
    [InlineData("object", "[]", false)]
    [InlineData("secureObject", "{ \"a\": 1 }", true)]
    [InlineData("array", "[1]", true)]
    [InlineData("array", "{}", false)]
    [InlineData("string", "\"[parameters('other')]\"", true)]
    [InlineData("int", "\"[add(1, 1)]\"", true)]
    [InlineData("string", "null", false)]
    public void ParameterTypes_AcceptOnlyMatchingLiterals_AndAnyExpression(string type, string literal, bool matches)
    {
        Assert.True(TemplateParameterTypes.TryParse(type, out var parsed));

        Assert.Equal(matches, TemplateParameterTypes.MatchesTemplateText(System.Text.Json.Nodes.JsonNode.Parse(literal), parsed));
        Assert.Equal(type, TemplateParameterTypes.CanonicalName(parsed));
    }

    [Fact]
    public void ParameterTypes_AreCaseInsensitive_ReportSecurity_AndRejectUnknownNames()
    {
        Assert.True(TemplateParameterTypes.TryParse("SECURESTRING", out var secure));
        Assert.True(TemplateParameterTypes.IsSecure(secure));
        Assert.True(TemplateParameterTypes.TryParse("secureobject", out var secureObject));
        Assert.True(TemplateParameterTypes.IsSecure(secureObject));
        Assert.True(TemplateParameterTypes.TryParse("Int", out var integer));
        Assert.False(TemplateParameterTypes.IsSecure(integer));
        Assert.False(TemplateParameterTypes.TryParse("long", out _));
        Assert.False(TemplateParameterTypes.TryParse(string.Empty, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => TemplateParameterTypes.CanonicalName((TemplateParameterType)99));
    }
}
