using System.Collections.Generic;
using System.Linq;
using EArm.Engine.Providers;

namespace EArm.Engine.Planning;

/// <summary>Instances that may be applied together once every earlier batch is done; ordered by phase then declaration order.</summary>
public sealed record DeploymentBatch(int Index, IReadOnlyList<ResourceInstance> Instances)
{
    public override string ToString()
    {
        return $"batch {Index}: {string.Join(", ", Instances.Select(i => i.Id.Id))}";
    }
}
