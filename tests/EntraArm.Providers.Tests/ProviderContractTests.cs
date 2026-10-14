using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using EntraArm.Core.Types;
using EntraArm.Engine.Execution;
using EntraArm.Engine.Providers;
using EntraArm.Engine.State;
using EntraArm.Providers.Tests.Infrastructure;
using EntraArm.TestSupport;
using Xunit;

namespace EntraArm.Providers.Tests;

/// <summary>
/// Reflection-driven contract checks over every discovered provider: constructor guards (wrong definition, catalog
/// without the type) and null-argument guards of every lifecycle method. One row per provider type, so a new provider
/// is covered the day it is added.
/// </summary>
public sealed class ProviderContractTests
{
    private static readonly Type[] AllProviders = ProviderDiscovery.Discover(typeof(ProviderDiscovery).Assembly)
        .Select(b => b.ProviderType).Distinct().OrderBy(t => t.Name, StringComparer.Ordinal).ToArray();

    public static TheoryData<Type> ProviderTypes()
    {
        var data = new TheoryData<Type>();
        foreach (var type in AllProviders)
        {
            data.Add(type);
        }

        return data;
    }

    /// <summary>Providers with the standard constructor pair (definition guard + catalog overload); GroupsProvider adjusts the definition instead.</summary>
    public static TheoryData<Type> GuardedProviderTypes()
    {
        var data = new TheoryData<Type>();
        foreach (var type in AllProviders.Where(t => t.GetConstructor([typeof(ResourceTypeCatalog)]) is not null))
        {
            data.Add(type);
        }

        return data;
    }

    private static string TypeOf(Type providerType)
    {
        return ProviderDiscovery.Discover(providerType.Assembly).First(b => b.ProviderType == providerType).TypeName;
    }

    private static ResourceTypeDefinition OtherDefinition(string notThis)
    {
        return ResourceTypeCatalog.Default.Types.First(t => !string.Equals(t.Type, notThis, StringComparison.OrdinalIgnoreCase));
    }

    private static IResourceProvider Create(Type providerType)
    {
        var catalogCtor = providerType.GetConstructor([typeof(ResourceTypeCatalog)]);
        return catalogCtor is not null
            ? (IResourceProvider)catalogCtor.Invoke([ResourceTypeCatalog.Default])
            : (IResourceProvider)providerType.GetConstructor([typeof(ResourceTypeDefinition), typeof(ResourceTypeCatalog)])!.Invoke([ResourceTypeCatalog.Default.Find(TypeOf(providerType)), ResourceTypeCatalog.Default]);
    }

    [Theory]
    [MemberData(nameof(GuardedProviderTypes))]
    public void Constructor_RejectsADefinitionOfAnotherType(Type providerType)
    {
        var wrong = OtherDefinition(TypeOf(providerType));
        var ctor = providerType.GetConstructor([typeof(ResourceTypeDefinition), typeof(ResourceTypeCatalog)]);
        Assert.NotNull(ctor);

        var ex = Assert.Throws<TargetInvocationException>(() => ctor.Invoke([wrong, ResourceTypeCatalog.Default]));

        Assert.IsType<ArgumentException>(ex.InnerException);
        Assert.Contains(wrong.Type, ex.InnerException!.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(ProviderTypes))]
    public void Constructor_AcceptsItsOwnDefinition_AndTheCatalogOverloadFindsIt(Type providerType)
    {
        string type = TypeOf(providerType);
        var definition = ResourceTypeCatalog.Default.Find(type)!;

        var fromDefinition = (IResourceProvider)providerType.GetConstructor([typeof(ResourceTypeDefinition), typeof(ResourceTypeCatalog)])!.Invoke([definition, ResourceTypeCatalog.Default]);
        var created = Create(providerType);

        Assert.Equal(type, fromDefinition.TypeName);
        Assert.Equal(type, created.TypeName);
    }

