using System.Collections.Generic;
using EArm.Core.Json;

namespace EArm.Engine.Providers;

/// <summary>One template-level conflict a provider found between a declared instance and other declared instances.</summary>
/// <param name="Code">The diagnostic code (an error).</param>
/// <param name="Message">What conflicts and what to do about it.</param>
/// <param name="Path">Where the conflict is reported in the template (the declaring resource or the conflicting property).</param>
public sealed record DeclarationConflict(string Code, string Message, JsonPath Path);

/// <summary>
/// Optional provider capability: conflicts of one declared instance with the other declared instances of the template, which no single row can see (a PIM
/// active assignment and the <c>roleAssignments</c> resource or the pruning <c>members</c> list that would fight it). The planner asks every instance of
/// such a provider once the dependency graph is built (the instances are expanded and their same-template references resolved), before any Graph call:
/// a conflict is a validation error in the what-if and in a deployment alike. Only the immutable parts of the instances may be read (type, ids,
/// declaration, reference occurrences); properties are not evaluated yet.
/// </summary>
public interface IDeclarationConflictSource
{
    /// <summary>The conflicts of <paramref name="instance"/> with the other instances of <paramref name="declared"/> (every expanded instance, itself included).</summary>
    IEnumerable<DeclarationConflict> DescribeConflicts(ResourceInstance instance, IReadOnlyList<ResourceInstance> declared);
}
