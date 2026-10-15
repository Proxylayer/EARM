using System;
using System.Collections.Generic;
using EArm.Core.Json;
using EArm.Engine.Providers;
using EArm.Expressions.Evaluation;
using Microsoft.Extensions.Logging;

namespace EArm.Engine.Planning;

/// <summary>
/// Re-evaluates the names of symbolic instances right before their batch runs (a resource whose
/// name is <c>[reference('app').appId]</c> gets its real name once <c>app</c> is live). Runs sequentially between
/// batches so the resource index is never mutated while a batch evaluates expressions in parallel. A resolved
/// instance keeps its planned (symbolic) id as an alias: dependents reference it by symbol.
/// </summary>
internal static class SymbolicNames
{
    /// <summary>Resolves what can be resolved; returns the instances whose name is still unknown, with the reason.</summary>
    public static IReadOnlyList<(ResourceInstance Instance, string Reason)> Resolve(DeploymentPlan plan, DeploymentBatch batch, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(logger);

        var unresolved = new List<(ResourceInstance, string)>();
        foreach (var instance in batch.Instances)
        {
            if (!instance.IsSymbolic)
            {
                LinkParent(plan, instance);
                continue;
            }

            var evaluation = InstanceEvaluator.EvaluateName(plan.Evaluator, instance.Declaration, instance.Definition, instance.Context);
            if (evaluation.IsConcrete && evaluation.Id is { } id)
            {
                instance.Id = id;
                instance.IsSymbolic = false;
                instance.NameValue = evaluation.Value;
                plan.Index.Add(new TemplateResourceIndex.Entry(id, false, instance.Declaration, TemplateJson.LiteralString(instance.Declaration.Copy?.Name)));
                plan.RegisterResolvedId(instance);
                LinkParent(plan, instance);
                EngineLog.SymbolicNameResolved(logger, instance.PlannedId.Id, id.Id);
                continue;
            }

            string reason = evaluation.Error
                ?? $"the name still depends on a value that is not available ({instance.PlannedId.NameLeaf}); the referenced resource was not created in this run";
            unresolved.Add((instance, reason));
        }

        return unresolved;
    }

    /// <summary>Links a child to its parent instance once both names are concrete, and copies the parent's Graph id when known.</summary>
    public static void LinkParent(DeploymentPlan plan, ResourceInstance instance)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(instance);

        if (instance.ParentId is not { } parentId)
        {
            return;
        }

        if (instance.Parent is null && plan.FindInstance(parentId) is { } parent)
        {
            instance.Parent = parent;
        }

        if (instance.ParentGraphId is null)
        {
            if (instance.Parent is { } linked && (plan.Resolver.TryGetGraphId(linked.Id, out var graphId) || plan.Resolver.TryGetGraphId(linked.PlannedId, out graphId)))
            {
                instance.ParentGraphId = graphId;
            }
            else if (plan.Resolver.TryGetGraphId(parentId, out var externalId))
            {
                instance.ParentGraphId = externalId;
            }
        }
    }
}
