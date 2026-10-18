using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using EArm.Core.Types;
using EArm.Engine.Providers;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>
/// The discovery rules of <see cref="ProviderDiscovery"/> that the shipped assembly never violates, checked against small dynamic
/// assemblies of deliberately wrong providers: a provider without the attribute, two providers for one type, an attribute naming a type
/// the catalog does not define, a provider with no usable constructor, and the happy path through each constructor shape.
/// </summary>
public sealed class ProviderDiscoveryErrorTests
{
    private const string GroupsType = "Microsoft.Entra/groups";

    private static readonly BindingFlags AllInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>Emits <c>public class Name : ProviderBase</c> with the requested constructor shapes and attribute type names.</summary>
    private static void DefineProvider(ModuleBuilder module, string name, Type[]? constructorSignature, params string[] attributeTypes)
    {
        var type = module.DefineType("Dyn." + name, TypeAttributes.Public | TypeAttributes.Class, typeof(ProviderBase));

        if (constructorSignature is not null)
        {
            var baseConstructor = typeof(ProviderBase).GetConstructor(AllInstance, null, [typeof(ResourceTypeDefinition), typeof(ResourceTypeCatalog)], null)!;
            var constructor = type.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, constructorSignature);
            var il = constructor.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            if (constructorSignature.SequenceEqual([typeof(ResourceTypeDefinition), typeof(ResourceTypeCatalog)]))
            {
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldarg_2);
            }
            else
            {
                il.Emit(OpCodes.Ldnull);
                il.Emit(OpCodes.Ldnull);
            }

