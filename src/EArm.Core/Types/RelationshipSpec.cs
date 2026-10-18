namespace EArm.Core.Types;

/// <summary>The edge kinds of the relationship catalog.</summary>
public enum RelationshipKind
{
    GroupMembership,
    Ownership,
    AdministrativeUnitMembership,
    Manager,
    AppRoleAssignment,
    DirectoryRoleAssignment,

    /// <summary><c>groupLifecyclePolicies</c> on Microsoft 365 groups: the group is added to a lifecycle (expiration) policy whose <c>managedGroupTypes</c> is <c>Selected</c>.</summary>
    GroupLifecyclePolicy,

    /// <summary><c>tokenLifetimePolicies</c> on applications and service principals: <c>POST …/tokenLifetimePolicies/$ref</c>.</summary>
    TokenLifetimePolicy,

    /// <summary><c>claimsMappingPolicies</c> on service principals: <c>POST …/claimsMappingPolicies/$ref</c>.</summary>
    ClaimsMappingPolicy,

    /// <summary><c>appManagementPolicies</c> on applications and service principals: <c>POST …/appManagementPolicies/$ref</c>.</summary>
    AppManagementPolicy,

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
