using System.Linq;
using System.Threading.Tasks;
using EArm.Core;
using EArm.Engine;
using EArm.Engine.Providers;
using EArm.Providers.Tests.Infrastructure;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>
/// "Two declarations, one object" for catalog resources. Graph keeps one <c>accessPackageResource</c> per origin, so a group added to two catalogs is
/// listed in both under the same id: two catalogs holding the same group are two objects, not a duplicate (the claim is scoped by the catalog,
/// <see cref="IResourceProvider.ClaimIdentity"/>). The same group declared twice in one catalog still is one.
/// </summary>
public sealed class CatalogResourceClaimIdentityTests
{
    private const string FirstCatalog = "First Catalog";
    private const string SecondCatalog = "Second Catalog";

    private static string Catalog(string name)
    {
        return Templates.Resource(AccessPackageCatalogsProvider.ResourceType, name, "{ \"description\": \"" + name + "\" }");
    }

    private static string CatalogResource(string catalog, string groupId, int label)
    {
        return Templates.Resource(CatalogResourcesProvider.ResourceType, AccessPackagesProviderTests.Guid(label), "{ \"catalogId\": \"[reference('" + catalog + "').id]\", \"originId\": \"" + groupId + "\" }");
    }

    private static string TwoCatalogs(string groupId)
    {
        return string.Join(", ", Catalog(FirstCatalog), Catalog(SecondCatalog), CatalogResource(FirstCatalog, groupId, 1), CatalogResource(SecondCatalog, groupId, 2));
    }

    private static bool IsDuplicate(Run run)
    {
        return run.Plan.Diagnostics.Any(d => d.Code == EngineDiagnosticCodes.DuplicateGraphObject)
            || (run.Result is { } result && (result.Diagnostics.Any(d => d.Code == EngineDiagnosticCodes.DuplicateGraphObject)
                || result.Operations.Any(o => o.ErrorCode == EngineDiagnosticCodes.DuplicateGraphObject)));
    }

    [Fact]
    public void TheClaimIdentity_OfACatalogResource_IsCatalogScoped()
    {
        var resources = new ProviderHost().Provider(CatalogResourcesProvider.ResourceType);
        ResourceKey Key(string catalog) => new()
        {
            Type = CatalogResourcesProvider.ResourceType,
            Values = new System.Collections.Generic.Dictionary<string, string> { ["catalogId"] = catalog, ["originId"] = "g" },
        };

        Assert.Equal(CatalogResourcesProvider.ResourceType + "/c1/r", resources.ClaimIdentity(Key("c1"), "r"));
        Assert.NotEqual(resources.ClaimIdentity(Key("c1"), "r"), resources.ClaimIdentity(Key("c2"), "r"));
    }

    [Fact]
    public async Task TheSameGroup_InTwoCatalogs_IsAddedToBoth_NotADuplicate()
    {
        var t = AccessPackagesProviderTests.NewTenant();
        var document = t.Host.Resources(TwoCatalogs(t.GroupId));

        var run = await t.Host.CreateAsync(document);

        Check.Succeeded(run);
        Assert.False(IsDuplicate(run), run.Errors);
        Assert.Equal(run.Op(AccessPackagesProviderTests.Guid(1)).GraphId, run.Op(AccessPackagesProviderTests.Guid(2)).GraphId);
    }

    [Fact]
    public async Task TheSameGroup_InTwoCatalogs_IsTwoNoChangeRows_InWhatIfAndOnARerun()
    {
        var t = AccessPackagesProviderTests.NewTenant();
        var document = t.Host.Resources(TwoCatalogs(t.GroupId));
        Check.Succeeded(await t.Host.CreateAsync(document));

        var whatIf = await t.Host.WhatIfAsync(document);

        Assert.False(IsDuplicate(whatIf));
        var rows = whatIf.Plan.Changes.Where(c => ResourceIdentifier.TypeEquals(c.ResourceType, CatalogResourcesProvider.ResourceType)).ToList();
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
    public async Task TheSameGroup_DeclaredTwiceInOneCatalog_IsStillADuplicate_InWhatIf()
    {
        var t = AccessPackagesProviderTests.NewTenant();
        string once = string.Join(", ", Catalog(FirstCatalog), CatalogResource(FirstCatalog, t.GroupId, 1));
        Check.Succeeded(await t.Host.CreateAsync(t.Host.Resources(once)));
        var twice = t.Host.Resources(once + ", " + CatalogResource(FirstCatalog, t.GroupId, 2));

        var whatIf = await t.Host.WhatIfAsync(twice);

        Assert.True(IsDuplicate(whatIf));
        Assert.Single(whatIf.Plan.Changes, c => ResourceIdentifier.TypeEquals(c.ResourceType, CatalogResourcesProvider.ResourceType) && c.HasError);
    }
}
