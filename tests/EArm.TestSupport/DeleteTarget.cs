using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using EArm.Core.Types;
using EArm.Engine.Providers;
using EArm.Engine.State;

namespace EArm.TestSupport;

/// <summary>
/// The arguments of <see cref="IResourceProvider.DeleteAsync"/> (the live key and the live object) derived from a state entry a test
/// already builds: the key carries the entry's values, api, parent Graph id and (for a child) the parent's <c>uniqueName</c> from the
/// resource id; the live object carries the entry's Graph id and its recorded extra values.
/// </summary>
public static class DeleteTarget
{
    public static ResourceKey KeyOf(StateEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var key = entry.ToResourceKey(ResourceTypeCatalog.Default);
        if (entry.Id.Parent is { } parent)
        {
            key = key with
            {
                ParentKey = new ResourceKey
                {
                    Type = parent.Type,
                    Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["uniqueName"] = parent.NameLeaf },
                },
            };
        }

        return key;
    }

    public static ExistingResource LiveOf(StateEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var raw = entry.Extra?.DeepClone() as JsonObject ?? new JsonObject();
        raw["id"] = entry.GraphId;
        return new ExistingResource(entry.GraphId, (JsonObject)raw.DeepClone(), raw);
    }
}
