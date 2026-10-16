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
    /// applications · 2: servicePrincipals, federatedIdentityCredentials, passwordCredentials · 3: groups ·
    /// 4: administrativeUnits · 5: roleAssignments, roleEligibilityScheduleRequests, appRoleAssignments, oauth2PermissionGrants ·
    /// 6: conditionalAccess/policies.
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
            "groups" => 3,
            "administrativeUnits" => 4,
            "roleAssignments" => 5,
            "roleEligibilityScheduleRequests" => 5,
            "appRoleAssignments" => 5,
            "oauth2PermissionGrants" => 5,
            "conditionalAccess/policies" => 6,
            _ => UnknownTypePhase,
        };
    }

    /// <summary>
    /// Complete-mode delete order: CA policies → assignments/eligibilities/grants → administrativeUnits → groups →
    /// passwordCredentials/federatedIdentityCredentials → servicePrincipals → applications → users → roleDefinitions
    /// → authenticationStrengths → authenticationContextClassReferences → namedLocations → authenticationMethodConfigurations (reset, never removed).
    /// </summary>
    public static int DeleteOrderOf(string type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return Relative(type) switch
        {
            "conditionalAccess/policies" => 1,
            "roleAssignments" => 2,
            "roleEligibilityScheduleRequests" => 2,
            "appRoleAssignments" => 2,
            "oauth2PermissionGrants" => 2,
            "administrativeUnits" => 3,
            "groups" => 4,
            "applications/passwordCredentials" => 5,
            "applications/federatedIdentityCredentials" => 5,
            "servicePrincipals" => 6,
            "applications" => 7,
            "users" => 8,
            "roleDefinitions" => 9,
            "conditionalAccess/authenticationStrengths" => 10,
            "conditionalAccess/authenticationContextClassReferences" => 11,
            "conditionalAccess/namedLocations" => 12,
            "authenticationMethodConfigurations" => 13,
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
        "groups",
        "administrativeUnits",
        "roleAssignments",
        "roleEligibilityScheduleRequests",
        "appRoleAssignments",
        "oauth2PermissionGrants",
        "conditionalAccess/policies",
    ];
}
