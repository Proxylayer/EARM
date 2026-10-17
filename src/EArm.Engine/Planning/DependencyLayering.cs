using System;
using System.Collections.Generic;
using System.Linq;
using EArm.Engine.Providers;

namespace EArm.Engine.Planning;

/// <summary>
/// The layering of an acyclic dependency graph (<see cref="DependencyGraph"/>), Kahn-style: level = 1 + the highest level among the dependencies, each
/// batch ordered by apply phase then declaration order; and the depth-first search that finds a cycle first.
/// </summary>
internal static class DependencyLayering
{
    /// <summary>The batches of an acyclic graph (<see cref="FindCycle"/> found none).</summary>
    public static IReadOnlyList<DeploymentBatch> Batches(IReadOnlyList<ResourceInstance> instances, IReadOnlyList<DependencyEdge> edges)
    {
        var dependencies = instances.ToDictionary(i => i, _ => new List<ResourceInstance>());
        var dependents = instances.ToDictionary(i => i, _ => new List<ResourceInstance>());
        foreach (var edge in edges)
        {
            dependencies[edge.Dependent].Add(edge.Dependency);
            dependents[edge.Dependency].Add(edge.Dependent);
        }

        var remaining = instances.ToDictionary(i => i, i => dependencies[i].Count);
        var level = new Dictionary<ResourceInstance, int>();
        var queue = new Queue<ResourceInstance>(instances.Where(i => remaining[i] == 0));
        foreach (var root in queue)
        {
            level[root] = 1;
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var dependent in dependents[current])
            {
                level[dependent] = Math.Max(level.GetValueOrDefault(dependent), level[current] + 1);
                if (--remaining[dependent] == 0)
                {
                    queue.Enqueue(dependent);
                }
            }
        }

        var batches = new List<DeploymentBatch>();
        foreach (var group in instances.GroupBy(i => level[i]).OrderBy(g => g.Key))
        {
            var ordered = group
                .OrderBy(i => i.Phase)
                .ThenBy(i => i.DeclarationIndex)
                .ThenBy(i => i.Copy?.Index ?? 0)
                .ToList();
            batches.Add(new DeploymentBatch(batches.Count + 1, ordered));
        }

        return batches;
    }

    /// <summary>Depth-first search for a back edge; returns the cycle path (first node repeated at the end) or <c>null</c>.</summary>
    public static List<ResourceInstance>? FindCycle(IReadOnlyList<ResourceInstance> instances, IReadOnlyList<DependencyEdge> edges)
    {
        var dependencies = instances.ToDictionary(i => i, _ => new List<ResourceInstance>());
        foreach (var edge in edges)
        {
            dependencies[edge.Dependent].Add(edge.Dependency);
        }

        var state = new Dictionary<ResourceInstance, int>();
        var stack = new List<ResourceInstance>();
        foreach (var start in instances)
        {
            if (state.GetValueOrDefault(start) != 0)
            {
                continue;
            }

            if (Visit(start, dependencies, state, stack) is { } cycle)
            {
                return cycle;
            }
        }

        return null;
    }

    private static List<ResourceInstance>? Visit(ResourceInstance node, Dictionary<ResourceInstance, List<ResourceInstance>> dependencies, Dictionary<ResourceInstance, int> state, List<ResourceInstance> stack)
    {
        state[node] = 1;
        stack.Add(node);
        foreach (var next in dependencies[node])
        {
            int nextState = state.GetValueOrDefault(next);
            if (nextState == 1)
            {
                int start = stack.IndexOf(next);
                var cycle = stack.Skip(start).ToList();
                cycle.Add(next);
                return cycle;
            }

            if (nextState == 0 && Visit(next, dependencies, state, stack) is { } found)
            {
                return found;
            }
        }

        stack.RemoveAt(stack.Count - 1);
        state[node] = 2;
        return null;
    }
}
