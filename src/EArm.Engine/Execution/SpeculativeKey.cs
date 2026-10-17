using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using EArm.Expressions;
using EArm.Expressions.Evaluation;

namespace EArm.Engine.Execution;

/// <summary>
/// The key of an instance that a <see cref="PlanMode.Create"/> plan (<c>create -y</c>) has not evaluated yet, worked out before the run starts so that
/// <see cref="KeyAbsencePrefetch"/> can begin its extra reads early. The evaluation is a dry run in a sandbox that shares no cache with the run:
/// <list type="bullet">
/// <item>every <c>reference()</c> is a placeholder (<see cref="StaticReferenceResolver"/>): no existing-resource lookup is made, so nothing (a NotFound in
/// particular) lands in the cache of the run's <see cref="LiveReferenceResolver"/> before the earlier batches have created what it names;</item>
/// <item>every tenant lookup (<c>appRole()</c>, <c>roleTemplate()</c>...) is a placeholder (<see cref="PlaceholderEntraLookup"/>), so the run's lookup cache
/// is not filled either;</item>
/// <item>variables are evaluated in a store of their own (the run memoises its variables; a variable that looks an existing resource up would otherwise keep
/// a placeholder for the rest of the run), and parameters are read without rebinding the run's store (parameter defaults cannot use live data);</item>
/// <item>the work is counted against a budget of its own, and the instance itself is left untouched (a scratch copy is evaluated: its condition and
/// properties are evaluated again, for real, when its turn comes).</item>
/// </list>
/// A key that depends on anything only known at deployment comes out unresolved and the instance is simply not prefetched. Only instances of the main
/// template are considered (a nested template has its own parameter and variable stores).
/// </summary>
internal static class SpeculativeKey
{
    /// <summary>The key of <paramref name="instance"/> when its condition is known and true and its key does not depend on deploy-time values; otherwise <c>null</c>.</summary>
    /// <param name="conditionFalse">Set when the condition is known to be false (the instance is not deployed).</param>
    public static ResourceKey? Resolve(DeploymentPlan plan, ResourceInstance instance, IResourceProvider provider, out bool conditionFalse)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(provider);
        conditionFalse = false;
        if (instance.Scope is { IsRoot: false } || instance.Context.Parameters is not LazyParameters parameters)
        {
            return null;
        }

        try
        {
            var context = (instance.Context with
            {
                Parameters = new ParameterView(parameters),
                Variables = new LazyVariables(plan.Document.Variables, plan.Document.VariableIterations),
                References = StaticReferenceResolver.Instance,
                EntraLookup = PlaceholderEntraLookup.Instance,
                Budget = new EvaluationBudget(EvaluationLimits.FromEnvironment()),
            }).Bind(plan.Evaluator);

            var condition = InstanceEvaluator.EvaluateCondition(plan.Evaluator, instance.Declaration, context, out var conditionError);
            if (conditionError is not null || condition is not true)
            {
                conditionFalse = condition == false;
                return null;
            }

            var scratch = new ResourceInstance(instance.Declaration, instance.Resolved, instance.Definition, instance.DeclarationIndex, instance.Id, instance.IsSymbolic, instance.NameValue, context, instance.Copy)
            {
                Scope = instance.Scope,
                Parent = instance.Parent,
                ParentGraphId = instance.ParentGraphId,
            };
            InstanceEvaluator.EvaluateProperties(plan.Evaluator, scratch);
            var key = provider.ResolveKey(scratch);
            return key.IsResolved ? key : null;
        }
        catch (Exception ex) when (ex is ExpressionException or InvalidOperationException or ArgumentException or ProviderException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// The run's parameters, read through: not a <see cref="LazyParameters"/>, so <see cref="EvaluationContext.Bind"/> leaves the run's store bound to the
    /// run's context. The values are the ones the run reads (parameter defaults cannot use live data).
    /// </summary>
    private sealed class ParameterView(IReadOnlyDictionary<string, JsonNode?> inner) : IReadOnlyDictionary<string, JsonNode?>
    {
        public JsonNode? this[string key] => inner[key];

        public IEnumerable<string> Keys => inner.Keys;

        public IEnumerable<JsonNode?> Values => inner.Values;

        public int Count => inner.Count;

        public bool ContainsKey(string key) => inner.ContainsKey(key);

        public bool TryGetValue(string key, [MaybeNullWhen(false)] out JsonNode? value) => inner.TryGetValue(key, out value);

        public IEnumerator<KeyValuePair<string, JsonNode?>> GetEnumerator() => inner.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
