using EArm.Core.Types;

namespace EArm.Providers;

/// <summary>
/// <c>Microsoft.Entra/policies/claimsMappingPolicies</c>: claims mapping policies (https://learn.microsoft.com/en-us/graph/api/resources/claimsmappingpolicy?view=graph-rest-1.0).
/// The document is <c>{"ClaimsMappingPolicy":{"Version":1,"IncludeBasicClaimSet":"true","ClaimsSchema":[…],"ClaimsTransformation":[…]}}</c> (at most 50
/// schema entries and 50 transformations; Graph silently ignores the rest). Learn says to ignore <c>isOrganizationDefault</c>: claims mapping applies to
/// service principals only, so a declared <c>true</c> is an error. Assigned with the <c>claimsMappingPolicies</c> edge of service principals (custom claims in
/// access tokens also need the resource application's <c>api.acceptMappedClaims</c>). See <see cref="StsPolicyProviderBase"/> for the requests.
/// </summary>
[EntraProvider(ResourceType)]
public sealed class ClaimsMappingPoliciesProvider : StsPolicyProviderBase
{
    public const string ResourceType = "Microsoft.Entra/policies/claimsMappingPolicies";

    public ClaimsMappingPoliciesProvider(ResourceTypeDefinition definition, ResourceTypeCatalog? catalog = null)
        : base(definition, catalog, ResourceType)
    {
    }

    public ClaimsMappingPoliciesProvider(ResourceTypeCatalog catalog)
        : this(RequireDefinition(catalog, ResourceType), catalog)
    {
    }

    protected override string Noun => "claims mapping policy";

    protected override string RootElement => "ClaimsMappingPolicy";

    protected override bool AllowsOrganizationDefault => false;

    protected override string AssignmentProperty => "claimsMappingPolicies";
}
