using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using EntraArm.Core;
using EntraArm.Core.Diagnostics;
using EntraArm.Core.Loading;
using EntraArm.Expressions;
using EntraArm.Expressions.Evaluation;
using EntraArm.Expressions.Functions;
using EntraArm.Expressions.Syntax;
using EntraArm.Expressions.Values;
using Xunit;

namespace EntraArm.Tests;

/// <summary>Shared helpers for the expression-engine tests: contexts, evaluation, JSON comparison, templates.</summary>
internal static class Expr
{
    public const string SchemaUri = "https://entraarm.dev/schemas/2026-10-01/deploymentTemplate.json#";

    /// <summary>The placeholder symbol of <c>reference('app')</c> for an application named <c>app</c>.</summary>
    public const string AppRef = "ref(/providers/Microsoft.Entra/applications/app)";

    public static ExpressionEvaluator Evaluator => ExpressionEvaluator.Default;

    /// <summary>A case-insensitive parameter dictionary.</summary>
    public static Dictionary<string, JsonNode?> Parameters(params (string Name, JsonNode? Value)[] values)
    {
        var dictionary = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in values)
        {
            dictionary[name] = value;
        }

        return dictionary;
    }

    /// <summary>A bound context; resource scope, no copy loop, static (placeholder) references by default.</summary>
    public static EvaluationContext Context(
        IReadOnlyDictionary<string, JsonNode?>? parameters = null,
        IReadOnlyDictionary<string, JsonNode?>? variables = null,
        TemplateResourceIndex? resources = null,
        EvaluationScope scope = EvaluationScope.Resource,
        IReferenceResolver? references = null,
        DeploymentInfo? deployment = null,
        FunctionRegistry? functions = null,
        IReadOnlyList<UserFunctionNamespace>? userFunctions = null)
    {
        var context = new EvaluationContext
        {
            Parameters = parameters ?? Parameters(),
            Variables = variables is null ? LazyVariables.Empty : new LazyVariables(variables),
            Resources = resources ?? TemplateResourceIndex.Empty,
            Scope = scope,
            References = references ?? StaticReferenceResolver.Instance,
            Deployment = deployment ?? DeploymentInfo.Offline,
            Functions = functions ?? FunctionRegistry.Default,
            UserFunctions = userFunctions ?? [],
        };

        return context.Bind(Evaluator);
    }

    /// <summary>An index of literal-named resources.</summary>
    public static TemplateResourceIndex Index(params (string Type, string Name)[] resources)
    {
        var index = new TemplateResourceIndex();
        foreach (var (type, name) in resources)
        {
            index.Add(new TemplateResourceIndex.Entry(ResourceIdentifier.Create(type, name), false, null));
        }

        return index;
    }

    public static JsonNode? Eval(string text, EvaluationContext? context = null)
    {
        return Evaluator.EvaluateString(text, context ?? Context());
    }

    /// <summary>Evaluates and asserts failure with the given diagnostic code.</summary>
    public static ExpressionEvaluationException Fails(string text, string expectedCode, EvaluationContext? context = null)
    {
        var exception = Assert.Throws<ExpressionEvaluationException>(() => Eval(text, context));
        Assert.Equal(expectedCode, exception.Code);
        return exception;
    }

    public static ExpressionParseException ParseFails(string text, int expectedOffset, string expectedCode = DiagnosticCodes.ExpressionSyntaxError)
    {
        var exception = Assert.Throws<ExpressionParseException>(() => ExpressionParser.Parse(text));
        Assert.Equal(expectedCode, exception.Code);
        Assert.Equal(expectedOffset, exception.Offset);
        return exception;
    }

    /// <summary>Asserts structural equality (numbers by value, strings ordinal) against JSON text.</summary>
    public static void AssertJson(string expectedJson, JsonNode? actual)
    {
        var expected = JsonNode.Parse(expectedJson);
        Assert.True(JsonValues.AreEqual(expected, actual), $"expected {expectedJson} but got {Render(actual)}");
    }

    public static string Render(JsonNode? node)
    {
        return node is null ? "null" : node.ToJsonString();
    }

    public static UnknownValue AssertUnknown(JsonNode? node)
    {
        Assert.True(UnknownJson.IsUnknown(node, out var unknown), "expected a deploy-time unknown value but got " + Render(node));
        return unknown!;
    }

    public static string AssertString(JsonNode? node)
    {
        Assert.True(JsonValues.TryGetString(node, out var text), "expected a string but got " + Render(node));
        return text;
    }

    public static string Envelope(string body)
    {
        return "{ \"$schema\": \"" + SchemaUri + "\", \"contentVersion\": \"1.0.0.0\", " + body + " }";
    }

    public static TemplateDocument Parse(string json, ParameterSet? parameters = null)
    {
        return new TemplateLoader().Parse(json, "inline.entra.json", parameters ?? ParameterSet.Empty);
    }

    public static TemplateDocument ParseBody(string body, ParameterSet? parameters = null)
    {
        return Parse(Envelope(body), parameters);
    }
}
