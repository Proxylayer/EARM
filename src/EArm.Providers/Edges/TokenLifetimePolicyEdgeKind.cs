using EArm.Engine.Edges;

namespace EArm.Providers.Edges;

/// <summary>
/// <c>tokenLifetimePolicies</c> on applications and service principals (ensure-only). Graph v1.0: application-post-tokenlifetimepolicies and
/// serviceprincipal-post-tokenlifetimepolicies (<c>POST …/tokenLifetimePolicies/$ref</c>, 204), the list and delete pages alongside; "can assign only one
/// tokenLifetimePolicy per application" (https://learn.microsoft.com/en-us/graph/api/application-post-tokenlifetimepolicies?view=graph-rest-1.0).
/// </summary>
[EntraEdgeKind(EdgeKind.TokenLifetimePolicy)]
public sealed class TokenLifetimePolicyEdgeKind : PolicyAssignmentEdgeKind
{
    public TokenLifetimePolicyEdgeKind()
        : base(EdgeKind.TokenLifetimePolicy, "tokenLifetimePolicies", "policies/tokenLifetimePolicies", "token lifetime policy", ["applications", "servicePrincipals"], onePerPrincipal: true)
    {
    }
}
