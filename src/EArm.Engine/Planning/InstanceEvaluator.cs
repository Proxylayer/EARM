using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Json;
using EArm.Core.Secrets;
using EArm.Core.Types;
using EArm.Engine.Providers;
using EArm.Expressions;
using EArm.Expressions.Evaluation;
using EArm.Expressions.Values;

namespace EArm.Engine.Planning;

/// <summary>The outcome of evaluating a resource name in a concrete context.</summary>
/// <param name="Id">Concrete, or symbolic (the name segment is the unknown's symbol) when <paramref name="IsSymbolic"/>; <c>null</c> on error.</param>
public sealed record NameEvaluation(ResourceIdentifier? Id, bool IsSymbolic, JsonNode? Value, string? ErrorCode, string? Error)
{
    public bool IsConcrete => Id is not null && !IsSymbolic;
}

/// <summary>
/// Evaluates the per-instance parts of a declaration (name, condition, properties) in the instance's context: the
/// deploy-time counterpart of the static evaluator, run once a concrete copy frame and the live resolver are known.
/// </summary>
public static class InstanceEvaluator
{
    /// <summary>Evaluates <c>name</c>: literal or from parameters/variables → concrete; depends on an unresolved <c>reference()</c> → symbolic.</summary>
    public static NameEvaluation EvaluateName(ExpressionEvaluator evaluator, ResourceDeclaration declaration, ResourceTypeDefinition definition, EvaluationContext context)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(declaration);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(context);

        if (declaration.Name is null)
        {
            return new NameEvaluation(null, false, null, DiagnosticCodes.MissingResourceName, "The resource has no name.");
        }

        JsonNode? value;
        try
        {
            value = evaluator.EvaluateString(declaration.Name, context);
        }
        catch (ExpressionException ex)
        {
            return new NameEvaluation(null, false, null, ex.Code, ex.MessageWithOffset);
        }

        if (UnknownJson.ContainsTaint(value))
        {
            return new NameEvaluation(null, false, value, DiagnosticCodes.SecureValueInResourceKey, "A secure value flows into the resource name.");
        }

        if (UnknownJson.IsUnknown(value, out var unknown))
        {
            if ((unknown.Origins & UnknownOrigins.Copy) != 0)
            {
                return new NameEvaluation(null, false, value, DiagnosticCodes.CopyIndexOutsideCopy, $"The resource name depends on copyIndex() but no concrete copy iteration is active ({unknown.Symbol}).");
            }

            return new NameEvaluation(new ResourceIdentifier(definition.Type, [unknown.Symbol]), true, value, null, null);
        }

        if (!JsonValues.TryGetString(value, out var name))
        {
            return new NameEvaluation(null, false, value, DiagnosticCodes.ResourceNameNotString, $"The resource name must evaluate to a string, got {JsonValues.Describe(value)}.");
        }

        var segments = name.Split('/');
        if (segments.Length != definition.Depth || segments.Any(string.IsNullOrEmpty))
        {
            string expected = definition.Depth == 1 ? "a single segment without '/'" : $"{definition.Depth} segments ('parent/child')";
            return new NameEvaluation(null, false, value, DiagnosticCodes.InvalidResourceName, $"Name '{name}' (evaluated) is not valid for type '{definition.Type}': expected {expected}.");
        }

        // Names computed by an expression face the same rules as literal names (nameKind, namePattern, no control characters, ...).
        if (ResourceNameRules.Validate(definition, name) is { } nameProblem)
        {
            return new NameEvaluation(null, false, value, DiagnosticCodes.InvalidResourceName, nameProblem);
        }

