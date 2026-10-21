namespace EArm.Graph.Throttling;

/// <summary>
/// The Microsoft Graph service a request counts against for throttling. Microsoft throttles per service, so a 429 from one area pauses only
/// that area. Only <see cref="Directory"/> and <see cref="ConditionalAccess"/> have published rates
/// (https://learn.microsoft.com/en-us/graph/throttling-limits); the other areas rely on <c>Retry-After</c> alone.
/// </summary>
internal enum GraphServiceArea
{
    /// <summary>Not a throttled request of its own: the <c>$batch</c> envelope (its sub-requests are charged instead).</summary>
    None = 0,

    /// <summary>"Identity and access service limits": users, groups, applications, service principals, directory objects, ...</summary>
    Directory,

    /// <summary>Conditional Access policies and named locations: one request per second per tenant.</summary>
    ConditionalAccess,

    /// <summary><c>identityGovernance/*</c> (entitlement management, access reviews, PIM for Groups): no published rate.</summary>
    IdentityGovernance,

    /// <summary><c>roleManagement/*</c>: no published rate.</summary>
    RoleManagement,

    /// <summary>The <c>policies/*</c> that are not directory policies (role management, authentication methods and strength policies): no published rate.</summary>
    Policies,

    /// <summary>Everything else (authentication contexts, group lifecycle policies, ...): no published rate.</summary>
    Other,
}

/// <summary>What one Graph request (or one <c>$batch</c> sub-request) costs: its area, its estimated directory resource units and whether it is a write.</summary>
/// <param name="Area">The throttling area.</param>
/// <param name="ResourceUnits">Estimated directory resource units (only meaningful for <see cref="GraphServiceArea.Directory"/>).</param>
/// <param name="IsWrite">POST, PATCH, PUT or DELETE: counts against the directory write quota.</param>
internal readonly record struct GraphRequestCost(GraphServiceArea Area, int ResourceUnits, bool IsWrite);
