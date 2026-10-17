using EArm.Core.Types;
using EArm.Engine.Providers;

namespace EArm.Engine.Planning;

/// <summary>Why one instance waits for another.</summary>
public enum DependencyKind
{
    /// <summary>An entry of <c>dependsOn</c>.</summary>
    Explicit,

    /// <summary>The parent of a child resource (nested or <c>parent/child</c> form).</summary>
    Parent,

    /// <summary>A same-template <c>reference()</c> / <c>resourceId()</c> outside the edge properties (ARM implicit dependency).</summary>
    Reference,

    /// <summary>The resource's own name is computed from the target (<c>[reference('app').appId]</c>).</summary>
    SymbolicName,

    /// <summary><c>dependsOn</c> naming a copy loop: every iteration of the loop.</summary>
    CopyLoop,

    /// <summary>A <c>mode: serial</c> loop: an iteration waits for the previous chunk.</summary>
    CopySerial,

    /// <summary>A provider-declared apply order (<see cref="IApplyOrderSource"/>: a PIM policy before the schedule requests it governs).</summary>
    Ordering,

    /// <summary>
    /// A nested deployment (<see cref="DeploymentResourceType"/>) waits for every resource of its template, and those resources wait for what the
    /// deployment depends on: depending on a deployment means depending on everything inside it.
    /// </summary>
    NestedDeployment,
}
