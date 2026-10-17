using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GraphApi = EArm.Graph.GraphApi;

namespace EArm.Engine.Edges;

/// <summary>The live state reads of one edge pass (<see cref="EdgePlanner"/>): each target and principal is read once per edge kind, then served from the cache.</summary>
internal sealed class EdgeLiveStates
{
    private readonly EdgeGraphContext _context;
    private readonly Dictionary<string, EdgeTargetState> _targets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, EdgePrincipalState> _principals = new(StringComparer.OrdinalIgnoreCase);

    public EdgeLiveStates(EdgeGraphContext context)
    {
        _context = context;
    }

    public async Task<EdgeTargetState> ReadTargetAsync(IEdgeKindHandler handler, string targetId, string? collectionHint, bool includeEdges, GraphApi api, CancellationToken cancellationToken)
    {
        string key = handler.Kind + "|" + Edge.NormalizeId(targetId);
        if (_targets.TryGetValue(key, out var cached) && (!includeEdges || cached.Edges is not null || !cached.Exists))
        {
            return cached;
        }

        var state = await handler.ReadTargetAsync(targetId, collectionHint, includeEdges, api, _context, cancellationToken).ConfigureAwait(false);
        _targets[key] = state;
        return state;
    }

    public async Task<EdgePrincipalState> ReadPrincipalAsync(IEdgeKindHandler handler, string principalId, string principalType, GraphApi api, CancellationToken cancellationToken)
    {
        string key = handler.Kind + "|" + Edge.NormalizeId(principalId);
        if (_principals.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var state = await handler.ReadPrincipalAsync(principalId, principalType, api, _context, cancellationToken).ConfigureAwait(false);
        _principals[key] = state;
        return state;
    }
}
