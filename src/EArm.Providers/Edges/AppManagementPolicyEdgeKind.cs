using EArm.Engine.Edges;

namespace EArm.Providers.Edges;

/// <summary>
/// <c>appManagementPolicies</c> on applications and service principals (ensure-only). Graph v1.0: appmanagementpolicy-post-appliesto
/// (<c>POST /applications/{id}/appManagementPolicies/$ref</c>, and the service principal form in its second example, 204) and
/// appmanagementpolicy-delete-appliesto; "Only one policy object can be assigned to an application or service principal"
/// (https://learn.microsoft.com/en-us/graph/api/appmanagementpolicy-post-appliesto?view=graph-rest-1.0).
/// </summary>
[EntraEdgeKind(EdgeKind.AppManagementPolicy)]
public sealed class AppManagementPolicyEdgeKind : PolicyAssignmentEdgeKind
{
    public AppManagementPolicyEdgeKind()
        : base(EdgeKind.AppManagementPolicy, "appManagementPolicies", "policies/appManagementPolicies", "app management policy", ["applications", "servicePrincipals"], onePerPrincipal: true)
    {
    }
}
