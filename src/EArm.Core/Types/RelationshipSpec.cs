namespace EArm.Core.Types;

/// <summary>The edge kinds of the relationship catalog (plan section 1.4).</summary>
public enum RelationshipKind
{
    GroupMembership,
    Ownership,
    AdministrativeUnitMembership,
    Manager,
    AppRoleAssignment,
    DirectoryRoleAssignment,

    /// <summary>Action-backed (<c>assignLicense</c>), not a tracked edge; listed so providers treat the property as non-PATCHable.</summary>
    License,
}

/// <summary>Which side of the edge a property declares.</summary>
public enum RelationshipSide
{
    /// <summary>Declared on the target (group <c>members</c>/<c>owners</c>, AU <c>members</c>): the array is the full desired set.</summary>
    Target,

    /// <summary>Declared on the principal (<c>memberOf</c>, <c>ownerOf</c>, <c>manager</c>, ...): ensure the edges exist; remove only edges previously created by this deployment.</summary>
    Principal,
}

/// <summary>A relationship/assignment property inside <c>properties</c> that the edge pass applies instead of PATCHing.</summary>
public sealed record RelationshipSpec(string Property, RelationshipKind Kind, RelationshipSide Side)
{
    public bool IsAuthoritativeSet => Side == RelationshipSide.Target;
}
