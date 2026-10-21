using System;
using EArm.Graph.Throttling;
using Xunit;

namespace EArm.Graph.Tests;

/// <summary>
/// <see cref="GraphRequestClassifier"/>: the throttling area of every path family EARM sends, and the directory resource-unit estimate from Microsoft's
/// published cost table (base cost, <c>$select</c> −1, <c>$expand</c> +1, <c>$top</c> below 20 −1, never below 1; a write costs 1 unit plus one write).
/// </summary>
public sealed class GraphRequestClassifierTests
{
    private static GraphServiceArea Area(string name)
    {
        return Enum.Parse<GraphServiceArea>(name);
    }

    // ---- areas --------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("/users", "Directory")]
    [InlineData("/users/u1", "Directory")]
    [InlineData("/groups", "Directory")]
    [InlineData("/groups/g1/members", "Directory")]
    [InlineData("/groups/g1/owners", "Directory")]
    [InlineData("/applications", "Directory")]
    [InlineData("/applications/a1/federatedIdentityCredentials", "Directory")]
    [InlineData("/applications/a1/extensionProperties", "Directory")]
    [InlineData("/servicePrincipals", "Directory")]
    [InlineData("/servicePrincipals/s1/appRoleAssignments", "Directory")]
    [InlineData("/servicePrincipals/s1/appRoleAssignedTo", "Directory")]
    [InlineData("/oauth2PermissionGrants", "Directory")]
    [InlineData("/directory/deletedItems/x1", "Directory")]
    [InlineData("/directory/deletedItems/microsoft.graph.group", "Directory")]
    [InlineData("/directoryObjects/getByIds", "Directory")]
    [InlineData("/administrativeUnits", "Directory")]
    [InlineData("/administrativeUnits/au1/members", "Directory")]
    [InlineData("/subscribedSkus", "Directory")]
    [InlineData("/organization", "Directory")]
    [InlineData("/policies/tokenLifetimePolicies", "Directory")]
    [InlineData("/policies/claimsMappingPolicies/c1", "Directory")]
    [InlineData("/policies/appManagementPolicies", "Directory")]
    [InlineData("/policies/defaultAppManagementPolicy", "Directory")]
    [InlineData("/policies/crossTenantAccessPolicy/partners", "Directory")]
    [InlineData("/identity/conditionalAccess/policies", "ConditionalAccess")]
    [InlineData("/identity/conditionalAccess/policies/p1", "ConditionalAccess")]
    [InlineData("/identity/conditionalAccess/namedLocations", "ConditionalAccess")]
    [InlineData("/identity/conditionalAccess/namedLocations/n1", "ConditionalAccess")]
    [InlineData("/identity/conditionalAccess/authenticationContextClassReferences", "Other")]
    [InlineData("/identity/conditionalAccess/authenticationContextClassReferences/c1", "Other")]
    [InlineData("/identity/conditionalAccess", "Other")]
    [InlineData("/groupLifecyclePolicies", "Other")]
    [InlineData("/groupLifecyclePolicies/l1/addGroup", "Other")]
    [InlineData("/identityGovernance/entitlementManagement/accessPackages", "IdentityGovernance")]
    [InlineData("/identityGovernance/accessReviews/definitions", "IdentityGovernance")]
    [InlineData("/identityGovernance/privilegedAccess/group/eligibilityScheduleRequests", "IdentityGovernance")]
    [InlineData("/roleManagement/directory/roleAssignments", "RoleManagement")]
    [InlineData("/roleManagement/directory/roleEligibilityScheduleRequests", "RoleManagement")]
    [InlineData("/policies/roleManagementPolicies", "Policies")]
    [InlineData("/policies/authenticationMethodsPolicy", "Policies")]
    [InlineData("/policies/authenticationStrengthPolicies/s1", "Policies")]
    [InlineData("/policies", "Policies")]
    [InlineData("/$batch", "None")]
    [InlineData("/", "Other")]
    [InlineData("/somethingNew/x", "Other")]
    public void EveryPathFamilyFallsInItsArea(string path, string area)
    {
        Assert.Equal(Area(area), GraphRequestClassifier.Classify("GET", path).Area);
    }

