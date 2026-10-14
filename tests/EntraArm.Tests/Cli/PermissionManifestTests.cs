using System;
using System.Collections.Generic;
using System.Linq;
using EntraArm.Cli.Commands;
using EntraArm.Core;
using EntraArm.Core.Types;
using EntraArm.Providers;
using Xunit;

namespace EntraArm.Tests.Cli;

/// <summary>The pure parts of <see cref="PermissionManifest"/>: mode mapping, read variants, edge tables and <c>Build</c> over no instances.</summary>
public sealed class PermissionManifestTests
{
    private static PermissionReport BuildEmpty(PermissionMode mode, IEnumerable<string>? strings = null, IEnumerable<(string Parameter, KeyVaultReference Reference)>? vaults = null, int ignored = 0)
    {
        var registry = ProviderDiscovery.CreateRegistry(ResourceTypeCatalog.Default);
        return PermissionManifest.Build("t.entra.json", mode, [], registry, strings ?? [], vaults ?? [], ignored);
    }

    // ---- ForMode ----

    [Theory]
    [InlineData("Application.ReadWrite.OwnedBy", false, "Application.ReadWrite.OwnedBy")]
    [InlineData("Application.ReadWrite.OwnedBy", true, "Application.ReadWrite.All")]
    [InlineData("application.readwrite.ownedby", true, "Application.ReadWrite.All")]
    [InlineData("Application.ReadWrite.All", true, "Application.ReadWrite.All")]
    [InlineData("Group.ReadWrite.All", false, "Group.ReadWrite.All")]
    [InlineData("Group.ReadWrite.All", true, "Group.ReadWrite.All")]
    public void ForMode_OnlyOwnedByChanges_AndOnlyInDelegatedMode(string permission, bool delegated, string expected)
    {
        Assert.Equal(expected, PermissionManifest.ForMode(permission, delegated ? PermissionMode.Delegated : PermissionMode.App));
    }

    // ---- ReadVariant ----

    [Theory]
    [InlineData("Application.ReadWrite.OwnedBy", "Application.Read.All")]
    [InlineData("Application.ReadWrite.All", "Application.Read.All")]
    [InlineData("Group.ReadWrite.All", "Group.Read.All")]
    [InlineData("User.ReadWrite.All", "User.Read.All")]
    [InlineData("RoleManagement.ReadWrite.Directory", "RoleManagement.Read.Directory")]
    [InlineData("Policy.ReadWrite.ConditionalAccess", "Policy.Read.ConditionalAccess")]
    [InlineData("Organization.Read.All", "Organization.Read.All")]
    [InlineData("Policy.Read.All", "Policy.Read.All")]
    public void ReadVariant_TurnsAReadWritePermissionIntoItsReadCounterpart(string permission, string expected)
    {
        Assert.Equal(expected, PermissionManifest.ReadVariant(permission));
    }

    // ---- edge tables ----

    [Theory]
    [InlineData(RelationshipKind.GroupMembership, new[] { "GroupMember.ReadWrite.All" })]
    [InlineData(RelationshipKind.Ownership, new string[0])]
    [InlineData(RelationshipKind.AdministrativeUnitMembership, new[] { "AdministrativeUnit.ReadWrite.All" })]
    [InlineData(RelationshipKind.Manager, new[] { "User.ReadWrite.All" })]
    [InlineData(RelationshipKind.AppRoleAssignment, new[] { "AppRoleAssignment.ReadWrite.All", "Application.Read.All" })]
    [InlineData(RelationshipKind.DirectoryRoleAssignment, new[] { "RoleManagement.ReadWrite.Directory" })]
    [InlineData(RelationshipKind.License, new[] { "Organization.Read.All" })]
    public void EdgeApplicationPermissions_MatchTheDocumentedTable(RelationshipKind kind, string[] expected)
    {
        Assert.Equal(expected, PermissionManifest.EdgeApplicationPermissions(kind));
    }

