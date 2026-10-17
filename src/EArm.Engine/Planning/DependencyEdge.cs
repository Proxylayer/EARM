using EArm.Engine.Providers;

namespace EArm.Engine.Planning;

/// <summary><paramref name="Dependent"/> must be applied after <paramref name="Dependency"/>.</summary>
public sealed record DependencyEdge(ResourceInstance Dependent, ResourceInstance Dependency, DependencyKind Kind, string? Detail = null);