    [Theory]
    [InlineData("/users?$select=id")]
    [InlineData("/groups/g1/members?$top=5")]
    [InlineData("/identity/conditionalAccess/policies")]
    [InlineData("/identityGovernance/accessReviews/definitions")]
    [InlineData("/roleManagement/directory/roleAssignments")]
    [InlineData("/policies/roleManagementPolicies")]
    [InlineData("/groupLifecyclePolicies")]
    [InlineData("/$batch")]
    [InlineData("/subscribedSkus")]
    public void TheApiVersionDoesNotChangeTheCost(string path)
    {
        var bare = GraphRequestClassifier.Classify("GET", path);

        Assert.Equal(bare, GraphRequestClassifier.Classify("GET", "/v1.0" + path));
        Assert.Equal(bare, GraphRequestClassifier.Classify("GET", "/beta" + path));
        Assert.Equal(bare, GraphRequestClassifier.Classify("GET", "/BETA" + path));
    }

    [Fact]
    public void ThePathIsCaseInsensitive()
    {
        Assert.Equal(GraphServiceArea.ConditionalAccess, GraphRequestClassifier.Classify("GET", "/v1.0/Identity/ConditionalAccess/NamedLocations").Area);
        Assert.Equal(GraphRequestClassifier.Classify("GET", "/groups/g1/members"), GraphRequestClassifier.Classify("get", "/GROUPS/G1/MEMBERS"));
    }

    [Theory]
    [InlineData("/identity/conditionalAccess/policies")]
    [InlineData("/identityGovernance/accessReviews/definitions")]
    [InlineData("/roleManagement/directory/roleAssignments")]
    [InlineData("/policies/roleManagementPolicies")]
    [InlineData("/groupLifecyclePolicies")]
    [InlineData("/$batch")]
    public void OnlyDirectoryRequestsCarryResourceUnits(string path)
    {
        Assert.Equal(0, GraphRequestClassifier.Classify("GET", path + "?$expand=x").ResourceUnits);
        Assert.Equal(0, GraphRequestClassifier.Classify("POST", path).ResourceUnits);
    }

    [Fact]
    public void AWriteOutsideTheDirectoryIsStillMarkedAsAWrite()
    {
        Assert.Equal(new GraphRequestCost(GraphServiceArea.ConditionalAccess, 0, true), GraphRequestClassifier.Classify("POST", "/identity/conditionalAccess/policies"));
        Assert.Equal(new GraphRequestCost(GraphServiceArea.IdentityGovernance, 0, false), GraphRequestClassifier.Classify("GET", "/identityGovernance/accessReviews/definitions"));
    }

    // ---- the published cost table -------------------------------------------------------------------------

    [Theory]
    [InlineData("/users", 2)]
    [InlineData("/users/u1", 2)]
    [InlineData("/users/u1/memberOf", 2)]
    [InlineData("/users/u1/transitiveMemberOf", 2)]
    [InlineData("/users/u1/ownedObjects", 2)]
    [InlineData("/users/u1/licenseDetails", 2)]
    [InlineData("/users/u1/manager", 1)]
    [InlineData("/me/memberOf", 2)]
    [InlineData("/groups", 1)]
    [InlineData("/groups/g1", 1)]
    [InlineData("/groups/g1/members", 3)]
    [InlineData("/groups/g1/transitiveMembers", 5)]
    [InlineData("/groups/g1/owners", 1)]
    [InlineData("/applications", 2)]
    [InlineData("/applications/a1", 2)]
    [InlineData("/applications/a1/extensionProperties", 2)]
    [InlineData("/applications/a1/extensionProperties/e1", 2)]
    [InlineData("/applications/a1/federatedIdentityCredentials", 1)]
    [InlineData("/servicePrincipals", 1)]
    [InlineData("/servicePrincipals/s1", 1)]
    [InlineData("/servicePrincipals/s1/appRoleAssignments", 2)]
    [InlineData("/servicePrincipals/s1/appRoleAssignedTo", 1)]
    [InlineData("/oauth2PermissionGrants", 2)]
    [InlineData("/oauth2PermissionGrants/o1", 2)]
    [InlineData("/directory/deletedItems/x1", 1)]
    [InlineData("/administrativeUnits", 1)]
    [InlineData("/administrativeUnits/au1/members", 1)]
    [InlineData("/subscribedSkus", 3)]
    [InlineData("/subscribedSkus/s1", 3)]
    [InlineData("/organization", 1)]
    [InlineData("/policies/tokenLifetimePolicies", 1)]
    [InlineData("/policies/crossTenantAccessPolicy/default", 1)]
    public void AReadCostsItsPublishedBaseUnits(string path, int units)
    {
        var cost = GraphRequestClassifier.Classify("GET", path);

        Assert.Equal(new GraphRequestCost(GraphServiceArea.Directory, units, false), cost);
    }

