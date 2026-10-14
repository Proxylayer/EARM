using System;
using EntraArm.Engine.Edges;

namespace EntraArm.Providers;

/// <summary>
/// Binds an <see cref="IEdgeKindHandler"/> class to the edge kind it implements, so <see cref="ProviderDiscovery"/>
/// can register it without a shared registration file: <c>[EntraEdgeKind(EdgeKind.AppRoleAssignment)]</c>. Part A
/// kinds are shipped by the engine (<see cref="EdgeKindRegistry.Default"/>) and cannot be claimed here.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class EntraEdgeKindAttribute : Attribute
{
    public EntraEdgeKindAttribute(EdgeKind kind)
    {
        Kind = kind;
    }

    /// <summary>The edge kind the class handles.</summary>
    public EdgeKind Kind { get; }
}
