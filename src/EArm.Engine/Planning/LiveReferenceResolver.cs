using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core;
using EArm.Engine.Providers;
using EArm.Expressions;
using EArm.Expressions.Evaluation;
using EArm.Expressions.Values;

namespace EArm.Engine.Planning;

/// <summary>An existing-resource lookup that could not be performed; reported in the plan and rendered as a what-if diagnostic.</summary>
public sealed record UnresolvedReference(ResourceIdentifier Id, string? ApiVersion, string Reason);

/// <summary>The outcome of an existing-resource lookup (<c>reference(id, apiVersion)</c>).</summary>
/// <param name="Found">The live object, or <c>null</c> when it does not exist.</param>
/// <param name="Unresolved">Set when the lookup could not be performed at all (no provider, unresolvable key); the reference stays a placeholder.</param>
public sealed record ExistingLookupResult(ExistingResource? Found, string? Unresolved)
{
    public static ExistingLookupResult NotFound { get; } = new(null, null);

    public static ExistingLookupResult Of(ExistingResource? found)
    {
        return new ExistingLookupResult(found, null);
    }

    public static ExistingLookupResult Unresolvable(string reason)
    {
        return new ExistingLookupResult(null, reason);
    }
}

/// <summary>
/// The engine's <see cref="IReferenceResolver"/>: same-template resources resolve to the live object once the engine
/// has published it (read during what-if, or created/updated during execution) and to a placeholder before that;
/// existing-resource lookups (<c>reference(id, apiVersion)</c>) go through <see cref="ExistingLookup"/> (set by the
/// planner once providers may be used) and are cached. A symbolic resource is published under both its planned
/// (symbolic) id and its resolved id, so dependents that refer to it by symbol see the same object.
/// <para>
/// <see cref="IReferenceResolver.Resolve"/> is synchronous while Graph reads are asynchronous; the lookup is awaited
/// on the calling thread-pool thread. The console host has no synchronization context, so this cannot deadlock; it
/// occupies a pool thread per concurrent lookup, bounded by the planner's parallelism.
/// </para>
/// </summary>
public sealed class LiveReferenceResolver : IReferenceResolver
{
    private readonly object _gate = new();
    private readonly Dictionary<ResourceIdentifier, ReferenceResult.Live> _published = [];
    private readonly Dictionary<string, ReferenceResult> _existing = new(StringComparer.Ordinal);
    private readonly List<UnresolvedReference> _unresolved = [];
    private readonly HashSet<string> _unresolvedKeys = new(StringComparer.Ordinal);

    /// <summary>Performs an existing-resource lookup; <c>null</c> (the default) keeps every such reference a placeholder.</summary>
    public Func<ResourceIdentifier, string, CancellationToken, Task<ExistingLookupResult>>? ExistingLookup { get; set; }

    public CancellationToken CancellationToken { get; set; }

    /// <summary>Lookups that could not be performed, in first-seen order (one entry per id/apiVersion pair).</summary>
    public IReadOnlyList<UnresolvedReference> Unresolved
    {
        get
        {
            lock (_gate)
            {
                return _unresolved.ToArray();
            }
        }
    }

    /// <summary>Publishes a live object for <paramref name="id"/> and every alias (the planned symbolic id of a resolved name).</summary>
    public void Publish(ResourceIdentifier id, JsonObject live, string graphId, params ResourceIdentifier[] aliases)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentException.ThrowIfNullOrWhiteSpace(graphId);
        var result = new ReferenceResult.Live(JsonValues.Clone(live)!, graphId);
        lock (_gate)
        {
            _published[id] = result;
            foreach (var alias in aliases)
            {
                _published[alias] = result;
            }
        }
    }

    public bool IsPublished(ResourceIdentifier id)
    {
        ArgumentNullException.ThrowIfNull(id);
        lock (_gate)
        {
            return _published.ContainsKey(id);
        }
    }

    public bool TryGetGraphId(ResourceIdentifier id, out string graphId)
    {
        ArgumentNullException.ThrowIfNull(id);
        lock (_gate)
        {
            if (_published.TryGetValue(id, out var live))
            {
                graphId = live.GraphId;
                return true;
            }
        }

        graphId = string.Empty;
        return false;
    }

    /// <summary>A copy of the published object for <paramref name="id"/>, when any.</summary>
    public bool TryGetLive(ResourceIdentifier id, out JsonObject live, out string graphId)
    {
        ArgumentNullException.ThrowIfNull(id);
        lock (_gate)
        {
            if (_published.TryGetValue(id, out var found))
            {
                live = (JsonObject)JsonValues.Clone(found.Object)!;
                graphId = found.GraphId;
                return true;
            }
        }

        live = null!;
        graphId = string.Empty;
        return false;
    }

    public ReferenceResult Resolve(ResourceIdentifier id, string? apiVersion)
    {
        ArgumentNullException.ThrowIfNull(id);
        lock (_gate)
        {
            if (_published.TryGetValue(id, out var live))
            {
                return live;
            }
        }

        if (apiVersion is null)
        {
            return new ReferenceResult.Placeholder(id);
        }

        string cacheKey = id.Id + "|" + apiVersion;
        lock (_gate)
        {
            if (_existing.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }
        }

        var lookup = ExistingLookup;
        if (lookup is null)
        {
            return new ReferenceResult.Placeholder(id);
        }

        ExistingLookupResult outcome;
        try
        {
            outcome = lookup(id, apiVersion, CancellationToken).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not ExpressionException)
        {
            outcome = ExistingLookupResult.Unresolvable(ex.Message);
        }

        ReferenceResult result;
        if (outcome.Unresolved is not null)
        {
            result = new ReferenceResult.Placeholder(id);
            lock (_gate)
            {
                if (_unresolvedKeys.Add(cacheKey))
                {
                    _unresolved.Add(new UnresolvedReference(id, apiVersion, outcome.Unresolved));
                }
            }
        }
        else if (outcome.Found is null)
        {
            result = new ReferenceResult.NotFound(id, apiVersion);
        }
        else
        {
            result = new ReferenceResult.Live(JsonValues.Clone(outcome.Found.Raw)!, outcome.Found.GraphId);
        }

        lock (_gate)
        {
            _existing[cacheKey] = result;
        }

        return result;
    }
}