        return new NameEvaluation(new ResourceIdentifier(definition.Type, segments), false, value, null, null);
    }

    /// <summary>Evaluates <c>condition</c>: <c>true</c> when absent; <c>null</c> when it still depends on a deploy-time value.</summary>
    public static bool? EvaluateCondition(ExpressionEvaluator evaluator, ResourceDeclaration declaration, EvaluationContext context, out string? error)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(declaration);
        ArgumentNullException.ThrowIfNull(context);
        error = null;

        if (declaration.Condition is null)
        {
            return true;
        }

        JsonNode? value;
        try
        {
            value = evaluator.EvaluateTree(declaration.Condition, context);
        }
        catch (ExpressionException ex)
        {
            error = ex.MessageWithOffset;
            return null;
        }

        if (UnknownJson.IsUnknown(value))
        {
            return null;
        }

        if (JsonValues.TryGetBool(value, out bool result))
        {
            return result;
        }

        error = $"condition must evaluate to a boolean, got {JsonValues.Describe(value)}.";
        return null;
    }

    /// <summary>
    /// Evaluates <c>properties</c> in the instance's context and splits the result into object properties and the
    /// relationship/assignment properties of the type, which the edge pass applies later and
    /// which may legitimately still hold unknown values here. Object properties are evaluated strictly; an edge
    /// property whose expression cannot be evaluated yet (a <c>reference()</c> by the concrete id of a resource whose
    /// symbolic name has not resolved) becomes a deploy-time placeholder instead of failing the object pass, since
    /// the edge pass re-evaluates every edge property against the live resolver once all objects exist.
    /// </summary>
    public static void EvaluateProperties(ExpressionEvaluator evaluator, ResourceInstance instance)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(instance);

        if (instance.Declaration.Properties is { } whole and not JsonObject)
        {
            EvaluateWholeProperties(evaluator, instance, whole);
            return;
        }

        var objectRaw = new JsonObject();
        var edgeRaw = new JsonObject();
        var edgeCopies = new HashSet<string>(StringComparer.Ordinal);
        if (instance.Declaration.Properties is JsonObject declared)
        {
            var objectCopies = new JsonArray();
            foreach (var property in declared)
            {
                if (PropertyCopyBlock.IsBlock(property.Key, property.Value))
                {
                    continue;
                }

                (instance.Definition.IsRelationshipProperty(property.Key) ? edgeRaw : objectRaw)[property.Key] = JsonValues.Clone(property.Value);
            }

            // Property iteration entries go where the property they build goes: an edge property is evaluated on its own
            // (a one-entry block whose result is that property), an object property stays in a block of the object properties.
            foreach (var entry in PropertyCopyBlock.Entries(declared))
            {
                if (entry.Name is not null && instance.Definition.IsRelationshipProperty(entry.Name))
                {
                    edgeRaw[entry.Name] = new JsonObject { [PropertyCopyBlock.Key] = new JsonArray(JsonValues.Clone(entry.Source)) };
                    edgeCopies.Add(entry.Name);
                }
                else
                {
                    objectCopies.Add(JsonValues.Clone(entry.Source));
                }
            }

            if (objectCopies.Count > 0)
            {
                objectRaw[PropertyCopyBlock.Key] = objectCopies;
            }
        }

        var objectProperties = evaluator.EvaluateTree(objectRaw, instance.Context) as JsonObject ?? new JsonObject();
        TaintSecrets(objectProperties, instance.Definition.Secure);
        var edgeProperties = new JsonObject();
        foreach (var property in edgeRaw)
        {
            JsonNode? value;
            try
            {
                value = edgeCopies.Contains(property.Key)
                    ? (evaluator.EvaluateTree(property.Value, instance.Context) as JsonObject)?[property.Key]
                    : evaluator.EvaluateTree(property.Value, instance.Context);
            }
            catch (ExpressionException ex)
            {
                value = UnknownJson.Create($"properties.{property.Key} ({ex.Code})", UnknownOrigins.Reference);
            }

            edgeProperties[property.Key] = value?.Parent is null ? value : JsonValues.Clone(value);
        }

        instance.Properties = objectProperties;
        instance.EdgeProperties = edgeProperties;
        instance.HasUnknownProperties = UnknownJson.ContainsUnknown(objectProperties);
    }

    /// <summary>
    /// The declaration of one instance whose whole <c>properties</c> is a single expression (<c>"properties": "[variables('groups')[copyIndex()].properties]"</c>,
    /// which ARM accepts): the expression is evaluated in the instance's context, and when it yields an object the returned copy of the declaration
    /// declares one expression per key of that object, each selecting its key from the same expression
    /// (<c>"displayName": "[variables('groups')[copyIndex()].properties['displayName']]"</c>). Everything that reads the declared properties (the
    /// split into object and edge properties, the edge pass, declaration conflicts, apply orders, the permission manifest) then sees which properties
    /// the instance declares, exactly as for an object literal, and every value is still evaluated by the evaluator, so secrets stay tainted and
    /// deploy-time values stay unknown. The declaration is returned unchanged for an object literal, a template-level resource (a nested deployment
    /// reads its own <c>properties</c>), and when the expression fails or its value is only known at deployment
    /// (<see cref="EvaluateProperties"/> then evaluates it as a whole).
    /// </summary>
    public static ResourceDeclaration WithPropertiesExpressionSplit(ExpressionEvaluator evaluator, ResourceDeclaration declaration, ResourceTypeDefinition definition, EvaluationContext context)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(declaration);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(context);

        if (definition.IsTemplateLevel || TemplateJson.AsString(declaration.Properties) is not { } expression || !ExpressionSyntax.IsExpression(expression))
        {
            return declaration;
        }

        JsonNode? value;
        try
        {
            value = evaluator.EvaluateTree(declaration.Properties, context);
        }
        catch (ExpressionException)
        {
            return declaration;
        }

        if (value is not JsonObject evaluated)
        {
            return declaration;
        }

        string inner = expression[1..^1];
        var split = new JsonObject();
        foreach (var property in evaluated)
        {
            split[property.Key] = JsonValue.Create($"[{inner}['{property.Key.Replace("'", "''", StringComparison.Ordinal)}']]");
        }

        return declaration with { Properties = split };
    }

    /// <summary>
    /// The symbol of the value of <c>properties</c> when it is one expression that was not split at expansion and whose value is still only known
    /// at deployment (it reads <c>reference()</c> of a resource the same run creates); <c>null</c> otherwise. The what-if shows such a resource as a
    /// "known at deployment" row instead of failing it: <see cref="EvaluateProperties"/> can evaluate it once the referenced resource exists. An
    /// expression that fails is left to <see cref="EvaluateProperties"/>, which reports the error.
    /// </summary>
    public static string? WholePropertiesKnownOnlyAtDeployment(ExpressionEvaluator evaluator, ResourceInstance instance)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(instance);
        if (instance.Declaration.Properties is not { } whole || whole is JsonObject)
        {
            return null;
        }

        try
        {
            return UnknownJson.IsUnknown(evaluator.EvaluateTree(whole, instance.Context), out var unknown) ? unknown.Symbol : null;
        }
        catch (ExpressionException)
        {
            return null;
        }
    }

    /// <summary>
    /// <c>properties</c> declared as one expression that could not be split at expansion (<see cref="WithPropertiesExpressionSplit"/>): its value is
    /// evaluated as a whole and split by key into object and edge properties. A value only known at deployment cannot be split (which of its keys
    /// are edges is not known) and fails the instance (the what-if shows it as known at deployment first:
    /// <see cref="WholePropertiesKnownOnlyAtDeployment"/>).
    /// </summary>
    private static void EvaluateWholeProperties(ExpressionEvaluator evaluator, ResourceInstance instance, JsonNode whole)
    {
        var value = evaluator.EvaluateTree(whole, instance.Context);
        if (UnknownJson.IsUnknown(value, out var unknown))
        {
            throw new ExpressionEvaluationException(
                DiagnosticCodes.ExpressionEvaluationFailed,
                $"properties is a single expression whose value is only known at deployment ({unknown.Symbol}): which properties the resource declares must be known when it is planned; declare properties as an object whose values are expressions.");
        }

        var evaluated = value switch
        {
            null => new JsonObject(),
            JsonObject obj => obj,
            _ => throw new ExpressionEvaluationException(DiagnosticCodes.ExpressionEvaluationFailed, $"properties must evaluate to an object, got {JsonValues.Describe(value)}."),
        };

        var objectProperties = new JsonObject();
        var edgeProperties = new JsonObject();
        foreach (var property in evaluated.ToList())
        {
            var node = property.Value;
            evaluated.Remove(property.Key);
            (instance.Definition.IsRelationshipProperty(property.Key) ? edgeProperties : objectProperties)[property.Key] = node;
        }

        TaintSecrets(objectProperties, instance.Definition.Secure);
        instance.Properties = objectProperties;
        instance.EdgeProperties = edgeProperties;
        instance.HasUnknownProperties = UnknownJson.ContainsUnknown(objectProperties);
    }

    /// <summary>The unknown leaves of the evaluated object properties, for messages.</summary>
    public static IReadOnlyList<(Core.Json.JsonPath Path, string Symbol)> UnknownLeaves(ResourceInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return JsonObjects.UnknownLeaves(instance.Properties, Core.Json.JsonPath.Of("properties"));
    }

    /// <summary>
    /// Taints, in place, every leaf under the overlay's <c>secure</c> paths (<c>passwordProfile.password</c>,
    /// <c>secretText</c>) exactly as a <c>securestring</c> parameter would: a plain leaf becomes a
    /// <see cref="SecretJson"/> leaf, a deploy-time-unknown leaf keeps its symbol and is marked tainted, and leaves
    /// that are already secret, already tainted or <c>null</c> are left alone, so calling it twice (the engine
    /// here, a provider again in its own <c>Plan</c>) is a no-op. A literal secret in a template therefore renders
    /// as <c>***</c> in what-if rows, diagnostics and logs; only <c>ProviderBase.BuildCreateBody</c>
    /// / <c>BuildPatchBody</c> reveal it. Property names match case-insensitively; a path segment that does not
    /// exist is skipped.
    /// </summary>
    public static void TaintSecrets(JsonObject properties, IReadOnlyList<JsonPath> securePaths)
    {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(securePaths);
        foreach (var path in securePaths)
        {
            if (path.IsRoot)
            {
                continue;
            }

            TaintAt(properties, path.Segments, 0);
        }
    }

    /// <summary>Walks one secure path (property names case-insensitive, array indexes literal) and taints what it ends at.</summary>
    private static void TaintAt(JsonNode? node, IReadOnlyList<JsonPathSegment> segments, int depth)
    {
        var segment = segments[depth];
        bool last = depth == segments.Count - 1;
        switch (node)
        {
            case JsonObject obj when !segment.IsIndex && JsonObjects.FindKey(obj, segment.Name!) is { } key:
                if (last)
                {
                    Replace(obj, key, TaintLeaves(obj[key]));
                }
                else
                {
                    TaintAt(obj[key], segments, depth + 1);
                }

                break;

            case JsonArray array when segment.IsIndex && segment.Index < array.Count:
                if (last)
                {
                    Replace(array, segment.Index, TaintLeaves(array[segment.Index]));
                }
                else
                {
                    TaintAt(array[segment.Index], segments, depth + 1);
                }

                break;
        }
    }

    /// <summary>
    /// Taints the leaves under <paramref name="node"/>: containers are mutated in place and returned as is; a
    /// value returns the leaf to store in its place (itself when nothing changes).
    /// </summary>
    private static JsonNode? TaintLeaves(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;

            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToList())
                {
                    Replace(obj, key, TaintLeaves(obj[key]));
                }

                return obj;

            case JsonArray array:
                for (int i = 0; i < array.Count; i++)
                {
                    Replace(array, i, TaintLeaves(array[i]));
                }

                return array;

            default:
                if (SecretJson.IsSecret(node))
                {
                    return node;
                }

                if (UnknownJson.IsUnknown(node, out var unknown))
                {
                    return unknown.Tainted ? node : UnknownJson.Create(unknown with { Tainted = true });
                }

                if (node is JsonValue value && value.GetValueKind() == JsonValueKind.Null)
                {
                    return node;
                }

                return SecretJson.Create(SecretValue.FromLeaf(node)!);
        }
    }

    /// <summary>Stores a replacement leaf; an unchanged node is left where it is (re-parenting a node to itself throws).</summary>
    private static void Replace(JsonObject obj, string key, JsonNode? replacement)
    {
        if (!ReferenceEquals(obj[key], replacement))
        {
            obj[key] = replacement;
        }
    }

    private static void Replace(JsonArray array, int index, JsonNode? replacement)
    {
        if (!ReferenceEquals(array[index], replacement))
        {
            array[index] = replacement;
        }
    }
}
