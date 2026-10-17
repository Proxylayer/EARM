using System.Collections.Concurrent;
using System.Collections.Generic;
using EArm.Core.Diagnostics;
using EArm.Engine.Providers;

namespace EArm.Engine.Execution;

/// <summary>
/// The diagnostics of one deployment run. Run-level findings are kept in the order they are raised; findings about one resource, raised while resources
/// are applied in parallel, are kept per instance and reported in plan order (<see cref="Flush"/>), so the result does not depend on timing.
/// </summary>
internal sealed class RunDiagnostics
{
    private readonly List<Diagnostic> _diagnostics = [];
    private readonly object _diagnosticsGate = new();
    private readonly ConcurrentDictionary<ResourceInstance, List<Diagnostic>> _instanceDiagnostics = new();

    public void Add(Diagnostic diagnostic)
    {
        lock (_diagnosticsGate)
        {
            _diagnostics.Add(diagnostic);
        }
    }

    /// <summary>A finding about one resource, raised while resources are applied in parallel: kept per instance and reported in plan order (<see cref="Flush"/>), so the result does not depend on timing.</summary>
    public void AddForInstance(ResourceInstance instance, Diagnostic diagnostic)
    {
        var list = _instanceDiagnostics.GetOrAdd(instance, static _ => []);
        lock (list)
        {
            list.Add(diagnostic);
        }
    }

    public void Flush(IEnumerable<ResourceInstance> instances)
    {
        foreach (var instance in instances)
        {
            if (_instanceDiagnostics.TryRemove(instance, out var found))
            {
                lock (_diagnosticsGate)
                {
                    _diagnostics.AddRange(found);
                }
            }
        }
    }

    public List<Diagnostic> Snapshot()
    {
        lock (_diagnosticsGate)
        {
            return [.. _diagnostics];
        }
    }
}