            il.Emit(OpCodes.Call, baseConstructor);
            il.Emit(OpCodes.Ret);
        }

        foreach (var method in typeof(ProviderBase).GetMethods(AllInstance).Where(m => m.IsAbstract))
        {
            var parameters = method.GetParameters().Select(p => p.ParameterType).ToArray();
            var builder = type.DefineMethod(method.Name, MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig, method.ReturnType, parameters);
            var il = builder.GetILGenerator();
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ret);
        }

        foreach (string attributeType in attributeTypes)
        {
            type.SetCustomAttribute(new CustomAttributeBuilder(typeof(EntraProviderAttribute).GetConstructor([typeof(string)])!, [attributeType]));
        }

        type.CreateType();
    }

    private static Assembly Assemble(Action<ModuleBuilder> define)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("DynProviders" + Guid.NewGuid().ToString("N")), AssemblyBuilderAccess.Run);
        var module = assembly.DefineDynamicModule("module");
        define(module);
        return assembly;
    }

    [Fact]
    public void AConcreteProviderWithoutTheAttribute_IsAnError_NotASilentOmission()
    {
        var assembly = Assemble(m => DefineProvider(m, "Unattributed", [typeof(ResourceTypeDefinition), typeof(ResourceTypeCatalog)]));

        var ex = Assert.Throws<InvalidOperationException>(() => ProviderDiscovery.Discover(assembly));

        Assert.Contains("Dyn.Unattributed", ex.Message, StringComparison.Ordinal);
        Assert.Contains("carries no [EntraProvider", ex.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => ProviderDiscovery.CreateRegistry(ResourceTypeCatalog.Default, assembly));
    }

    [Fact]
    public void TwoProvidersForOneType_AreRejected_EvenWhenTheNamesDifferInCase()
    {
        var assembly = Assemble(m =>
        {
            DefineProvider(m, "First", [typeof(ResourceTypeDefinition), typeof(ResourceTypeCatalog)], GroupsType);
            DefineProvider(m, "Second", [typeof(ResourceTypeDefinition), typeof(ResourceTypeCatalog)], GroupsType.ToUpperInvariant());
        });

        var ex = Assert.Throws<InvalidOperationException>(() => ProviderDiscovery.CreateRegistry(ResourceTypeCatalog.Default, assembly));

        Assert.Contains("claimed by both", ex.Message, StringComparison.Ordinal);
        Assert.Contains("a type has exactly one provider", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAttributeNamingAnUnknownType_IsRejected_WithAHintWhereToAddIt()
    {
        var assembly = Assemble(m => DefineProvider(m, "Ghost", [typeof(ResourceTypeDefinition), typeof(ResourceTypeCatalog)], "Microsoft.Entra/doesNotExist"));

        var ex = Assert.Throws<InvalidOperationException>(() => ProviderDiscovery.CreateRegistry(ResourceTypeCatalog.Default, assembly));

        Assert.Contains("catalog defines no such type", ex.Message, StringComparison.Ordinal);
        Assert.Contains("schemas/" + ResourceTypeCatalog.SchemaVersion, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AProviderWithoutAUsableConstructor_IsRejected()
    {
        var assembly = Assemble(m => DefineProvider(m, "NoCtor", [typeof(int)], GroupsType));

        var ex = Assert.Throws<InvalidOperationException>(() => ProviderDiscovery.CreateRegistry(ResourceTypeCatalog.Default, assembly));

        Assert.Contains("has no usable constructor", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AProviderWhoseConstructorThrows_IsWrappedWithTheTypeAndTheCause()
    {
        // Constructed for 'Microsoft.Entra/groups' while its constructor passes null definitions down: the base class rejects it.
        var assembly = Assemble(m => DefineProvider(m, "Throws", [typeof(string)], GroupsType));

        var ex = Assert.Throws<InvalidOperationException>(() => ProviderDiscovery.CreateRegistry(ResourceTypeCatalog.Default, assembly));

        Assert.Contains("has no usable constructor", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AClassWithSeveralAttributes_ContributesOneBindingPerType_SortedByTypeThenClass()
    {
        var assembly = Assemble(m =>
        {
            DefineProvider(m, "Both", [typeof(ResourceTypeDefinition), typeof(ResourceTypeCatalog)], "Microsoft.Entra/users", GroupsType);
            DefineProvider(m, "Another", [typeof(ResourceTypeDefinition), typeof(ResourceTypeCatalog)], "Microsoft.Entra/applications");
        });

        var bindings = ProviderDiscovery.Discover(assembly);

        Assert.Equal(["Microsoft.Entra/applications", GroupsType, "Microsoft.Entra/users"], bindings.Select(b => b.TypeName).ToArray());
        Assert.Equal(["Dyn.Another", "Dyn.Both", "Dyn.Both"], bindings.Select(b => b.ProviderType.FullName).ToArray());
    }

    [Fact]
    public void AnAssemblyWithoutProviders_YieldsAnEmptyRegistry_AndNullArgumentsAreRejected()
    {
        var empty = Assemble(m => m.DefineType("Dyn.Nothing", TypeAttributes.Public).CreateType());

        Assert.Empty(ProviderDiscovery.Discover(empty));
        Assert.Empty(ProviderDiscovery.CreateRegistry(ResourceTypeCatalog.Default, empty).Providers);
        Assert.Throws<ArgumentNullException>(() => ProviderDiscovery.Discover(null!));
        Assert.Throws<ArgumentNullException>(() => ProviderDiscovery.CreateRegistry(null!, empty));
        Assert.Throws<ArgumentNullException>(() => ProviderDiscovery.CreateRegistry(ResourceTypeCatalog.Default, (Assembly)null!));
        Assert.Throws<ArgumentNullException>(() => ProviderDiscovery.CreateRegistry(ResourceTypeCatalog.Default, empty, null!));
        Assert.Throws<ArgumentNullException>(() => ProviderDiscovery.DiscoverEdgeKinds(null!));
        Assert.Throws<ArgumentNullException>(() => ProviderDiscovery.CreateEdgeKinds(null!, empty));
        Assert.Throws<ArgumentNullException>(() => ProviderDiscovery.CreateEdgeKinds(ResourceTypeCatalog.Default, null!));
        Assert.Empty(ProviderDiscovery.DiscoverEdgeKinds(empty));
    }

    [Fact]
    public void ACatalogMissingAType_FailsTheShippedDiscovery_NamingTheProvider()
    {
        var others = ResourceTypeCatalog.Default.Types
            .Where(t => !t.Type.StartsWith(GroupsType, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var catalog = ResourceTypeCatalog.FromDefinitions(others);

        var ex = Assert.Throws<InvalidOperationException>(() => ProviderDiscovery.CreateRegistry(catalog));

        Assert.Contains("GroupsProvider", ex.Message, StringComparison.Ordinal);
        Assert.Contains(GroupsType, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheShippedEdgeKinds_AreTheTwoPartBKinds_AndTheEdgeAttributeRejectsABlankKind()
    {
        var bindings = ProviderDiscovery.DiscoverEdgeKinds(typeof(ProviderDiscovery).Assembly);

        Assert.Equal(
            [EArm.Engine.Edges.EdgeKind.AppRoleAssignment, EArm.Engine.Edges.EdgeKind.DirectoryRole, EArm.Engine.Edges.EdgeKind.GroupLifecyclePolicy],
            bindings.Select(b => b.Kind).Order().ToArray());
        Assert.ThrowsAny<ArgumentException>(() => new EntraProviderAttribute(" "));
        Assert.ThrowsAny<ArgumentException>(() => new EntraProviderAttribute(null!));
    }
}
