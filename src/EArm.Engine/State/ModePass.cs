using System;
using System.Collections.Generic;
using System.Linq;
using EArm.Engine.Planning;
using EArm.Engine.Providers;

namespace EArm.Engine.State;

/// <summary>
/// The mode pass (plan section 2.4, step 8): state entries whose resource id is no longer among the expanded
/// instances of the template. In Incremental mode they are detached from the state (what-if: <c>Ignore</c>); in
/// Complete mode they are deleted in reverse dependency order (what-if: <c>Delete</c>).
/// </summary>
public static class ModePass
{
    /// <summary>What-if reason / executor reason for an Incremental-mode orphan.</summary>
    public const string DetachReasonWhatIf = "not in template; will be detached from state";

    public const string DetachReason = "not in template; detached from state";

    public const string DeleteReason = "not in template; deleted (Complete mode)";

    /// <summary>
    /// The state entries not declared by the plan, in delete order. Ids are compared concretely: copy instances by
    /// their expanded ids, resolved symbolic names by their resolved ids. A <c>condition: false</c> instance still
    /// counts as declared. When an instance's name is still symbolic (its input does not exist yet), entries of that
    /// type cannot be judged and are left alone; their types are returned in <paramref name="undecidedTypes"/>.
    /// </summary>
    public static IReadOnlyList<StateEntry> FindOrphans(StateFile state, DeploymentPlan plan, out IReadOnlyList<(string Type, string PlannedId)> undecidedTypes)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(plan);

        var declared = new HashSet<string>(StringComparer.Ordinal);
        var undecided = new List<(string, string)>();
        var undecidedTypeSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var instance in plan.Instances)
        {
            if (instance.IsSymbolic)
            {
                if (undecidedTypeSet.Add(instance.Type))
                {
                    undecided.Add((instance.Type, instance.PlannedId.Id));
                }

                continue;
            }

            declared.Add(instance.Id.Id);
        }

        undecidedTypes = undecided;
        var orphans = state.Resources.Values
            .Where(entry => !declared.Contains(entry.Id.Id) && !undecidedTypeSet.Contains(entry.Type))
            .ToList();
        return OrderForDeletion(orphans);
    }

    /// <summary>
    /// Reverse dependency order over the entries' recorded <c>dependsOn</c> (an entry is deleted before every entry it
    /// depends on), ties broken by <see cref="ApplyPhases.DeleteOrderOf"/> and then by resource id. Dependencies on
    /// entries outside the set are ignored; a cycle (impossible for a graph the planner accepted) falls back to the
    /// phase order.
    /// </summary>
    public static IReadOnlyList<StateEntry> OrderForDeletion(IEnumerable<StateEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var byId = new Dictionary<string, StateEntry>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            byId[entry.Id.Id] = entry;
        }

        // dependents[x] = number of entries in the set that depend on x; x may only go once they are gone.
        var dependents = byId.Keys.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
        foreach (var entry in byId.Values)
        {
            foreach (var dependency in entry.DependsOn.Distinct(StringComparer.Ordinal))
            {
                if (!string.Equals(dependency, entry.Id.Id, StringComparison.Ordinal) && dependents.ContainsKey(dependency))
                {
                    dependents[dependency]++;
                }
            }
        }

        var ready = new PriorityQueue<StateEntry, (int Phase, string Id)>();
        foreach (var entry in byId.Values.Where(e => dependents[e.Id.Id] == 0))
        {
            ready.Enqueue(entry, PriorityOf(entry));
        }

        var ordered = new List<StateEntry>(byId.Count);
        var done = new HashSet<string>(StringComparer.Ordinal);
        while (ready.TryDequeue(out var next, out _))
        {
            if (!done.Add(next.Id.Id))
            {
                continue;
            }

            ordered.Add(next);
            foreach (var dependency in next.DependsOn.Distinct(StringComparer.Ordinal))
            {
                if (dependents.TryGetValue(dependency, out int count) && count > 0)
                {
                    dependents[dependency] = --count;
                    if (count == 0)
                    {
                        ready.Enqueue(byId[dependency], PriorityOf(byId[dependency]));
                    }
                }
            }
        }

        if (ordered.Count < byId.Count)
        {
            // A cycle among the remaining entries: fall back to the phase order for them.
            ordered.AddRange(byId.Values.Where(e => !done.Contains(e.Id.Id)).OrderBy(PriorityOf));
        }

        return ordered;
    }

    private static (int Phase, string Id) PriorityOf(StateEntry entry)
    {
        return (ApplyPhases.DeleteOrderOf(entry.Type), entry.Id.Id);
    }
}
