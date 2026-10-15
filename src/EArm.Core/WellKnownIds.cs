using System;
using System.Collections.Generic;
using System.Linq;

namespace EArm.Core;

/// <summary>A well-known resource application: the name <c>wellKnownApp()</c> accepts and its appId.</summary>
public sealed record WellKnownApp(string Name, string AppId, string DisplayName);

/// <summary>
/// A Microsoft Graph permission: an application permission (app role) or a delegated permission (OAuth2 scope).
/// <see cref="Verified"/> is <c>false</c> for an id that is not confirmed against the Microsoft Graph permissions
/// reference (https://learn.microsoft.com/graph/permissions-reference); such ids still resolve offline. The tables are
/// the OFFLINE fallback: when a tenant lookup is available it wins (a wrong table id must never beat the tenant).
/// Pinned by tests/EArm.Providers.Tests/Review1D_WellKnownIdsTests.cs.
/// </summary>
public sealed record WellKnownPermission(string Value, string Id, bool Verified = true);

/// <summary>A built-in directory role: its display name and the role template id (equal to the role definition id).</summary>
public sealed record WellKnownRoleTemplate(string DisplayName, string Id);

/// <summary>A built-in authentication strength policy (<c>/policies/authenticationStrengthPolicies</c>).</summary>
public sealed record WellKnownAuthenticationStrength(string Name, string DisplayName, string Id);

/// <summary>
/// The embedded id tables: resource application ids, Microsoft Graph app roles and delegated
/// scopes, directory role templates, built-in authentication strengths and the Conditional Access special values.
/// Runtime resolution by <c>value</c> is preferred; the tables are the offline fallback and the lint source.
/// Names and values are matched case-insensitively.
/// </summary>
public static class WellKnownIds
{
    public const string MicrosoftGraphAppId = "00000003-0000-0000-c000-000000000000";
    public const string ExchangeOnlineAppId = "00000002-0000-0ff1-ce00-000000000000";
    public const string SharePointOnlineAppId = "00000003-0000-0ff1-ce00-000000000000";
    public const string AzureServiceManagementAppId = "797f4846-ba00-4fd7-ba43-dac1f8f63013";
    public const string AzureKeyVaultAppId = "cfa8b339-82a2-471a-a3c9-0fc0be7a4093";
    public const string AzureStorageAppId = "e406a681-f3d4-42a8-90b6-c2b029497af1";
    public const string AzureDevOpsAppId = "499b84ac-1321-427f-aa17-267ca6975798";

    /// <summary>Resource applications by the name <c>wellKnownApp()</c> accepts (the ARM management plane is listed under both of its names).</summary>
    public static IReadOnlyList<WellKnownApp> Apps { get; } =
    [
        new("MicrosoftGraph", MicrosoftGraphAppId, "Microsoft Graph"),
        new("ExchangeOnline", ExchangeOnlineAppId, "Office 365 Exchange Online"),
        new("SharePointOnline", SharePointOnlineAppId, "Office 365 SharePoint Online"),
        new("AzureResourceManager", AzureServiceManagementAppId, "Windows Azure Service Management API"),
        new("AzureServiceManagement", AzureServiceManagementAppId, "Windows Azure Service Management API"),
        new("AzureKeyVault", AzureKeyVaultAppId, "Azure Key Vault"),
        new("AzureStorage", AzureStorageAppId, "Azure Storage"),
        new("AzureDevOps", AzureDevOpsAppId, "Azure DevOps"),
    ];

    /// <summary>Microsoft Graph application permissions (<c>appRoles</c> of the Graph service principal).</summary>
    public static IReadOnlyList<WellKnownPermission> GraphAppRoles { get; } =
    [
        new("User.Read.All", "df021288-bdef-4463-88db-98f22de89214"),
        new("User.ReadWrite.All", "741f803b-c850-494e-b5df-cde7c675a1ca"),
        new("Directory.Read.All", "7ab1d382-f21e-4acd-a863-ba3e13f7da61"),
        new("Directory.ReadWrite.All", "19dbc75e-c2e2-444c-a770-ec69d8559fc7"),
        new("Group.ReadWrite.All", "62a82d76-70ea-41e2-9197-370581804d09"),
        new("GroupMember.ReadWrite.All", "dbaae8cf-10b5-4b86-a4a1-f871c94c6695"),
        new("Application.Read.All", "9a5d68dd-52b0-4cc2-bd40-abcf44ac3a30"),
        new("Application.ReadWrite.All", "1bfefb4e-e0b5-418b-a88f-73c46d2cc8e9"),
        new("Application.ReadWrite.OwnedBy", "18a4783c-866b-4cc7-a460-3d5e5662c884"),
        new("AppRoleAssignment.ReadWrite.All", "06b708a9-e830-4db3-a914-8e69da51d44f"),
        new("DelegatedPermissionGrant.ReadWrite.All", "8e8e4742-1d95-4f68-9d56-6ee75648c72a"),
        new("RoleManagement.ReadWrite.Directory", "9e3f62cf-ca93-4989-b6ce-bf83c28f9fe8"),
        new("Policy.Read.All", "246dd0d5-5bd0-4def-940b-0421030a5b68"),
        new("Policy.ReadWrite.ConditionalAccess", "01c0a623-fc9b-48e9-b794-0756f8e8f067"),
        new("AdministrativeUnit.ReadWrite.All", "5eb59dd3-1da2-4329-8733-9dabdc435916"),
    ];

