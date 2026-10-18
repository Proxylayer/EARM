using System.Linq;
using System.Threading.Tasks;
using EArm.Core;
using EArm.Engine;
using EArm.Engine.Providers;
using EArm.Providers.Tests.Infrastructure;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>
/// "Two declarations, one object" for resource role scopes. A role scope's Graph id is the composite <c>{roleId}_{scopeId}</c> of the catalog's role and
/// scope, so two access packages of one catalog that grant the same role of the same group have role scopes with the same id: they are two objects, not
/// a duplicate (the claim is scoped by the package, <see cref="IResourceProvider.ClaimIdentity"/>). Two declarations of the same grant in one package
/// still are one.
/// </summary>
public sealed class RoleScopeClaimIdentityTests
{
    private const string CatalogName = "Shared Catalog";
    private const string FirstPackage = "First Package";
    private const string SecondPackage = "Second Package";

    private static string Catalog()
    {
        return Templates.Resource(AccessPackageCatalogsProvider.ResourceType, CatalogName, "{ \"description\": \"One catalog, two packages\" }");
    }

    private static string CatalogResource(string groupId)
    {
        return Templates.Resource(CatalogResourcesProvider.ResourceType, AccessPackagesProviderTests.Guid(1), "{ \"catalogId\": \"[reference('" + CatalogName + "').id]\", \"originId\": \"" + groupId + "\" }");
    }

    private static string Package(string name)
    {
        return Templates.Resource(AccessPackagesProvider.ResourceType, name, "{ \"catalogId\": \"[reference('" + CatalogName + "').id]\", \"description\": \"" + name + "\" }");
    }

    private static string RoleScope(string package, string groupId, int label)
    {
        return Templates.Resource(ResourceRoleScopesProvider.ResourceType, AccessPackagesProviderTests.Guid(label), "{ \"accessPackageId\": \"[reference('" + package + "').id]\", \"resourceOriginId\": \"" + groupId + "\", \"role\": \"Member\" }");
    }

    private static string TwoPackages(string groupId)
    {
        return string.Join(", ", Catalog(), CatalogResource(groupId), Package(FirstPackage), Package(SecondPackage), RoleScope(FirstPackage, groupId, 2), RoleScope(SecondPackage, groupId, 3));
    }

    private static bool IsDuplicate(Run run)
    {
        return run.Plan.Diagnostics.Any(d => d.Code == EngineDiagnosticCodes.DuplicateGraphObject)
            || (run.Result is { } result && (result.Diagnostics.Any(d => d.Code == EngineDiagnosticCodes.DuplicateGraphObject)
                || result.Operations.Any(o => o.ErrorCode == EngineDiagnosticCodes.DuplicateGraphObject)));
    }

    [Fact]
    public void TheClaimIdentity_IsTypeScoped_AndARoleScopeIsAlsoPackageScoped()
    {
        var host = new ProviderHost();
        var roleScopes = host.Provider(ResourceRoleScopesProvider.ResourceType);
        var packages = host.Provider(AccessPackagesProvider.ResourceType);
        ResourceKey RoleScopeKey(string package) => new()
        {
            Type = ResourceRoleScopesProvider.ResourceType,
            Values = new System.Collections.Generic.Dictionary<string, string> { ["accessPackageId"] = package, ["resourceOriginId"] = "g", ["role"] = "Member" },
        };

        Assert.Equal(ResourceRoleScopesProvider.ResourceType + "/p1/r_s", roleScopes.ClaimIdentity(RoleScopeKey("p1"), "r_s"));
        Assert.NotEqual(roleScopes.ClaimIdentity(RoleScopeKey("p1"), "r_s"), roleScopes.ClaimIdentity(RoleScopeKey("p2"), "r_s"));
        Assert.Equal(AccessPackagesProvider.ResourceType + "/x", packages.ClaimIdentity(new ResourceKey { Type = AccessPackagesProvider.ResourceType }, "x"));
    }

