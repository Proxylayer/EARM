using System;
using EArm.Core;

namespace EArm.Engine.Planning;

/// <summary>
/// The apply and delete order Phases are a tie-breaker inside a dependency batch (explicit
/// and implicit dependencies decide the batches); the delete order is the reverse, for 'deployment delete' (TemplateDeleter).
/// </summary>
public static class ApplyPhases
{
    /// <summary>Phase for a type that is not in the table (applied with the groups).</summary>
    public const int UnknownTypePhase = 3;

    /// <summary>
    /// 1: namedLocations, authenticationContextClassReferences, authenticationStrengths, authenticationMethodConfigurations, roleDefinitions, users,
    /// applications · 2: servicePrincipals, federatedIdentityCredentials, passwordCredentials, keyCredentials, extensionProperties · 3: groups ·
    /// 4: administrativeUnits, roleManagementPolicies (also ordered before the PIM schedule requests they govern) · 5: roleAssignments, roleEligibilityScheduleRequests, roleAssignmentScheduleRequests, privilegedAccess/groupEligibilityScheduleRequests,
    /// privilegedAccess/groupAssignmentScheduleRequests, appRoleAssignments, oauth2PermissionGrants ·
    /// 6: conditionalAccess/policies. Entitlement management: catalogs 4, catalogResources and accessPackages 5, resourceRoleScopes and
    /// accessPackages/assignmentPolicies 6 (role scopes are also ordered after the catalog resources and before the policies they relate to).
    /// </summary>
    public static int Of(string type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return Relative(type) switch
        {
            "applications" => 1,
            "users" => 1,
            "roleDefinitions" => 1,
            "conditionalAccess/namedLocations" => 1,
            "conditionalAccess/authenticationContextClassReferences" => 1,
            "conditionalAccess/authenticationStrengths" => 1,
            "authenticationMethodConfigurations" => 1,
            "servicePrincipals" => 2,
            "applications/federatedIdentityCredentials" => 2,
            "applications/passwordCredentials" => 2,
            "applications/keyCredentials" => 2,
            "applications/extensionProperties" => 2,
            "groups" => 3,
            "administrativeUnits" => 4,
            "roleManagementPolicies" => 4,
            "roleAssignments" => 5,
            "roleEligibilityScheduleRequests" => 5,
            "privilegedAccess/groupEligibilityScheduleRequests" => 5,
            "roleAssignmentScheduleRequests" => 5,
            "privilegedAccess/groupAssignmentScheduleRequests" => 5,
            "appRoleAssignments" => 5,
            "oauth2PermissionGrants" => 5,
            "conditionalAccess/policies" => 6,
            "entitlementManagement/catalogs" => 4,
            "entitlementManagement/catalogResources" => 5,
            "entitlementManagement/accessPackages" => 5,
            "entitlementManagement/resourceRoleScopes" => 6,
            "entitlementManagement/accessPackages/assignmentPolicies" => 6,
            "groupLifecyclePolicies" => 1,
            "policies/tokenLifetimePolicies" => 1,
            "policies/claimsMappingPolicies" => 1,
            "policies/appManagementPolicies" => 1,
            "policies/defaultAppManagementPolicy" => 1,
            "policies/crossTenantAccessPolicy" => 1,
            "policies/crossTenantAccessPolicy/partners" => 1,
            _ => UnknownTypePhase,
        };
    }