    [Theory]
    [InlineData("/groups/g1/members?$select=id", 2)]
    [InlineData("/groups/g1/members?$expand=owners", 4)]
    [InlineData("/groups/g1/members?$top=5", 2)]
    [InlineData("/groups/g1/members?$top=19", 2)]
    [InlineData("/groups/g1/members?$top=20", 3)]
    [InlineData("/groups/g1/members?$top=999", 3)]
    [InlineData("/groups/g1/members?$top=abc", 3)]
    [InlineData("/groups/g1/members?$select=id&$top=5", 1)]
    [InlineData("/groups/g1/members?$select=id&$expand=owners&$top=5", 2)]
    [InlineData("/groups/g1/transitiveMembers?$select=id&$top=1", 3)]
    [InlineData("/groups?$select=id", 1)]
    [InlineData("/groups?$select=id&$top=1", 1)]
    [InlineData("/users?$select=id&$top=1", 1)]
    [InlineData("/groups/g1/members?$filter=x&$count=true", 3)]
    [InlineData("/groups/g1/members?%24select=id", 2)]
    [InlineData("/groups/g1/members?%24SELECT=id&%24top=5", 1)]
    [InlineData("/groups/g1/members?$Expand=owners", 4)]
    public void QueryOptionsAdjustTheCostAndItNeverDropsBelowOne(string pathAndQuery, int units)
    {
        Assert.Equal(units, GraphRequestClassifier.Classify("GET", pathAndQuery).ResourceUnits);
    }

    [Theory]
    [InlineData("/applications(appId='00000000-0000-0000-0000-000000000001')", 2)]
    [InlineData("/applications(uniqueName='orders-api')", 2)]
    [InlineData("/applications(appId='00000000-0000-0000-0000-000000000001')/extensionProperties", 2)]
    [InlineData("/servicePrincipals(appId='00000000-0000-0000-0000-000000000001')/appRoleAssignments", 2)]
    [InlineData("/servicePrincipals(appId='00000000-0000-0000-0000-000000000001')", 1)]
    [InlineData("/groups/g1/members/$count", 3)]
    [InlineData("/groups/g1/members/$ref", 3)]
    [InlineData("/groups/g1/members/microsoft.graph.user", 3)]
    [InlineData("/groups/g1/members/microsoft.graph.user/$count", 3)]
    [InlineData("/groups/g1/transitiveMembers/microsoft.graph.servicePrincipal", 5)]
    [InlineData("/users/$count", 2)]
    [InlineData("/users/alice%40contoso.com", 2)]
    [InlineData("/users/alice%40contoso.com/memberOf", 2)]
    [InlineData("/applications%28appId%3D%27x%27%29", 2)]
    [InlineData("/groups/g1/%6Dembers", 3)]
    [InlineData("/users/bad%zzescape", 2)]
    public void KeySegmentsTrailingSegmentsAndPercentEncodingAddressTheSameResource(string path, int units)
    {
        var cost = GraphRequestClassifier.Classify("GET", path);

        Assert.Equal(GraphServiceArea.Directory, cost.Area);
        Assert.Equal(units, cost.ResourceUnits);
    }

