using System;
using System.Collections.Generic;
using static EArm.TestSupport.FakeDirectorySchemaProperties;

namespace EArm.TestSupport;

/// <summary>
/// The property vocabulary of the typed Graph objects the <see cref="FakeDirectory"/> serves, taken from the Learn resource pages
/// (https://learn.microsoft.com/en-us/graph/api/resources/user, group, application, serviceprincipal, administrativeunit, unifiedroledefinition,
/// oauth2permissiongrant, federatedidentitycredential, approleassignment; graph-rest-1.0): every documented property (<c>$select</c> of anything else is a
/// 400 "Could not find a property named ...") and the subset returned WITHOUT <c>$select</c> (the rest is marked "Requires $select to retrieve").
/// Users return only the eleven properties Learn lists on user-get (businessPhones, displayName, givenName, id, jobTitle, mail, mobilePhone,
/// officeLocation, preferredLanguage, surname, userPrincipalName). <c>id</c> and every <c>@odata.*</c> annotation are always returned.
/// </summary>
internal sealed class FakeDirectorySchema
{
    private readonly HashSet<string> _known;
    private readonly HashSet<string> _default;

    private FakeDirectorySchema(string name, IEnumerable<string> known, IEnumerable<string> defaults)
    {
        Name = name;
        _known = new HashSet<string>(known, StringComparer.OrdinalIgnoreCase);
        _default = new HashSet<string>(defaults, StringComparer.OrdinalIgnoreCase);
        _known.UnionWith(_default);
    }

    /// <summary>The OData type name used in error messages (<c>microsoft.graph.user</c>).</summary>
    public string Name { get; }

    public IReadOnlyCollection<string> Default => _default;

    public bool IsKnown(string property)
    {
        // Schema and directory extension attributes (extension_{appId}_name, ext{8 chars}_name) are open-ended.
        return _known.Contains(property)
            || property.StartsWith("extension_", StringComparison.OrdinalIgnoreCase)
            || (property.Length > 12 && property.StartsWith("ext", StringComparison.OrdinalIgnoreCase) && property[11] == '_');
    }

    /// <summary>The schema of a collection path (<c>/users</c>, <c>/applications/{id}/federatedIdentityCredentials</c>...), or null for an untyped collection.</summary>
    public static FakeDirectorySchema? ForCollection(string collectionPath)
    {
        string path = collectionPath.TrimEnd('/');
        if (path.EndsWith("/federatedIdentityCredentials", StringComparison.OrdinalIgnoreCase))
        {
            return FederatedCredentials;
        }

        if (path.EndsWith("/appRoleAssignedTo", StringComparison.OrdinalIgnoreCase) || path.EndsWith("/appRoleAssignments", StringComparison.OrdinalIgnoreCase))
        {
            return AppRoleAssignments;
        }

        return path.ToLowerInvariant() switch
        {
            "/users" => Users,
            "/groups" => Groups,
            "/applications" => Applications,
            "/serviceprincipals" => ServicePrincipals,
            "/directory/administrativeunits" => AdministrativeUnits,
            "/rolemanagement/directory/roledefinitions" => RoleDefinitions,
            "/oauth2permissiongrants" => PermissionGrants,
            _ => null,
        };
    }

    /// <summary>The schema of an <c>@odata.type</c> (<c>#microsoft.graph.group</c>).</summary>
    public static FakeDirectorySchema? ForODataType(string? odataType)
    {
        return odataType?.TrimStart('#').ToLowerInvariant() switch
        {
            "microsoft.graph.user" => Users,
            "microsoft.graph.group" => Groups,
            "microsoft.graph.application" => Applications,
            "microsoft.graph.serviceprincipal" => ServicePrincipals,
            "microsoft.graph.administrativeunit" => AdministrativeUnits,
            _ => null,
        };
    }

    public static FakeDirectorySchema Users { get; } = new("microsoft.graph.user", UserAll,
        ["businessPhones", "displayName", "givenName", "id", "jobTitle", "mail", "mobilePhone", "officeLocation", "preferredLanguage", "surname", "userPrincipalName"]);

    public static FakeDirectorySchema Groups { get; } = new("microsoft.graph.group", GroupAll, GroupDefault);

    public static FakeDirectorySchema Applications { get; } = new("microsoft.graph.application", ApplicationAll, ApplicationDefault);

    public static FakeDirectorySchema ServicePrincipals { get; } = new("microsoft.graph.servicePrincipal", ServicePrincipalAll, ServicePrincipalDefault);

    // deletedDateTime is inherited from directoryObject (https://learn.microsoft.com/en-us/graph/api/resources/directoryobject), not repeated in the administrativeUnit table.
    public static FakeDirectorySchema AdministrativeUnits { get; } = new("microsoft.graph.administrativeUnit", [.. AdministrativeUnitAll, "deletedDateTime"], AdministrativeUnitDefault);

    public static FakeDirectorySchema RoleDefinitions { get; } = new("microsoft.graph.unifiedRoleDefinition", RoleDefinitionAll, RoleDefinitionDefault);

    public static FakeDirectorySchema PermissionGrants { get; } = new("microsoft.graph.oAuth2PermissionGrant", PermissionGrantAll, PermissionGrantDefault);

    // claimsMatchingExpression is the beta-only property EARM reads on the beta endpoint.
    public static FakeDirectorySchema FederatedCredentials { get; } = new("microsoft.graph.federatedIdentityCredential", [.. FederatedCredentialAll, "claimsMatchingExpression"], [.. FederatedCredentialDefault, "claimsMatchingExpression"]);

    public static FakeDirectorySchema AppRoleAssignments { get; } = new("microsoft.graph.appRoleAssignment", AppRoleAssignmentAll, AppRoleAssignmentDefault);
}
