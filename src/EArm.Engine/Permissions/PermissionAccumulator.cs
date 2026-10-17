using System;
using System.Collections.Generic;
using System.Linq;

namespace EArm.Engine.Permissions;

/// <summary>Name → contributing resources and reasons, with the "a ReadWrite grant covers its Read variant" folding applied on build.</summary>
internal sealed class PermissionAccumulator
{
    private readonly SortedDictionary<string, (SortedSet<string> Resources, SortedSet<string> Reasons)> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string[] AnyOf, string[][] AllOf, SortedSet<string> Resources, SortedSet<string> Reasons)> _groups = new(StringComparer.OrdinalIgnoreCase);

    public void Add(string name, string resource, string reason)
    {
        if (!_items.TryGetValue(name, out var entry))
        {
            entry = (new SortedSet<string>(StringComparer.Ordinal), new SortedSet<string>(StringComparer.Ordinal));
            _items[name] = entry;
        }

        entry.Resources.Add(resource);
        entry.Reasons.Add(reason);
    }


    public void AddGroupSet(IEnumerable<string> anyOf, string resource, string reason)
    {
        string[] sorted = anyOf.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(r => r, StringComparer.OrdinalIgnoreCase).ToArray();
        string key = string.Join('|', sorted);
        if (!_groups.TryGetValue(key, out var entry))
        {
            entry = (sorted, [], new SortedSet<string>(StringComparer.Ordinal), new SortedSet<string>(StringComparer.Ordinal));
            _groups[key] = entry;
        }

        entry.Resources.Add(resource);
        entry.Reasons.Add(reason);
    }

    /// <summary>Roles needed together: one role of every part, the parts in the overlay's order (a single part is a plain group).</summary>
    public void AddTogether(IEnumerable<IReadOnlyList<string>> parts, string resource, string reason)
    {
        string[][] sorted = parts
            .Select(p => p.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(r => r, StringComparer.OrdinalIgnoreCase).ToArray())
            .Where(p => p.Length > 0)
            .DistinctBy(p => string.Join('|', p), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (sorted.Length == 0)
        {
            return;
        }

        if (sorted.Length == 1)
        {
            AddGroupSet(sorted[0], resource, reason);
            return;
        }

        string key = TogetherKey(sorted);
        if (!_groups.TryGetValue(key, out var entry))
        {
            entry = ([], sorted, new SortedSet<string>(StringComparer.Ordinal), new SortedSet<string>(StringComparer.Ordinal));
            _groups[key] = entry;
        }

        entry.Resources.Add(resource);
        entry.Reasons.Add(reason);
    }

    private static string TogetherKey(string[][] parts)
    {
        return string.Join(" + ", parts.Select(p => string.Join('|', p)));
    }

    public IEnumerable<PermissionEntry> Build(PermissionKind kind)
    {
        // Application.ReadWrite.All implies Application.ReadWrite.OwnedBy: when both are needed only the broader grant is listed
        // (the resources that would manage with OwnedBy alone stay visible under it).
        if (_items.TryGetValue(PermissionManifest.OwnedBy, out var owned) && _items.TryGetValue("Application.ReadWrite.All", out var all))
        {
            all.Resources.UnionWith(owned.Resources);
            all.Reasons.Add("Application.ReadWrite.OwnedBy would suffice for the applications this principal owns (" + owned.Resources.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " resource(s))");
            _items.Remove(PermissionManifest.OwnedBy);
        }

        foreach (var pair in _items)
        {
            // A pure lookup requirement (reason starts with "lookup" / "preflight") that a write permission of the same
            // family already covers adds nothing: Application.ReadWrite.All implies Application.Read.All.
            if (pair.Key.Contains(".Read.", StringComparison.Ordinal) && pair.Value.Reasons.All(r => r.StartsWith("lookup", StringComparison.Ordinal) || r.StartsWith("preflight", StringComparison.Ordinal))
                && _items.ContainsKey(pair.Key.Replace(".Read.", ".ReadWrite.", StringComparison.Ordinal)))
            {
                continue;
            }

            yield return new PermissionEntry(pair.Key, kind, [.. pair.Value.Resources], [.. pair.Value.Reasons]);
        }
    }

    public IReadOnlyList<RoleRequirement> BuildGroups()
    {
        return _groups
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new RoleRequirement(g.Value.AnyOf, [.. g.Value.Resources], [.. g.Value.Reasons]) { AllOf = g.Value.AllOf })
            .ToList();
    }
}
