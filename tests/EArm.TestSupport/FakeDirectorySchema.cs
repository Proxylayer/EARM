using System;
using System.Collections.Generic;
using System.Linq;

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
    private static readonly string[] UserAll =
    [
        "aboutMe",
        "accountEnabled",
        "ageGroup",
        "assignedLicenses",
        "assignedPlans",
        "birthday",
        "businessPhones",
        "city",
        "companyName",
        "consentProvidedForMinor",
        "country",
        "createdDateTime",
        "creationType",
        "customSecurityAttributes",
        "deletedDateTime",
        "department",
        "displayName",
        "employeeHireDate",
        "employeeLeaveDateTime",
        "employeeId",
        "employeeOrgData",
        "employeeType",
        "externalUserState",
        "externalUserStateChangeDateTime",
        "faxNumber",
        "givenName",
        "hireDate",
        "id",
        "identities",
        "imAddresses",
        "interests",
        "isManagementRestricted",
        "isResourceAccount",
        "jobTitle",
        "lastPasswordChangeDateTime",
        "legalAgeGroupClassification",
        "licenseAssignmentStates",
        "mail",
        "mailboxSettings",
        "mailNickname",
        "mobilePhone",
        "mySite",
        "officeLocation",
        "onPremisesDistinguishedName",
        "onPremisesDomainName",
        "onPremisesExtensionAttributes",
        "onPremisesImmutableId",
        "onPremisesLastSyncDateTime",
        "onPremisesProvisioningErrors",
        "onPremisesSamAccountName",
        "onPremisesSecurityIdentifier",
        "onPremisesSyncEnabled",
        "onPremisesUserPrincipalName",
        "otherMails",
        "passwordPolicies",
        "passwordProfile",
        "pastProjects",
        "postalCode",
        "preferredDataLocation",
        "preferredLanguage",
        "preferredName",
        "provisionedPlans",
        "proxyAddresses",
        "refreshTokensValidFromDateTime",
        "responsibilities",
        "serviceProvisioningErrors",
        "schools",
        "securityIdentifier",
        "showInAddressList",
        "signInActivity",
        "signInSessionsValidFromDateTime",
        "skills",
        "state",
        "streetAddress",
        "surname",
        "usageLocation",
        "userPrincipalName",
        "userType",
    ];

    private static readonly string[] GroupAll =
    [
        "accessType",
        "allowExternalSenders",
        "assignedLabels",
        "assignedLicenses",
        "autoSubscribeNewMembers",
        "classification",
        "createdDateTime",
        "deletedDateTime",
        "description",
        "displayName",
        "expirationDateTime",
        "groupTypes",
        "hasMembersWithLicenseErrors",
        "hideFromAddressLists",
        "hideFromOutlookClients",
        "id",
        "isArchived",
        "isAssignableToRole",
        "isFavorite",
        "isManagementRestricted",
        "isSubscribedByMail",
        "licenseProcessingState",
        "mail",
        "mailEnabled",
        "mailNickname",
        "membershipRule",
        "membershipRuleProcessingState",
        "onPremisesDomainName",
        "onPremisesExtensionAttributes",
        "onPremisesLastSyncDateTime",
        "onPremisesNetBiosName",
        "onPremisesProvisioningErrors",
        "onPremisesSamAccountName",
        "onPremisesSecurityIdentifier",
        "onPremisesSyncEnabled",
        "preferredDataLocation",
        "preferredLanguage",
        "proxyAddresses",
        "renewedDateTime",
        "resourceBehaviorOptions",
        "resourceProvisioningOptions",
        "securityEnabled",
        "securityIdentifier",
        "serviceProvisioningErrors",
        "theme",
        "uniqueName",
        "unseenConversationsCount",
        "unseenCount",
        "unseenMessagesCount",
        "visibility",
    ];

    private static readonly string[] GroupDefault =
    [
        "classification",
        "createdDateTime",
        "deletedDateTime",
        "description",
        "displayName",
        "expirationDateTime",
        "groupTypes",
        "hasMembersWithLicenseErrors",
        "id",
        "isArchived",
        "isAssignableToRole",
        "mail",
        "mailEnabled",
        "mailNickname",
        "membershipRule",
        "membershipRuleProcessingState",
        "onPremisesDomainName",
        "onPremisesExtensionAttributes",
        "onPremisesLastSyncDateTime",
        "onPremisesNetBiosName",
        "onPremisesProvisioningErrors",
        "onPremisesSamAccountName",
        "onPremisesSecurityIdentifier",
        "onPremisesSyncEnabled",
        "preferredDataLocation",
        "preferredLanguage",
        "proxyAddresses",
        "renewedDateTime",
        "resourceBehaviorOptions",
        "resourceProvisioningOptions",
        "securityEnabled",
        "securityIdentifier",
        "serviceProvisioningErrors",
        "theme",
        "uniqueName",
        "visibility",
    ];

    private static readonly string[] ApplicationAll =
    [
        "addIns",
        "api",
        "appId",
        "applicationTemplateId",
        "appRoles",
        "authenticationBehaviors",
        "certification",
        "createdByAppId",
        "createdDateTime",
        "deletedDateTime",
        "description",
        "disabledByMicrosoftStatus",
        "displayName",
        "groupMembershipClaims",
        "id",
        "identifierUris",
        "info",
        "isDeviceOnlyAuthSupported",
        "isFallbackPublicClient",
        "keyCredentials",
        "logo",
        "managerApplications",
        "nativeAuthenticationApisEnabled",
        "notes",
        "oauth2RequiredPostResponse",
        "optionalClaims",
        "parentalControlSettings",
        "passwordCredentials",
        "publicClient",
        "publisherDomain",
        "requestSignatureVerification",
        "requiredResourceAccess",
        "samlMetadataUrl",
        "serviceManagementReference",
        "servicePrincipalLockConfiguration",
        "signInAudience",
        "spa",
        "tags",
        "tokenEncryptionKeyId",
        "uniqueName",
        "verifiedPublisher",
        "web",
    ];

    private static readonly string[] ApplicationDefault =
    [
        "addIns",
        "api",
        "appId",
        "applicationTemplateId",
        "appRoles",
        "certification",
        "createdByAppId",
        "createdDateTime",
        "deletedDateTime",
        "description",
        "disabledByMicrosoftStatus",
        "displayName",
        "groupMembershipClaims",
        "id",
        "identifierUris",
        "info",
        "isDeviceOnlyAuthSupported",
        "isFallbackPublicClient",
        "keyCredentials",
        "logo",
        "managerApplications",
        "nativeAuthenticationApisEnabled",
        "notes",
        "oauth2RequiredPostResponse",
        "optionalClaims",
        "parentalControlSettings",
        "passwordCredentials",
        "publicClient",
        "publisherDomain",
        "requestSignatureVerification",
        "requiredResourceAccess",
        "samlMetadataUrl",
        "serviceManagementReference",
        "servicePrincipalLockConfiguration",
        "signInAudience",
        "spa",
        "tags",
        "tokenEncryptionKeyId",
        "uniqueName",
        "verifiedPublisher",
        "web",
    ];

    private static readonly string[] ServicePrincipalAll =
    [
        "accountEnabled",
        "addIns",
        "alternativeNames",
        "appDescription",
        "appDisplayName",
        "appId",
        "applicationTemplateId",
        "appOwnerOrganizationId",
        "appRoleAssignmentRequired",
        "appRoles",
        "createdByAppId",
        "customSecurityAttributes",
        "deletedDateTime",
        "description",
        "disabledByMicrosoftStatus",
        "displayName",
        "homepage",
        "id",
        "info",
        "keyCredentials",
        "loginUrl",
        "logoutUrl",
        "notes",
        "notificationEmailAddresses",
        "oauth2PermissionScopes",
        "passwordCredentials",
        "preferredSingleSignOnMode",
        "preferredTokenSigningKeyThumbprint",
        "replyUrls",
        "resourceSpecificApplicationPermissions",
        "samlSingleSignOnSettings",
        "servicePrincipalNames",
        "servicePrincipalType",
        "signInAudience",
        "tags",
        "tokenEncryptionKeyId",
        "verifiedPublisher",
    ];

    private static readonly string[] ServicePrincipalDefault =
    [
        "accountEnabled",
        "addIns",
        "alternativeNames",
        "appDescription",
        "appDisplayName",
        "appId",
        "applicationTemplateId",
        "appOwnerOrganizationId",
        "appRoleAssignmentRequired",
        "appRoles",
        "createdByAppId",
        "deletedDateTime",
        "description",
        "disabledByMicrosoftStatus",
        "displayName",
        "homepage",
        "id",
        "info",
        "keyCredentials",
        "loginUrl",
        "logoutUrl",
        "notes",
        "notificationEmailAddresses",
        "oauth2PermissionScopes",
        "passwordCredentials",
        "preferredSingleSignOnMode",
        "preferredTokenSigningKeyThumbprint",
        "replyUrls",
        "resourceSpecificApplicationPermissions",
        "samlSingleSignOnSettings",
        "servicePrincipalNames",
        "servicePrincipalType",
        "signInAudience",
        "tags",
        "tokenEncryptionKeyId",
        "verifiedPublisher",
    ];

    private static readonly string[] AdministrativeUnitAll =
    [
        "description",
        "displayName",
        "id",
        "isMemberManagementRestricted",
        "membershipRule",
        "membershipRuleProcessingState",
        "membershipType",
        "visibility",
    ];

    private static readonly string[] AdministrativeUnitDefault =
    [
        "description",
        "displayName",
        "id",
        "isMemberManagementRestricted",
        "membershipRule",
        "membershipRuleProcessingState",
        "membershipType",
        "visibility",
    ];

    private static readonly string[] RoleDefinitionAll =
    [
        "description",
        "displayName",
        "id",
        "isBuiltIn",
        "isEnabled",
        "resourceScopes",
        "rolePermissions",
        "templateId",
        "version",
    ];

    private static readonly string[] RoleDefinitionDefault =
    [
        "description",
        "displayName",
        "id",
        "isBuiltIn",
        "isEnabled",
        "resourceScopes",
        "rolePermissions",
        "templateId",
        "version",
    ];

    private static readonly string[] PermissionGrantAll =
    [
        "clientId",
        "consentType",
        "id",
        "principalId",
        "resourceId",
        "scope",
    ];

    private static readonly string[] PermissionGrantDefault =
    [
        "clientId",
        "consentType",
        "id",
        "principalId",
        "resourceId",
        "scope",
    ];

    private static readonly string[] FederatedCredentialAll =
    [
        "audiences",
        "description",
        "id",
        "issuer",
        "name",
        "subject",
    ];

    private static readonly string[] FederatedCredentialDefault =
    [
        "audiences",
        "description",
        "id",
        "issuer",
        "name",
        "subject",
    ];

    private static readonly string[] AppRoleAssignmentAll =
    [
        "appRoleId",
        "createdDateTime",
        "deletedDateTime",
        "id",
        "principalDisplayName",
        "principalId",
        "principalType",
        "resourceDisplayName",
        "resourceId",
    ];

    private static readonly string[] AppRoleAssignmentDefault =
    [
        "appRoleId",
        "createdDateTime",
        "deletedDateTime",
        "id",
        "principalDisplayName",
        "principalId",
        "principalType",
        "resourceDisplayName",
        "resourceId",
    ];
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
