using System.Collections.Generic;

namespace EArm.Engine.Permissions;

/// <summary>
/// A group of Entra roles of which the signed-in user needs at least one, and the resources that need it. A requirement of roles needed
/// <b>together</b> has an empty <see cref="AnyOf"/> and its parts in <see cref="AllOf"/>: the user needs one role of every part at the same time
/// (adding a role-assignable group to a catalog needs Identity Governance Administrator AND Privileged Role Administrator).
/// </summary>
public sealed record RoleRequirement(IReadOnlyList<string> AnyOf, IReadOnlyList<string> Resources, IReadOnlyList<string> Reasons)
{
    /// <summary>The parts of a requirement of roles needed together (each part a set of alternatives); empty for a plain group of alternatives.</summary>
    public IReadOnlyList<IReadOnlyList<string>> AllOf { get; init; } = [];

    /// <summary>True when the roles of <see cref="AllOf"/> are needed together.</summary>
    public bool IsTogether => AllOf.Count > 0;
}
