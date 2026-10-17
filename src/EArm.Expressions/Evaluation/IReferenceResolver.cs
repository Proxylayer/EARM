using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using EArm.Core;
using EArm.Expressions.Values;

namespace EArm.Expressions.Evaluation;

/// <summary>
/// Supplies the object behind <c>reference()</c>. <paramref name="apiVersion"/> is <c>null</c> for a same-template
/// resource (implicit dependency; the engine publishes the live object after the resource is applied) and set for
/// an existing-resource lookup outside the template. Implemented live by the engine;
/// <see cref="StaticReferenceResolver"/> is the offline implementation.
/// </summary>
public interface IReferenceResolver
{
    ReferenceResult Resolve(ResourceIdentifier id, string? apiVersion);

    /// <summary>
    /// True when the template resource <paramref name="id"/> is not deployed because its <c>condition</c> evaluated to false; <c>references()</c>
    /// leaves such an iteration out, as ARM does. Unknown (false) before the engine has decided the condition.
    /// </summary>
    bool IsConditionFalse(ResourceIdentifier id) => false;
}

/// <summary>The outcome of a <see cref="IReferenceResolver.Resolve"/> call.</summary>
public abstract record ReferenceResult
{
    private ReferenceResult()
    {
    }

    /// <summary>The live Graph object (never mutated by the evaluator: it is cloned on every use).</summary>
    public sealed record Live(JsonNode Object, string GraphId) : ReferenceResult;

    /// <summary>The resource does not exist yet (what-if, validation): every property evaluates to an unknown value.</summary>
    public sealed record Placeholder(ResourceIdentifier Id) : ReferenceResult;

    /// <summary>An existing-resource lookup found nothing: <c>reference()</c> fails with <c>ReferenceTargetNotFound</c>.</summary>
    public sealed record NotFound(ResourceIdentifier Id, string ApiVersion) : ReferenceResult;
}

/// <summary>Offline resolver: every reference is a <see cref="ReferenceResult.Placeholder"/>.</summary>
public sealed class StaticReferenceResolver : IReferenceResolver
{
    public static StaticReferenceResolver Instance { get; } = new();

    public ReferenceResult Resolve(ResourceIdentifier id, string? apiVersion)
    {
        ArgumentNullException.ThrowIfNull(id);
        return new ReferenceResult.Placeholder(id);
    }
}

/// <summary>
/// A resolver backed by a dictionary of known objects, for tests and for the engine to publish applied resources
/// into. Unknown same-template ids yield a placeholder; unknown existing lookups yield <see cref="ReferenceResult.NotFound"/>.
/// </summary>
public sealed class InMemoryReferenceResolver : IReferenceResolver
{
    private readonly Dictionary<ResourceIdentifier, ReferenceResult.Live> _objects = [];
    private readonly object _gate = new();

    public void Publish(ResourceIdentifier id, JsonNode liveObject, string graphId)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(liveObject);
        ArgumentNullException.ThrowIfNull(graphId);
        lock (_gate)
        {
            _objects[id] = new ReferenceResult.Live(JsonValues.Clone(liveObject)!, graphId);
        }
    }

    public bool Remove(ResourceIdentifier id)
    {
        ArgumentNullException.ThrowIfNull(id);
        lock (_gate)
        {
            return _objects.Remove(id);
        }
    }

    public ReferenceResult Resolve(ResourceIdentifier id, string? apiVersion)
    {
        ArgumentNullException.ThrowIfNull(id);
        lock (_gate)
        {
            if (_objects.TryGetValue(id, out var live))
            {
                return live;
            }
        }

        return apiVersion is null ? new ReferenceResult.Placeholder(id) : new ReferenceResult.NotFound(id, apiVersion);
    }
}