    /// <summary>
    /// Complete-mode delete order: CA policies → assignments/eligibilities/PIM active assignments/grants/PIM policies (kept, never deleted) → administrativeUnits → groups →
    /// passwordCredentials/federatedIdentityCredentials/keyCredentials/extensionProperties → servicePrincipals → applications → users → roleDefinitions
    /// → authenticationStrengths → authenticationContextClassReferences → namedLocations → authenticationMethodConfigurations (reset, never removed).
    /// Entitlement management: assignment policies and resource role scopes (1) → access packages (2) → catalog resources (3) → catalogs (4).
    /// </summary>
    public static int DeleteOrderOf(string type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return Relative(type) switch
        {
            "conditionalAccess/policies" => 1,
            "roleAssignments" => 2,
            "roleEligibilityScheduleRequests" => 2,
            "privilegedAccess/groupEligibilityScheduleRequests" => 2,
            "roleAssignmentScheduleRequests" => 2,
            "privilegedAccess/groupAssignmentScheduleRequests" => 2,
            "appRoleAssignments" => 2,
            "oauth2PermissionGrants" => 2,
            "roleManagementPolicies" => 2,
            "administrativeUnits" => 3,
            "groups" => 4,
            "applications/passwordCredentials" => 5,
            "applications/federatedIdentityCredentials" => 5,
            "applications/keyCredentials" => 5,
            "applications/extensionProperties" => 5,
            "servicePrincipals" => 6,
            "applications" => 7,
            "users" => 8,
            "roleDefinitions" => 9,
            "conditionalAccess/authenticationStrengths" => 10,
            "conditionalAccess/authenticationContextClassReferences" => 11,
            "conditionalAccess/namedLocations" => 12,
            "authenticationMethodConfigurations" => 13,
            "entitlementManagement/accessPackages/assignmentPolicies" => 1,
            "entitlementManagement/resourceRoleScopes" => 1,
            "entitlementManagement/accessPackages" => 2,
            "entitlementManagement/catalogResources" => 3,
            "entitlementManagement/catalogs" => 4,
            "groupLifecyclePolicies" => 12,
            "policies/tokenLifetimePolicies" => 12,
            "policies/claimsMappingPolicies" => 12,
            "policies/appManagementPolicies" => 12,
            "policies/defaultAppManagementPolicy" => 13,
            "policies/crossTenantAccessPolicy" => 13,
            "policies/crossTenantAccessPolicy/partners" => 12,
            _ => 4,
        };
    }

    /// <summary>The type without the <c>Microsoft.Entra/</c> namespace, in the catalog's canonical casing where known.</summary>
    private static string Relative(string type)
    {
        string normalized = ResourceIdentifier.NormalizeType(type);
        string prefix = ResourceIdentifier.ProviderNamespace + "/";
        string relative = normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? normalized[prefix.Length..] : normalized;
        foreach (var known in KnownTypes)
        {
            if (known.Equals(relative, StringComparison.OrdinalIgnoreCase))
            {
                return known;
            }
        }

        return relative;
    }

    private static readonly string[] KnownTypes =
    [
        "applications",
        "users",
        "roleDefinitions",
        "conditionalAccess/namedLocations",
        "conditionalAccess/authenticationContextClassReferences",
        "conditionalAccess/authenticationStrengths",
        "authenticationMethodConfigurations",
        "servicePrincipals",
        "applications/federatedIdentityCredentials",
        "applications/passwordCredentials",
        "applications/keyCredentials",
        "applications/extensionProperties",
        "groups",
        "administrativeUnits",
        "roleManagementPolicies",
        "roleAssignments",
        "roleEligibilityScheduleRequests",
        "privilegedAccess/groupEligibilityScheduleRequests",
        "roleAssignmentScheduleRequests",
        "privilegedAccess/groupAssignmentScheduleRequests",
        "appRoleAssignments",
        "oauth2PermissionGrants",
        "conditionalAccess/policies",
        "entitlementManagement/catalogs",
        "entitlementManagement/catalogResources",
        "entitlementManagement/accessPackages",
        "entitlementManagement/resourceRoleScopes",
        "entitlementManagement/accessPackages/assignmentPolicies",
        "groupLifecyclePolicies",
        "policies/tokenLifetimePolicies",
        "policies/claimsMappingPolicies",
        "policies/appManagementPolicies",
        "policies/defaultAppManagementPolicy",
        "policies/crossTenantAccessPolicy",
        "policies/crossTenantAccessPolicy/partners",
    ];
}
