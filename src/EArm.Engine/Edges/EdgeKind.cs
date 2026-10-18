using EArm.Core.Types;

namespace EArm.Engine.Edges;

/// <summary>The edge kinds of the relationship catalog, as the edge pass applies them.</summary>
public enum EdgeKind
{
    /// <summary><c>groups.members</c> / <c>memberOf</c>: <c>POST /groups/{g}/members/$ref</c>.</summary>
    GroupMembership,

    /// <summary><c>owners</c> on applications, service principals and groups / <c>ownerOf</c>: <c>POST /{collection}/{t}/owners/$ref</c>.</summary>
    Ownership,

    /// <summary><c>administrativeUnits.members</c> / <c>administrativeUnits</c>: <c>POST /directory/administrativeUnits/{au}/members/$ref</c>.</summary>
    AdministrativeUnitMembership,

    /// <summary><c>manager</c> on users (single-valued): <c>PUT</c> / <c>DELETE /users/{u}/manager/$ref</c>.</summary>
    Manager,

    /// <summary><c>appRoleAssignments</c> on principals and the standalone <c>Microsoft.Entra/appRoleAssignments</c> type.</summary>
    AppRoleAssignment,

    /// <summary><c>directoryRoles</c> on principals and the standalone <c>Microsoft.Entra/roleAssignments</c> type.</summary>
    DirectoryRole,

    /// <summary><c>groupLifecyclePolicies</c> on groups: <c>POST /groupLifecyclePolicies/{id}/addGroup</c> (ensure-only).</summary>
    GroupLifecyclePolicy,

    /// <summary><c>tokenLifetimePolicies</c> on applications and service principals (ensure-only).</summary>
    TokenLifetimePolicy,

    /// <summary><c>claimsMappingPolicies</c> on service principals (ensure-only).</summary>
    ClaimsMappingPolicy,

    /// <summary><c>appManagementPolicies</c> on applications and service principals (ensure-only).</summary>
    AppManagementPolicy,
}

/// <summary>
/// The two parts of the edge pass: part A (owners/ownerOf, members/memberOf, AU members,
/// manager) runs first; part B (directory roles, app role assignments) plugs in behind it.
/// </summary>
public enum EdgePhase
{
    A = 1,
    B = 2,
}

/// <summary>Which side of an edge a relationship property declares (mirrors <see cref="RelationshipSide"/>).</summary>
public enum EdgeSide
{
    Target,
    Principal,
}

public static class EdgeKinds
{
    /// <summary>Maps a catalog relationship kind to an edge kind; <c>false</c> for action-backed properties (licenses).</summary>
    public static bool TryFromRelationship(RelationshipKind kind, out EdgeKind edgeKind)
    {
        switch (kind)
        {
            case RelationshipKind.GroupMembership:
                edgeKind = EdgeKind.GroupMembership;
                return true;
            case RelationshipKind.Ownership:
                edgeKind = EdgeKind.Ownership;
                return true;
            case RelationshipKind.AdministrativeUnitMembership:
                edgeKind = EdgeKind.AdministrativeUnitMembership;
                return true;
            case RelationshipKind.Manager:
                edgeKind = EdgeKind.Manager;
                return true;
            case RelationshipKind.AppRoleAssignment:
                edgeKind = EdgeKind.AppRoleAssignment;
                return true;
            case RelationshipKind.DirectoryRoleAssignment:
                edgeKind = EdgeKind.DirectoryRole;
                return true;
            case RelationshipKind.GroupLifecyclePolicy:
                edgeKind = EdgeKind.GroupLifecyclePolicy;
                return true;
            case RelationshipKind.TokenLifetimePolicy:
                edgeKind = EdgeKind.TokenLifetimePolicy;
                return true;
            case RelationshipKind.ClaimsMappingPolicy:
                edgeKind = EdgeKind.ClaimsMappingPolicy;
                return true;
            case RelationshipKind.AppManagementPolicy:
                edgeKind = EdgeKind.AppManagementPolicy;
                return true;
            default:
                edgeKind = default;
                return false;
        }
    }

    public static EdgeSide ToEdgeSide(RelationshipSide side)
    {
        return side == RelationshipSide.Target ? EdgeSide.Target : EdgeSide.Principal;
    }
}
