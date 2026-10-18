using System;
using System.Linq;
using EArm.Core;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>
/// Review 1D / G-01: the embedded Microsoft Graph permission tables are pinned against the permissions reference
/// (https://learn.microsoft.com/en-us/graph/permissions-reference, read 2026-10-14). A wrong GUID (Application.ReadWrite.All
/// carried <c>1bfefb4e-c769-4b31-8f3a-ccd1d37f1e7f</c>, the real one is <c>1bfefb4e-e0b5-418b-a88f-73c46d2cc8e9</c>) fails the build here.
/// </summary>
public sealed class Review1D_WellKnownIdsTests
{
    /// <summary>Application permission (app role) ids, copied from the "Identifier / Application" rows of the reference.</summary>
    private static readonly (string Name, string Id)[] ApplicationPermissions =
    [
        ("User.Read.All", "df021288-bdef-4463-88db-98f22de89214"),
        ("User.ReadWrite.All", "741f803b-c850-494e-b5df-cde7c675a1ca"),
        ("Directory.Read.All", "7ab1d382-f21e-4acd-a863-ba3e13f7da61"),
        ("Directory.ReadWrite.All", "19dbc75e-c2e2-444c-a770-ec69d8559fc7"),
        ("Group.ReadWrite.All", "62a82d76-70ea-41e2-9197-370581804d09"),
        ("GroupMember.ReadWrite.All", "dbaae8cf-10b5-4b86-a4a1-f871c94c6695"),
        ("Application.Read.All", "9a5d68dd-52b0-4cc2-bd40-abcf44ac3a30"),
        ("Application.ReadWrite.All", "1bfefb4e-e0b5-418b-a88f-73c46d2cc8e9"),
        ("Application.ReadWrite.OwnedBy", "18a4783c-866b-4cc7-a460-3d5e5662c884"),
        ("AppRoleAssignment.ReadWrite.All", "06b708a9-e830-4db3-a914-8e69da51d44f"),
        ("DelegatedPermissionGrant.ReadWrite.All", "8e8e4742-1d95-4f68-9d56-6ee75648c72a"),
        ("RoleManagement.ReadWrite.Directory", "9e3f62cf-ca93-4989-b6ce-bf83c28f9fe8"),
        ("Policy.Read.All", "246dd0d5-5bd0-4def-940b-0421030a5b68"),
        ("Policy.ReadWrite.ConditionalAccess", "01c0a623-fc9b-48e9-b794-0756f8e8f067"),
        ("AdministrativeUnit.ReadWrite.All", "5eb59dd3-1da2-4329-8733-9dabdc435916"),
    ];

    /// <summary>Delegated permission (OAuth2 scope) ids, copied from the "Identifier / Delegated" rows of the reference.</summary>
    private static readonly (string Name, string Id)[] DelegatedPermissions =
    [
        ("User.Read", "e1fe6dd8-ba31-4d61-89e7-88639da4683d"),
        ("openid", "37f7f235-527c-4136-accd-4a02d197296e"),
        ("profile", "14dad69e-099b-42c9-810b-d002981feec1"),
        ("offline_access", "7427e0e9-2fba-42fe-b0c0-848c9e6a8182"),
        ("email", "64a6cdd6-aab1-4aaf-94b8-3cc8405e90d0"),
        ("User.ReadBasic.All", "b340eb25-3456-403f-be2f-af7a0d370277"),
        ("User.Read.All", "a154be20-db9c-4678-8ab7-66f6cc099a59"),
        ("User.ReadWrite.All", "204e0828-b5ca-4ad8-b9f3-f32a958e7cc4"),
        ("Directory.Read.All", "06da0dbc-49e2-44d2-8312-53f166ab848a"),
        ("Directory.ReadWrite.All", "c5366453-9fb0-48a5-a156-24f0c49a4b84"),
        ("Group.Read.All", "5f8c59db-677d-491f-a6b8-5f174b11ec1d"),
        ("Group.ReadWrite.All", "4e46008b-f24c-477d-8fff-7bb4ec7aafe0"),
        ("Application.Read.All", "c79f8feb-a9db-4090-85f9-90d820caa0eb"),
        ("Application.ReadWrite.All", "bdfbf15f-ee85-4955-8675-146e8e5296b5"),
        ("Mail.Read", "570282fd-fa5c-430d-a7fd-fc8dc98a9dca"),
        ("Mail.Send", "e383f46e-2787-4529-855e-0e479a3ffac0"),
    ];

    [Fact]
    public void GraphAppRoles_MatchThePinnedPermissionsReference_ExactlyAndVerified()
    {
        var actual = WellKnownIds.GraphAppRoles.Select(p => (p.Value, p.Id.ToLowerInvariant())).OrderBy(p => p.Value, StringComparer.Ordinal).ToList();
        var pinned = ApplicationPermissions.OrderBy(p => p.Name, StringComparer.Ordinal).ToList();

        Assert.Equal(pinned, actual);
        Assert.All(WellKnownIds.GraphAppRoles, p => Assert.True(p.Verified, p.Value));
    }

    [Fact]
    public void GraphScopes_MatchThePinnedPermissionsReference_ExactlyAndVerified()
    {
        var actual = WellKnownIds.GraphScopes.Select(p => (p.Value, p.Id.ToLowerInvariant())).OrderBy(p => p.Value, StringComparer.Ordinal).ToList();
        var pinned = DelegatedPermissions.OrderBy(p => p.Name, StringComparer.Ordinal).ToList();

        Assert.Equal(pinned, actual);
        Assert.All(WellKnownIds.GraphScopes, p => Assert.True(p.Verified, p.Value));
    }

    [Fact]
    public void ApplicationReadWriteAll_HasTheRealGraphId_AndResolvesByName()
    {
        Assert.True(WellKnownIds.TryGetGraphAppRoleId("application.readwrite.all", out string id));
        Assert.Equal("1bfefb4e-e0b5-418b-a88f-73c46d2cc8e9", id);
        Assert.DoesNotContain(WellKnownIds.GraphAppRoles, p => p.Id.Equals("1bfefb4e-c769-4b31-8f3a-ccd1d37f1e7f", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ApplicationAndDelegatedIdsOfTheSamePermission_AreDifferentKinds()
    {
        // The two tables are separate id spaces: a permission name resolves to its own kind's id.
        Assert.True(WellKnownIds.TryGetGraphAppRoleId("Application.ReadWrite.All", out string role));
        Assert.True(WellKnownIds.TryGetGraphScopeId("Application.ReadWrite.All", out string scope));
        Assert.NotEqual(role, scope);
    }
}
