using System;
using System.Linq;
using EArm.Core.Types;
using EArm.Engine.Edges;
using EArm.Providers.Edges;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>Provider and edge-kind discovery over the whole catalog.</summary>
public sealed class DiscoveryTests
{
    [Fact]
    public void EveryCatalogType_HasADiscoveredProvider_OfTheMatchingTypeName()
    {
        var catalog = ResourceTypeCatalog.Default;
        var registry = ProviderDiscovery.CreateRegistry(catalog);

        var missing = catalog.Types.Select(t => t.Type).Where(t => !registry.Contains(t)).ToList();

        Assert.True(missing.Count == 0, "catalog types without a discovered provider: " + string.Join(", ", missing));
        Assert.All(registry.Providers, p => Assert.Equal(p.TypeName, registry.Get(p.TypeName).TypeName));
    }

    [Theory]
    [InlineData("Microsoft.Entra/applications", typeof(ApplicationsProvider))]
    [InlineData("Microsoft.Entra/servicePrincipals", typeof(ServicePrincipalsProvider))]
    [InlineData("Microsoft.Entra/groups", typeof(GroupsProvider))]
    [InlineData("Microsoft.Entra/applications/federatedIdentityCredentials", typeof(FederatedIdentityCredentialsProvider))]
    [InlineData("Microsoft.Entra/applications/passwordCredentials", typeof(PasswordCredentialsProvider))]
    [InlineData("Microsoft.Entra/users", typeof(UsersProvider))]
    [InlineData("Microsoft.Entra/conditionalAccess/namedLocations", typeof(NamedLocationsProvider))]
    [InlineData("Microsoft.Entra/conditionalAccess/policies", typeof(ConditionalAccessPoliciesProvider))]
    [InlineData("Microsoft.Entra/conditionalAccess/authenticationContextClassReferences", typeof(AuthenticationContextClassReferencesProvider))]
    [InlineData("Microsoft.Entra/authenticationMethodConfigurations", typeof(AuthenticationMethodConfigurationsProvider))]
    [InlineData("Microsoft.Entra/groupLifecyclePolicies", typeof(GroupLifecyclePoliciesProvider))]
    [InlineData("Microsoft.Entra/policies/defaultAppManagementPolicy", typeof(DefaultAppManagementPolicyProvider))]
    [InlineData("Microsoft.Entra/policies/appManagementPolicies", typeof(AppManagementPoliciesProvider))]
    [InlineData("Microsoft.Entra/policies/claimsMappingPolicies", typeof(ClaimsMappingPoliciesProvider))]
    [InlineData("Microsoft.Entra/policies/tokenLifetimePolicies", typeof(TokenLifetimePoliciesProvider))]
    [InlineData("Microsoft.Entra/roleEligibilityScheduleRequests", typeof(RoleEligibilityScheduleRequestsProvider))]
    [InlineData("Microsoft.Entra/privilegedAccess/groupEligibilityScheduleRequests", typeof(GroupEligibilityScheduleRequestsProvider))]
    [InlineData("Microsoft.Entra/roleAssignmentScheduleRequests", typeof(RoleAssignmentScheduleRequestsProvider))]
    [InlineData("Microsoft.Entra/privilegedAccess/groupAssignmentScheduleRequests", typeof(GroupAssignmentScheduleRequestsProvider))]
    [InlineData("Microsoft.Entra/roleManagementPolicies", typeof(RoleManagementPoliciesProvider))]
    [InlineData("Microsoft.Entra/conditionalAccess/authenticationStrengths", typeof(AuthenticationStrengthsProvider))]
    [InlineData("Microsoft.Entra/appRoleAssignments", typeof(AppRoleAssignmentsProvider))]
    [InlineData("Microsoft.Entra/oauth2PermissionGrants", typeof(OAuth2PermissionGrantsProvider))]
    [InlineData("Microsoft.Entra/administrativeUnits", typeof(AdministrativeUnitsProvider))]
    [InlineData("Microsoft.Entra/roleDefinitions", typeof(RoleDefinitionsProvider))]
    [InlineData("Microsoft.Entra/roleAssignments", typeof(RoleAssignmentsProvider))]
    [InlineData("Microsoft.Entra/entitlementManagement/catalogs", typeof(AccessPackageCatalogsProvider))]
    [InlineData("Microsoft.Entra/entitlementManagement/catalogResources", typeof(CatalogResourcesProvider))]
    [InlineData("Microsoft.Entra/entitlementManagement/accessPackages", typeof(AccessPackagesProvider))]
    [InlineData("Microsoft.Entra/entitlementManagement/resourceRoleScopes", typeof(ResourceRoleScopesProvider))]
    [InlineData("Microsoft.Entra/entitlementManagement/accessPackages/assignmentPolicies", typeof(AssignmentPoliciesProvider))]
    public void TypeIsServedByItsProvider(string type, Type expected)
    {
        var registry = ProviderDiscovery.CreateRegistry(ResourceTypeCatalog.Default);

        Assert.IsType(expected, registry.Get(type));
        Assert.Contains(ProviderDiscovery.Discover(typeof(ProviderDiscovery).Assembly), b => b.TypeName == type && b.ProviderType == expected);
    }

    [Fact]
    public void EdgeKinds_IncludeThePartAKinds_AndTheDiscoveredPartBKinds()
    {
        var kinds = ProviderDiscovery.CreateEdgeKinds(ResourceTypeCatalog.Default);

        Assert.True(kinds.Contains(EdgeKind.GroupMembership));
        Assert.True(kinds.Contains(EdgeKind.Ownership));
        Assert.True(kinds.Contains(EdgeKind.AdministrativeUnitMembership));
        Assert.True(kinds.Contains(EdgeKind.Manager));
        Assert.True(kinds.Contains(EdgeKind.AppRoleAssignment));
        Assert.True(kinds.Contains(EdgeKind.DirectoryRole));
        Assert.True(kinds.Contains(EdgeKind.GroupLifecyclePolicy));
        Assert.True(kinds.Contains(EdgeKind.AppManagementPolicy));
        Assert.True(kinds.Contains(EdgeKind.ClaimsMappingPolicy));
        Assert.True(kinds.Contains(EdgeKind.TokenLifetimePolicy));
        Assert.Equal(10, kinds.Handlers.Count);
        Assert.Equal(4, kinds.OfPhase(EdgePhase.A).Count);
        Assert.Equal(6, kinds.OfPhase(EdgePhase.B).Count);
        Assert.True(kinds.TryGet(EdgeKind.DirectoryRole, out var directoryRoles));
        Assert.IsType<DirectoryRoleEdgeKind>(directoryRoles);
        Assert.Equal(EdgePhase.B, directoryRoles.Phase);
        var bindings = ProviderDiscovery.DiscoverEdgeKinds(typeof(ProviderDiscovery).Assembly);
        Assert.Equal(6, bindings.Count);
        Assert.Contains(bindings, b => b.Kind == EdgeKind.GroupLifecyclePolicy);
        Assert.Contains(bindings, b => b.Kind == EdgeKind.AppRoleAssignment);
        Assert.Contains(bindings, b => b.Kind == EdgeKind.DirectoryRole);
    }
}