    [Theory]
    [InlineData(RelationshipKind.GroupMembership, new[] { "Groups Administrator" })]
    [InlineData(RelationshipKind.Ownership, new string[0])]
    [InlineData(RelationshipKind.AdministrativeUnitMembership, new[] { "Privileged Role Administrator" })]
    [InlineData(RelationshipKind.Manager, new[] { "User Administrator" })]
    [InlineData(RelationshipKind.AppRoleAssignment, new[] { "Cloud Application Administrator", "Application Administrator" })]
    [InlineData(RelationshipKind.DirectoryRoleAssignment, new[] { "Privileged Role Administrator" })]
    [InlineData(RelationshipKind.License, new[] { "License Administrator" })]
    public void EdgeRoles_MatchTheDocumentedTable(RelationshipKind kind, string[] expected)
    {
        Assert.Equal(expected, PermissionManifest.EdgeRoles(kind));
    }

    [Fact]
    public void EveryRelationshipKind_HasAnEdgeTableRow_OrIsDeliberatelyEmpty()
    {
        foreach (var kind in Enum.GetValues<RelationshipKind>())
        {
            var permissions = PermissionManifest.EdgeApplicationPermissions(kind);
            var roles = PermissionManifest.EdgeRoles(kind);

            // Ownership is the only kind whose permission is the target type's own (docs/permissions.md, Edge kinds).
            Assert.Equal(kind == RelationshipKind.Ownership, permissions.Count == 0);
            Assert.Equal(kind == RelationshipKind.Ownership, roles.Count == 0);
        }
    }

    // ---- Build over no instances ----

    [Fact]
    public void Build_WithNoResources_AppMode_ListsOnlyThePreflightRead()
    {
        var report = BuildEmpty(PermissionMode.App);

        var entry = Assert.Single(report.Permissions);
        Assert.Equal(PermissionManifest.Preflight, entry.Name);
        Assert.Equal(PermissionKind.GraphApplication, entry.Kind);
        Assert.Equal(["(every deployment)"], entry.Resources);
        Assert.Equal(["preflight: GET /organization"], entry.Reasons);
        Assert.Empty(report.Roles);
        Assert.Equal(0, report.ResourceCount);
        Assert.Equal("t.entra.json", report.Template);
    }

