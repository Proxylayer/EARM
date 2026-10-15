using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Az;
using EArm.Core.Types;
using EArm.Engine.State;

namespace EArm.Engine.Edges;

/// <summary>
/// Removes the edges a state entry records (<c>relationships</c>) before the object itself is deleted in Complete
/// mode (plan section 1.4: "<c>--mode Complete</c> removes all state-recorded edges of a resource before deleting
/// it"). A 404 means the edge is already gone; any other failure is logged and does not stop the delete, since
/// Graph drops the edges of a deleted object anyway.
/// </summary>
public static class RecordedEdges
{
    /// <summary>Removes every recorded edge of <paramref name="entry"/>; returns how many removals were sent.</summary>
    public static async Task<int> RemoveAsync(StateEntry entry, ResourceTypeCatalog catalog, EdgeKindRegistry kinds, EdgeGraphContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(kinds);
        ArgumentNullException.ThrowIfNull(context);

        if (entry.Relationships is not { Count: > 0 } relationships)
        {
            return 0;
        }

        var definition = catalog.Find(entry.Type);
        if (definition is null)
        {
            EngineLog.RecordedEdgeSkipped(context.Logger, entry.Id.Id, "the resource type is not in the catalog");
            return 0;
        }

        var api = entry.ToResourceKey(catalog).Api;
        int removed = 0;
        foreach (var (property, value) in relationships.ToList())
        {
            var spec = definition.Relationships.FirstOrDefault(s => s.Property.Equals(property, StringComparison.OrdinalIgnoreCase));
            if (spec is null || !EdgeKinds.TryFromRelationship(spec.Kind, out var kind) || !kinds.TryGet(kind, out var handler))
            {
                EngineLog.RecordedEdgeSkipped(context.Logger, entry.Id.Id, $"no edge handler applies to relationships.{property}");
                continue;
            }

            var records = value is JsonArray array ? array.ToList() : new List<JsonNode?> { value };
            foreach (var record in records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var edge = await handler.FromRecordAsync(record, entry.GraphId, api, context, cancellationToken).ConfigureAwait(false);
                if (edge is null)
                {
                    EngineLog.RecordedEdgeSkipped(context.Logger, entry.Id.Id, $"relationships.{property} entry '{record?.ToJsonString()}' could not be interpreted");
                    continue;
                }

                var outcome = await EdgeExecutor.SendAsync(handler.RemoveRequest(edge, null, api), api, RetryHint.Default, context, cancellationToken).ConfigureAwait(false);
                removed++;
                if (outcome.Succeeded || outcome.Exception is GraphException { IsNotFound: true })
                {
                    EngineLog.EdgeApplied(context.Logger, "remove " + handler.Describe(edge), outcome.Succeeded ? "done" : "already gone");
                }
                else
                {
                    EngineLog.EdgeApplyFailed(context.Logger, "remove " + handler.Describe(edge), outcome.Exception!.Message);
                }
            }
        }

        return removed;
    }
}