    /// <summary>
    /// Microsoft Graph delegated permissions (<c>oauth2PermissionScopes</c> of the Graph service principal); every id
    /// is confirmed against the Microsoft Graph permissions reference.
    /// </summary>
    public static IReadOnlyList<WellKnownPermission> GraphScopes { get; } =
    [
        new("User.Read", "e1fe6dd8-ba31-4d61-89e7-88639da4683d"),
        new("openid", "37f7f235-527c-4136-accd-4a02d197296e"),
        new("profile", "14dad69e-099b-42c9-810b-d002981feec1"),
        new("offline_access", "7427e0e9-2fba-42fe-b0c0-848c9e6a8182"),
        new("email", "64a6cdd6-aab1-4aaf-94b8-3cc8405e90d0"),
        new("User.ReadBasic.All", "b340eb25-3456-403f-be2f-af7a0d370277"),
        new("User.Read.All", "a154be20-db9c-4678-8ab7-66f6cc099a59"),
        new("User.ReadWrite.All", "204e0828-b5ca-4ad8-b9f3-f32a958e7cc4"),
        new("Directory.Read.All", "06da0dbc-49e2-44d2-8312-53f166ab848a"),
        new("Directory.ReadWrite.All", "c5366453-9fb0-48a5-a156-24f0c49a4b84"),
        new("Group.Read.All", "5f8c59db-677d-491f-a6b8-5f174b11ec1d"),
        new("Group.ReadWrite.All", "4e46008b-f24c-477d-8fff-7bb4ec7aafe0"),
        new("Application.Read.All", "c79f8feb-a9db-4090-85f9-90d820caa0eb"),
        new("Application.ReadWrite.All", "bdfbf15f-ee85-4955-8675-146e8e5296b5"),
        new("Mail.Read", "570282fd-fa5c-430d-a7fd-fc8dc98a9dca"),
        new("Mail.Send", "e383f46e-2787-4529-855e-0e479a3ffac0"),
    ];

    /// <summary>Built-in directory roles (verified).</summary>
    public static IReadOnlyList<WellKnownRoleTemplate> RoleTemplates { get; } =
    [
        new("Global Administrator", "62e90394-69f5-4237-9190-012177145e10"),
        new("Global Reader", "f2ef992c-3afb-46b9-b7cf-a126ee74c451"),
        new("Application Administrator", "9b895d92-2cd3-44c7-9d02-a6ac2d5ea5c3"),
        new("Cloud Application Administrator", "158c047a-c907-4556-b7ef-446551a6b5f7"),
        new("Application Developer", "cf1c38e5-3621-4004-a7cb-879624dced7c"),
        new("User Administrator", "fe930be7-5e62-47db-91af-98c3a49a38b1"),
        new("Groups Administrator", "fdd7a751-b60b-444a-984c-02652fe8fa1c"),
        new("Conditional Access Administrator", "b1be1c3e-b65d-4f19-8427-f6fa0d97feb9"),
        new("Privileged Role Administrator", "e8611ab8-c189-46e8-94e1-60213ab1f814"),
        new("Security Administrator", "194ae4cb-b126-40b2-bd5b-6091b380977d"),
        new("Security Reader", "5d6b6bb7-de71-4623-b4af-96380a352509"),
        new("Privileged Authentication Administrator", "7be44c8a-adaf-4e2a-84d6-ab2649e08a13"),
        new("Authentication Administrator", "c4e39bd9-1100-46d3-8c65-fb160da0071f"),
        new("Helpdesk Administrator", "729827e3-9c14-49f7-bb1b-9608f156bbb8"),
        new("Directory Readers", "88d8e3e3-8f55-4a1e-953a-9b9898b8876b"),
        new("Exchange Administrator", "29232cdf-9323-42fd-ade2-1d097af3e4de"),
        new("SharePoint Administrator", "f28a1f50-f6e7-4571-818b-6a12f2af6b6c"),
        new("Teams Administrator", "69091246-20e8-4a56-aa4d-066075b2a7a8"),
        new("Intune Administrator", "3a2c62db-5318-420d-8d74-23affee5d9d5"),
        new("Guest Inviter", "95e79109-95c0-4d8e-aee3-d01accf2d47b"),
        new("Identity Governance Administrator", "45d8d3c5-c802-45c6-b32a-1d70b5e1e86e"),
        new("Authentication Policy Administrator", "0526716b-113d-4c15-b2c8-68e3c22b9f80"),
    ];