    [Fact]
    public async Task TheSameGrant_InTwoPackagesOfOneCatalog_IsCreatedTwice_NotADuplicate()
    {
        var t = AccessPackagesProviderTests.NewTenant();
        var document = t.Host.Resources(TwoPackages(t.GroupId));

        var run = await t.Host.CreateAsync(document);

        Check.Succeeded(run);
        Assert.False(IsDuplicate(run), run.Errors);
        var scopes = t.Host.Directory.EntitlementManagement.RoleScopes;
        Assert.Equal(2, scopes.Count);
        Assert.Equal(scopes[0]["id"]!.GetValue<string>(), scopes[1]["id"]!.GetValue<string>());
        Assert.Equal(run.Op(AccessPackagesProviderTests.Guid(2)).GraphId, run.Op(AccessPackagesProviderTests.Guid(3)).GraphId);
    }

    [Fact]
    public async Task TheSameGrant_InTwoPackagesOfOneCatalog_IsTwoNoChangeRows_InWhatIfAndOnARerun()
    {
        var t = AccessPackagesProviderTests.NewTenant();
        var document = t.Host.Resources(TwoPackages(t.GroupId));
        Check.Succeeded(await t.Host.CreateAsync(document));

        var whatIf = await t.Host.WhatIfAsync(document);

        Assert.False(IsDuplicate(whatIf));
        var rows = whatIf.Plan.Changes.Where(c => ResourceIdentifier.TypeEquals(c.ResourceType, ResourceRoleScopesProvider.ResourceType)).ToList();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(ChangeType.NoChange, r.Type));
        Assert.DoesNotContain(whatIf.Plan.Changes, c => c.HasError);
        Check.NoWrites(whatIf);

        var rerun = await t.Host.CreateAsync(document);

        Check.Succeeded(rerun);
        Assert.False(IsDuplicate(rerun), rerun.Errors);
        Assert.Empty(rerun.Writes);
    }

    [Fact]
    public async Task TheSameGrant_DeclaredTwiceInOnePackage_IsStillADuplicate_InWhatIfAndOnApply()
    {
        var t = AccessPackagesProviderTests.NewTenant();
        string once = string.Join(", ", Catalog(), CatalogResource(t.GroupId), Package(FirstPackage), RoleScope(FirstPackage, t.GroupId, 2));
        Check.Succeeded(await t.Host.CreateAsync(t.Host.Resources(once)));
        var twice = t.Host.Resources(once + ", " + RoleScope(FirstPackage, t.GroupId, 3));

        var whatIf = await t.Host.WhatIfAsync(twice);

        Assert.True(IsDuplicate(whatIf));
        Assert.Single(whatIf.Plan.Changes, c => ResourceIdentifier.TypeEquals(c.ResourceType, ResourceRoleScopesProvider.ResourceType) && c.HasError);

        var run = await t.Host.CreateAsync(twice, continueOnError: true);

        Assert.False(run.Succeeded);
        Assert.Single(run.Executed.Operations, o => o.ErrorCode == EngineDiagnosticCodes.DuplicateGraphObject);
        Assert.Empty(run.Writes);
    }

    [Fact]
    public async Task TheSameGrant_DeclaredTwiceInOnePackage_IsCreatedOnce_AndTheSecondIsADuplicate()
    {
        var t = AccessPackagesProviderTests.NewTenant();
        var twice = t.Host.Resources(string.Join(", ", Catalog(), CatalogResource(t.GroupId), Package(FirstPackage), RoleScope(FirstPackage, t.GroupId, 2), RoleScope(FirstPackage, t.GroupId, 3)));

        var run = await t.Host.CreateAsync(twice, continueOnError: true);

        Assert.False(run.Succeeded);
        Assert.Single(run.Executed.Operations, o => o.ErrorCode == EngineDiagnosticCodes.DuplicateGraphObject);
        Assert.Single(t.Host.Directory.EntitlementManagement.RoleScopes);
    }
}