    [Fact]
    public void AKeySegmentIsSplitIntoTheCollectionAndAKey()
    {
        Assert.Equal(["applications", "{key}", "extensionproperties"], GraphRequestClassifier.Segments("/v1.0/applications(appId='A')/extensionProperties"));
        Assert.Equal(["groups", "g1", "members"], GraphRequestClassifier.Segments("/beta/groups/g1/members/microsoft.graph.user/$count"));
        Assert.Equal(["users"], GraphRequestClassifier.Segments("/users/$count"));
        Assert.Equal(["$count"], GraphRequestClassifier.Segments("/$count"));
    }

    [Theory]
    [InlineData("POST", "/groups")]
    [InlineData("PATCH", "/users/u1")]
    [InlineData("PUT", "/applications/a1/logo")]
    [InlineData("DELETE", "/applications/a1")]
    [InlineData("POST", "/groups/g1/members/$ref")]
    [InlineData("DELETE", "/groups/g1/members/u1/$ref")]
    [InlineData("POST", "/users?$select=id")]
    [InlineData("POST", "/subscribedSkus")]
    [InlineData("PATCH", "/groups/g1/members")]
    [InlineData("POST", "/applications(appId='x')/federatedIdentityCredentials")]
    [InlineData("PATCH", "/policies/defaultAppManagementPolicy")]
    [InlineData("delete", "/directory/deletedItems/x1")]
    public void AWriteCostsOneUnitAndOneWrite(string method, string path)
    {
        Assert.Equal(new GraphRequestCost(GraphServiceArea.Directory, 1, true), GraphRequestClassifier.Classify(method, path));
    }

    [Fact]
    public void GetByIdsCostsFiveUnitsAndAWrite()
    {
        Assert.Equal(new GraphRequestCost(GraphServiceArea.Directory, 5, true), GraphRequestClassifier.Classify("POST", "/directoryObjects/getByIds"));
        Assert.Equal(new GraphRequestCost(GraphServiceArea.Directory, 5, true), GraphRequestClassifier.Classify("POST", "/v1.0/directoryObjects/getByIds"));
        Assert.Equal(new GraphRequestCost(GraphServiceArea.Directory, 5, true), GraphRequestClassifier.Classify("post", "/beta/DirectoryObjects/GetByIds"));
    }

    [Fact]
    public void HeadAndOptionsAreReads()
    {
        Assert.False(GraphRequestClassifier.Classify("HEAD", "/users").IsWrite);
        Assert.False(GraphRequestClassifier.Classify("OPTIONS", "/users").IsWrite);
    }

    [Theory]
    [InlineData("/v1.0/users?$skiptoken=X%27abc%27&$select=id", "Directory", 1)]
    [InlineData("/beta/groups/g1/members?$skiptoken=RFNwdAIAAQAAA&$top=5", "Directory", 2)]
    [InlineData("/v1.0/identity/conditionalAccess/policies?$skiptoken=abc", "ConditionalAccess", 0)]
    [InlineData("/beta/identityGovernance/accessReviews/definitions?$skiptoken=abc", "IdentityGovernance", 0)]
    public void AnAbsoluteNextLinkPathIsClassifiedLikeTheFirstPage(string pathAndQuery, string area, int units)
    {
        // The client classifies an @odata.nextLink by its absolute path and query (the API version included).
        var cost = GraphRequestClassifier.Classify("GET", pathAndQuery);

        Assert.Equal(Area(area), cost.Area);
        Assert.Equal(units, cost.ResourceUnits);
    }

    [Fact]
    public void TheInputsAreValidated()
    {
        Assert.Throws<ArgumentException>(() => GraphRequestClassifier.Classify(" ", "/users"));
        Assert.Throws<ArgumentNullException>(() => GraphRequestClassifier.Classify("GET", null!));
    }
}
