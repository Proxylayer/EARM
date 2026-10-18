using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Core.Secrets;
using EArm.Engine.Providers;
using EArm.Expressions;
using EArm.Expressions.Evaluation;
using EArm.Expressions.Values;

namespace EArm.Engine.Planning.Nested;

/// <summary>
/// The outputs of a nested deployment, as <c>reference('&lt;deployment&gt;')</c> returns them in ARM: <c>{ outputs: { name: { type, value } }, ... }</c>
/// (https://learn.microsoft.com/azure/azure-resource-manager/templates/linked-templates#get-values-from-linked-template). They are evaluated in the
/// nested template's own context once every resource of the template is planned or applied (the deployment waits for all of them in the graph) and
/// published under the deployment's id, so <c>reference('dep').outputs.x.value</c> and a languageVersion 2.0 <c>reference('sym')</c> read them.
/// A secure output (<c>secureString</c>, <c>secureObject</c>) stays tainted: it is usable in the parent and masked wherever it is shown.
/// </summary>
internal static class NestedDeploymentOutputs
{
    /// <summary>The result of an evaluation: the object to publish and the outputs that could not be evaluated (name and reason).</summary>
    public sealed record Result(JsonObject Value, IReadOnlyList<(string Output, string Problem)> Problems)
    {
        /// <summary>True when some output value holds a deploy-time placeholder (what-if: a value that depends on a resource still to be created).</summary>
        public bool HasUnknown => UnknownJson.ContainsUnknown(Value);
    }

    /// <summary>Evaluates the outputs of the template <paramref name="deployment"/> expanded (<see cref="ResourceInstance.NestedTemplate"/>).</summary>
    public static Result Evaluate(ResourceInstance deployment, ExpressionEvaluator evaluator)
    {
        ArgumentNullException.ThrowIfNull(deployment);
        ArgumentNullException.ThrowIfNull(evaluator);

        var outputs = new JsonObject();
        var problems = new List<(string, string)>();
        if (deployment.NestedTemplate is { } scope)
        {
            var context = scope.Context.ForScope(EvaluationScope.Output);
            foreach (var output in scope.Document.Outputs.Values)
            {
                try
                {
                    if (output.Condition is not null)
                    {
                        var condition = evaluator.EvaluateTree(output.Condition, context);
                        if (UnknownJson.IsUnknown(condition, out _))
                        {
                            outputs[output.Name] = new JsonObject { ["type"] = output.Type ?? "string", ["value"] = UnknownJson.Create($"reference('{deployment.Id.Name}').outputs.{output.Name}", UnknownOrigins.Reference) };
                            continue;
                        }

                        if (!JsonValues.TryGetBool(condition, out bool include))
                        {
                            problems.Add((output.Name, "the condition does not evaluate to a boolean"));
                            continue;
                        }

                        if (!include)
                        {
                            continue;
                        }
                    }

                    var value = CopyExpansion.EvaluateOutput(evaluator, output, context);
                    if (output.IsSecure)
                    {
                        value = SecretJson.Taint(value);
                    }

                    outputs[output.Name] = new JsonObject { ["type"] = output.Type ?? "string", ["value"] = value };
                }
                catch (ExpressionException ex)
                {
                    problems.Add((output.Name, ex.MessageWithOffset));
                }
            }
        }

        var published = new JsonObject
        {
            ["outputs"] = outputs,
            ["mode"] = "Incremental",
            ["provisioningState"] = "Succeeded",
        };
        return new Result(published, problems);
    }

    /// <summary>Publishes <paramref name="result"/> for <paramref name="deployment"/>: later <c>reference()</c> calls read it.</summary>
    public static void Publish(LiveReferenceResolver resolver, ResourceInstance deployment, Result result)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(deployment);
        ArgumentNullException.ThrowIfNull(result);
        resolver.Publish(deployment.Id, result.Value, deployment.Id.Id, deployment.PlannedId);
    }

    /// <summary>One line naming the outputs that could not be evaluated.</summary>
    public static string Describe(ResourceInstance deployment, Result result)
    {
        ArgumentNullException.ThrowIfNull(deployment);
        ArgumentNullException.ThrowIfNull(result);
        return $"Nested deployment '{deployment.Id.Name}': " + string.Join("; ", result.Problems.Select(p => $"output '{p.Output}' could not be evaluated: {p.Problem}"));
    }
}
