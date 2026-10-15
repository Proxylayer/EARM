using System.Collections.Generic;
using System.Text.Json.Nodes;
using EArm.Core;
using EArm.Expressions.Static;
using EArm.Expressions.Syntax;

namespace EArm.Expressions.Evaluation;

/// <summary>
/// The template expression engine (plan section 2.2). Values are <see cref="JsonNode"/> trees; secure values are
/// <see cref="EArm.Core.Secrets.SecretValue"/> leaves and deploy-time-unknown values are
/// <see cref="Values.UnknownValue"/> leaves, both of which propagate through every function.
/// </summary>
public interface IExpressionEvaluator
{
    /// <summary>Parses a full expression string (<c>[...]</c>); throws <see cref="ExpressionParseException"/>.</summary>
    Expression Parse(string text);

    /// <summary>Evaluates a parsed expression; throws <see cref="ExpressionEvaluationException"/>.</summary>
    JsonNode? Evaluate(Expression expression, EvaluationContext context);

    /// <summary>
    /// Evaluates every expression string inside a JSON tree and returns a new tree (escaped literals unescaped,
    /// secret and unknown leaves preserved). The input is never mutated.
    /// </summary>
    JsonNode? EvaluateTree(JsonNode? node, EvaluationContext context);

    /// <summary>
    /// The same-template resources referenced through <c>reference(...)</c> (and <c>resourceId(...)</c> arguments
    /// that name a template resource) whose target can be resolved from literals, parameters and variables.
    /// Targets that cannot be resolved statically are reported by <see cref="ReferenceScanner"/>; this method
    /// returns only the resolvable set.
    /// </summary>
    IReadOnlySet<ResourceIdentifier> FindReferences(JsonNode? node, StaticContext context);
}