    [Fact]
    public void Build_WithNoResources_DelegatedMode_ListsNothing()
    {
        var report = BuildEmpty(PermissionMode.Delegated);

        Assert.Empty(report.Permissions);
        Assert.Empty(report.Roles);
        Assert.Contains(report.Notes, n => n.Contains("alternatives", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_CountsConditionFalseResourcesInANote_NotInTheResourceCount()
    {
        var withIgnored = BuildEmpty(PermissionMode.App, ignored: 3);
        var without = BuildEmpty(PermissionMode.App);

        Assert.Contains("3 resource(s) with a condition that evaluates to false offline are not included.", withIgnored.Notes);
        Assert.DoesNotContain(without.Notes, n => n.Contains("condition that evaluates to false", StringComparison.Ordinal));
        Assert.Equal(0, withIgnored.ResourceCount);
    }

    [Fact]
    public void Build_KeyVaultReferences_AreAzureRbacEntries_SortedByParameter_AndNeverGraph()
    {
        var vaults = new (string, KeyVaultReference)[]
        {
            ("zeta", new KeyVaultReference("/subscriptions/s/resourceGroups/rg/providers/Microsoft.KeyVault/vaults/vault-z", "secret-z")),
            ("alpha", new KeyVaultReference("/subscriptions/s/resourceGroups/rg/providers/Microsoft.KeyVault/vaults/vault-a", "secret-a")),
        };

        var report = BuildEmpty(PermissionMode.Delegated, vaults: vaults);

        Assert.Equal(["parameter 'alpha'", "parameter 'zeta'"], report.Permissions.SelectMany(p => p.Resources).ToList());
        Assert.All(report.Permissions, p => Assert.Equal(PermissionKind.AzureRbac, p.Kind));
        var first = report.Permissions.Single(p => p.Resources.Contains("parameter 'alpha'"));
        Assert.Equal("Key Vault secret read ('Key Vault Secrets User' or the get-secret access policy) on vault 'vault-a'", first.Name);
        Assert.Equal(["az keyvault secret show --vault-name vault-a --name secret-a"], first.Reasons);
    }

    // ---- expression lookups ----

    [Fact]
    public void Build_LookupFunctions_AddTheirReadPermissions_CaseInsensitively()
    {
        string[] strings =
        [
            "[APPROLE('00000003-0000-0000-c000-000000000000', 'User.Read.All')]",
            "[PermissionScope('00000003-0000-0000-c000-000000000000', 'User.Read')]",
            "[roletemplate('Global Reader')]",
            "[Tenant().displayName]",
        ];

        var report = BuildEmpty(PermissionMode.Delegated, strings);

        var names = report.Permissions.Select(p => p.Name).ToList();
        Assert.Equal(["Application.Read.All", "Organization.Read.All", "RoleManagement.Read.Directory"], names);
        var application = report.Permissions.Single(p => p.Name == "Application.Read.All");
        Assert.Equal(["lookup: appRole()", "lookup: permissionScope()"], application.Reasons);
        Assert.Equal(["(expression lookups)"], application.Resources);
        Assert.All(report.Permissions, p => Assert.Equal(PermissionKind.GraphDelegated, p.Kind));
    }

    [Fact]
    public void Build_ReferenceLookup_NeedsAnApiVersionLiteral_AndAKnownEntraTypeLiteral()
    {
        // reference(resourceId(<type>, <name>), <apiVersion>) reads an existing object; reference('name') reads a template resource.
        var existing = BuildEmpty(PermissionMode.Delegated, ["[reference(resourceId('Microsoft.Entra/users', 'a@contoso.com'), '2026-10-01').id]"]);
        var templateOnly = BuildEmpty(PermissionMode.Delegated, ["[reference('contoso-admins').id]"]);
        var noApiVersion = BuildEmpty(PermissionMode.Delegated, ["[reference(resourceId('Microsoft.Entra/users', 'a@contoso.com')).id]"]);
        var unknownType = BuildEmpty(PermissionMode.Delegated, ["[reference(resourceId('Microsoft.Entra/spaceships', 'x'), '2026-10-01').id]"]);

        var entry = Assert.Single(existing.Permissions);
        Assert.Equal("User.Read.All", entry.Name);
        Assert.Equal(["reference(Microsoft.Entra/users, apiVersion)"], entry.Resources);
        Assert.Equal(["lookup: existing resource"], entry.Reasons);
        Assert.Empty(templateOnly.Permissions);
        Assert.Empty(noApiVersion.Permissions);
        Assert.Empty(unknownType.Permissions);
    }

    [Fact]
    public void Build_ReferenceLookup_OfAnApplication_NeverListsOwnedBy()
    {
        var report = BuildEmpty(PermissionMode.App, ["[reference(resourceId('Microsoft.Entra/applications', 'x'), '2026-10-01').appId]"]);

        Assert.Contains(report.Permissions, p => p.Name == "Application.Read.All" && p.Reasons.Contains("lookup: existing resource"));
        Assert.DoesNotContain(report.Permissions, p => p.Name.Contains("OwnedBy", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_NullArguments_AreRejected()
    {
        var registry = ProviderDiscovery.CreateRegistry(ResourceTypeCatalog.Default);

        Assert.Throws<ArgumentNullException>(() => PermissionManifest.Build(null!, PermissionMode.App, [], registry, [], [], 0));
        Assert.Throws<ArgumentNullException>(() => PermissionManifest.Build("t", PermissionMode.App, null!, registry, [], [], 0));
        Assert.Throws<ArgumentNullException>(() => PermissionManifest.Build("t", PermissionMode.App, [], null!, [], [], 0));
        Assert.Throws<ArgumentNullException>(() => PermissionManifest.Build("t", PermissionMode.App, [], registry, null!, [], 0));
        Assert.Throws<ArgumentNullException>(() => PermissionManifest.Build("t", PermissionMode.App, [], registry, [], null!, 0));
    }
}