    /// <summary>The built-in authentication strengths a Conditional Access policy may reference (never managed as resources).</summary>
    public static IReadOnlyList<WellKnownAuthenticationStrength> AuthenticationStrengths { get; } =
    [
        new("MFA", "Multifactor authentication", "00000000-0000-0000-0000-000000000002"),
        new("Passwordless", "Passwordless MFA", "00000000-0000-0000-0000-000000000003"),
        new("PhishingResistant", "Phishing-resistant MFA", "00000000-0000-0000-0000-000000000004"),
    ];

    /// <summary>The special values Conditional Access accepts in place of object ids.</summary>
    public static class ConditionalAccess
    {
        /// <summary>Accepted in <c>conditions.users</c> and <c>conditions.applications</c> include/exclude lists.</summary>
        public static IReadOnlyList<string> PrincipalAndApplicationValues { get; } = ["All", "None", "GuestsOrExternalUsers", "Office365", "MicrosoftAdminPortals"];

        /// <summary>Accepted in <c>conditions.locations</c> include/exclude lists.</summary>
        public static IReadOnlyList<string> LocationValues { get; } = ["All", "AllTrusted"];
    }

    private static readonly Dictionary<string, string> AppsByName = Apps.ToDictionary(a => a.Name, a => a.AppId, StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> AppRolesByValue = GraphAppRoles.ToDictionary(p => p.Value, p => p.Id, StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> ScopesByValue = GraphScopes.ToDictionary(p => p.Value, p => p.Id, StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> RolesByDisplayName = RoleTemplates.ToDictionary(r => r.DisplayName, r => r.Id, StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> StrengthsByName = AuthenticationStrengths
        .SelectMany(s => new[] { (Key: s.Name, s.Id), (Key: s.DisplayName, s.Id) })
        .ToDictionary(p => p.Key, p => p.Id, StringComparer.OrdinalIgnoreCase);

    /// <summary>The names <c>wellKnownApp()</c> accepts, in table order (for messages).</summary>
    public static IReadOnlyList<string> AppNames { get; } = Apps.Select(a => a.Name).ToList();

    public static bool TryGetAppId(string name, out string appId)
    {
        ArgumentNullException.ThrowIfNull(name);
        return AppsByName.TryGetValue(name.Trim(), out appId!);
    }

    /// <summary>The id of a Microsoft Graph application permission by its <c>value</c> (<c>User.Read.All</c>).</summary>
    public static bool TryGetGraphAppRoleId(string value, out string id)
    {
        ArgumentNullException.ThrowIfNull(value);
        return AppRolesByValue.TryGetValue(value.Trim(), out id!);
    }

    /// <summary>The id of a Microsoft Graph delegated permission by its <c>value</c> (<c>User.Read</c>).</summary>
    public static bool TryGetGraphScopeId(string value, out string id)
    {
        ArgumentNullException.ThrowIfNull(value);
        return ScopesByValue.TryGetValue(value.Trim(), out id!);
    }

    /// <summary>The role template id of a built-in directory role by display name (<c>Global Reader</c>).</summary>
    public static bool TryGetRoleTemplateId(string displayName, out string id)
    {
        ArgumentNullException.ThrowIfNull(displayName);
        return RolesByDisplayName.TryGetValue(displayName.Trim(), out id!);
    }

    /// <summary>The id of a built-in authentication strength by short name (<c>MFA</c>) or display name.</summary>
    public static bool TryGetAuthenticationStrengthId(string nameOrDisplayName, out string id)
    {
        ArgumentNullException.ThrowIfNull(nameOrDisplayName);
        return StrengthsByName.TryGetValue(nameOrDisplayName.Trim(), out id!);
    }

    /// <summary>True when <paramref name="appId"/> is the Microsoft Graph resource application.</summary>
    public static bool IsMicrosoftGraph(string appId)
    {
        return Guid.TryParse(appId, out var guid) && guid == Guid.Parse(MicrosoftGraphAppId);
    }
}