    [Theory]
    [MemberData(nameof(GuardedProviderTypes))]
    public void CatalogConstructor_ThrowsWhenTheCatalogDoesNotDefineTheType(Type providerType)
    {
        string type = TypeOf(providerType);
        // Child types go with their parent: the catalog rejects an orphaned child.
        var others = ResourceTypeCatalog.Default.Types
            .Where(t => !string.Equals(t.Type, type, StringComparison.OrdinalIgnoreCase) && !t.Type.StartsWith(type + "/", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var catalog = ResourceTypeCatalog.FromDefinitions(others);
        var ctor = providerType.GetConstructor([typeof(ResourceTypeCatalog)])!;

        var ex = Assert.Throws<TargetInvocationException>(() => ctor.Invoke([catalog]));

        Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Contains(type, ex.InnerException!.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(GuardedProviderTypes))]
    public void CatalogConstructor_RejectsANullCatalog(Type providerType)
    {
        var ctor = providerType.GetConstructor([typeof(ResourceTypeCatalog)])!;

        var ex = Assert.Throws<TargetInvocationException>(() => ctor.Invoke([null]));

        Assert.IsType<ArgumentNullException>(ex.InnerException);
    }

    [Theory]
    [MemberData(nameof(ProviderTypes))]
    public async Task LifecycleMethods_RejectNullArguments(Type providerType)
    {
        var provider = Create(providerType);
        var graph = new FakeGraphClient();
        var ct = TestContext.Current.CancellationToken;

        Assert.Throws<ArgumentNullException>(() => provider.ResolveKey(null!));
        Assert.Throws<ArgumentNullException>(() => provider.Plan(null!, null, PlanOptions.Default));
        if (providerType.GetMethod(nameof(IResourceProvider.PrepareAsync))!.DeclaringType == providerType)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(() => provider.PrepareAsync(null!, graph, ct));
        }

        await Assert.ThrowsAsync<ArgumentNullException>(() => provider.ReadAsync(null!, null, graph, ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => provider.CreateAsync(null!, graph, ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => provider.DeleteAsync(null!, graph, ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => provider.UpdateAsync(null!, null!, null!, graph, ct));
    }

    [Theory]
    [MemberData(nameof(ProviderTypes))]
    public async Task LifecycleMethods_RejectANullGraphClient_BeforeTouchingTheirOtherArguments(Type providerType)
    {
        var provider = Create(providerType);
        var ct = TestContext.Current.CancellationToken;

        // Uninitialised stand-ins: only their non-nullness matters, a guard that reads a member first would throw something else.
        var instance = (ResourceInstance)RuntimeHelpers.GetUninitializedObject(typeof(ResourceInstance));
        var existing = (ExistingResource)RuntimeHelpers.GetUninitializedObject(typeof(ExistingResource));
        var change = (ResourceChange)RuntimeHelpers.GetUninitializedObject(typeof(ResourceChange));
        var entry = (StateEntry)RuntimeHelpers.GetUninitializedObject(typeof(StateEntry));
        var key = (ResourceKey)RuntimeHelpers.GetUninitializedObject(typeof(ResourceKey));

        if (providerType.GetMethod(nameof(IResourceProvider.PrepareAsync))!.DeclaringType == providerType)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(() => provider.PrepareAsync(instance, null!, ct));
        }

        await Assert.ThrowsAsync<ArgumentNullException>(() => provider.ReadAsync(key, null, null!, ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => provider.CreateAsync(instance, null!, ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => provider.DeleteAsync(entry, null!, ct));
        if (providerType != typeof(RoleAssignmentsProvider) && providerType != typeof(AppRoleAssignmentsProvider))
        {
            // These two never update in place: UpdateAsync only guards the instance and then throws.
            await Assert.ThrowsAsync<ArgumentNullException>(() => provider.UpdateAsync(instance, existing, change, null!, ct));
        }
    }

    [Theory]
    [MemberData(nameof(ProviderTypes))]
    public void Plan_RejectsNullOptions(Type providerType)
    {
        var provider = Create(providerType);
        var instance = (ResourceInstance)RuntimeHelpers.GetUninitializedObject(typeof(ResourceInstance));

        Assert.Throws<ArgumentNullException>(() => provider.Plan(instance, null, null!));
    }

    [Theory]
    [MemberData(nameof(ProviderTypes))]
    public void RequiredPermissions_AreNonEmpty_AndStableWithoutAnInstance(Type providerType)
    {
        var provider = Create(providerType);

        var first = provider.RequiredPermissions(null);
        var second = provider.RequiredPermissions(null);

        Assert.NotEmpty(first);
        Assert.Equal(first, second);
        Assert.All(first, p => Assert.False(string.IsNullOrWhiteSpace(p)));
    }

    [Theory]
    [MemberData(nameof(ProviderTypes))]
    public void ImmutableProperties_AndApiVersions_AreExposed(Type providerType)
    {
        var provider = Create(providerType);

        Assert.NotNull(provider.ImmutableProperties);
        Assert.NotNull(provider.ApiVersions);
        Assert.NotEmpty(provider.ApiVersions);
    }

    [Fact]
    public void GroupsProvider_RejectsANullDefinition_AndLoosensTheOverlayForCreate()
    {
        Assert.Throws<ArgumentNullException>(() => new GroupsProvider(null!, ResourceTypeCatalog.Default));

        var provider = new GroupsProvider(ResourceTypeCatalog.Default.Find("Microsoft.Entra/groups")!, ResourceTypeCatalog.Default);

        Assert.Equal(["displayName"], provider.Definition.RequiredOnCreate);
        Assert.DoesNotContain(provider.Definition.Immutable, p => p.ToString() is "groupTypes" or "visibility");
    }

    [Fact]
    public void UsersProvider_PlanOptionsSetter_RejectsNull_AndKeepsTheAssignedValue()
    {
        var provider = new UsersProvider(ResourceTypeCatalog.Default);

        Assert.Throws<ArgumentNullException>(() => provider.PlanOptions = null!);

        var options = PlanOptions.Default with { };
        provider.PlanOptions = options;
        Assert.Same(options, provider.PlanOptions);
    }

    [Fact]
    public void DiscoveredProviderSet_CoversEveryCatalogType_ExactlyOnce()
    {
        var bindings = ProviderDiscovery.Discover(typeof(ProviderDiscovery).Assembly).ToList();

        var duplicated = bindings.GroupBy(b => b.TypeName, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();

        Assert.Empty(duplicated);
        Assert.Equal(ResourceTypeCatalog.Default.Types.Count, bindings.Select(b => b.TypeName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void EveryProviderHostRegistryEntry_ReportsItsOwnTypeName()
    {
        var host = new ProviderHost();

        IEnumerable<IResourceProvider> providers = host.Registry.Providers;

        Assert.All(providers, p => Assert.Same(p, host.Provider(p.TypeName)));
    }
}
