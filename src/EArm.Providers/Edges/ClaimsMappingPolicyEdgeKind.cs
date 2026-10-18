using EArm.Engine.Edges;

namespace EArm.Providers.Edges;

/// <summary>
/// <c>claimsMappingPolicies</c> on service principals (ensure-only). Graph v1.0: serviceprincipal-post-claimsmappingpolicies (<c>POST …/claimsMappingPolicies/$ref</c>,
/// 204), the list and delete pages alongside (https://learn.microsoft.com/en-us/graph/api/serviceprincipal-post-claimsmappingpolicies?view=graph-rest-1.0).
/// Learn states no one-per-principal limit for claims mapping, so none is checked here.
/// </summary>
[EntraEdgeKind(EdgeKind.ClaimsMappingPolicy)]
public sealed class ClaimsMappingPolicyEdgeKind : PolicyAssignmentEdgeKind
{
    public ClaimsMappingPolicyEdgeKind()
        : base(EdgeKind.ClaimsMappingPolicy, "claimsMappingPolicies", "policies/claimsMappingPolicies", "claims mapping policy", ["servicePrincipals"], onePerPrincipal: false)
    {
    }
}
