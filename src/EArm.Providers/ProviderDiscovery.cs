using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EArm.Core;
using EArm.Core.Types;
using EArm.Engine.Edges;
using EArm.Engine.Execution;
using EArm.Engine.Providers;
using EArm.Graph;

namespace EArm.Providers;

/// <summary>One discovered binding: the resource type an <see cref="EntraProviderAttribute"/> names and the class that carries it.</summary>
public sealed record ProviderBinding(string TypeName, Type ProviderType);

/// <summary>One discovered edge-kind binding: the kind an <see cref="EntraEdgeKindAttribute"/> names and the handler class that carries it.</summary>
public sealed record EdgeKindBinding(EdgeKind Kind, Type HandlerType);

/// <summary>
/// Builds the <see cref="ProviderRegistry"/> by reflection: every non-abstract <see cref="IResourceProvider"/> in
/// the providers assembly declares its type with <see cref="EntraProviderAttribute"/> and is constructed from the
/// catalog definition of that type. Adding a provider is therefore a new class, never an edit to a shared file.
/// Mistakes fail loudly at startup: a provider without the attribute, an attribute naming a type the catalog does
/// not define, two providers claiming one type, or a class without a usable constructor.
/// </summary>
public static class ProviderDiscovery
{
    /// <summary>The registry of every provider in this assembly, bound to <paramref name="catalog"/>.</summary>
    public static ProviderRegistry CreateRegistry(ResourceTypeCatalog catalog)
    {
        return CreateRegistry(catalog, typeof(ProviderDiscovery).Assembly);
    }

    /// <summary>
    /// The registry of every provider in this assembly, bound to <paramref name="catalog"/>; the providers that wait for Graph to settle after a write
    /// (a constructor taking a <see cref="ReplicationWait"/>) wait on <paramref name="settle"/>.
    /// </summary>
    public static ProviderRegistry CreateRegistry(ResourceTypeCatalog catalog, ReplicationWait settle)
    {
        return CreateRegistry(catalog, typeof(ProviderDiscovery).Assembly, settle);
    }

    /// <summary>
    /// The registry of an online run: as <see cref="CreateRegistry(ResourceTypeCatalog, ReplicationWait)"/>, and the providers whose calls go to Azure
    /// Resource Manager (<see cref="IResourceManagerProvider"/>, a constructor taking an <see cref="ArmClient"/>) call it through
    /// <paramref name="resourceManager"/>. The other overloads build the registry without one (offline: <c>validate</c>, <c>permissions</c>, the run scopes),
    /// where such a provider still validates and plans offline but fails any Resource Manager call with an <see cref="InvalidOperationException"/>.
    /// </summary>
    public static ProviderRegistry CreateRegistry(ResourceTypeCatalog catalog, ReplicationWait settle, ArmClient resourceManager)
    {
        ArgumentNullException.ThrowIfNull(resourceManager);
        return CreateRegistry(catalog, typeof(ProviderDiscovery).Assembly, settle, resourceManager);
    }

    /// <summary>The registry of every attributed provider in <paramref name="assembly"/>, bound to <paramref name="catalog"/>.</summary>
    public static ProviderRegistry CreateRegistry(ResourceTypeCatalog catalog, Assembly assembly)
    {
        return CreateRegistry(catalog, assembly, ReplicationWait.Default);
    }

    /// <summary>The registry of every attributed provider in <paramref name="assembly"/>, bound to <paramref name="catalog"/>, settling on <paramref name="settle"/>.</summary>
    public static ProviderRegistry CreateRegistry(ResourceTypeCatalog catalog, Assembly assembly, ReplicationWait settle)
    {
        return CreateRegistry(catalog, assembly, settle, null);
    }

    /// <summary>
    /// The registry of every attributed provider in <paramref name="assembly"/>, bound to <paramref name="catalog"/>, settling on <paramref name="settle"/>; the
    /// Resource Manager providers get <paramref name="resourceManager"/> (<c>null</c> offline).
    /// </summary>
    public static ProviderRegistry CreateRegistry(ResourceTypeCatalog catalog, Assembly assembly, ReplicationWait settle, ArmClient? resourceManager)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(settle);

        var registry = new ProviderRegistry();
        var owners = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in Discover(assembly))
        {
            string normalized = ResourceIdentifier.NormalizeType(binding.TypeName);
            if (owners.TryGetValue(normalized, out var other))
            {
                throw new InvalidOperationException($"Resource type '{binding.TypeName}' is claimed by both {other.FullName} and {binding.ProviderType.FullName}; a type has exactly one provider.");
            }

            var definition = catalog.Find(binding.TypeName)
                ?? throw new InvalidOperationException($"{binding.ProviderType.FullName} is attributed [EntraProvider(\"{binding.TypeName}\")] but the catalog defines no such type; add the type schema under schemas/{ResourceTypeCatalog.SchemaVersion}/types/ or fix the attribute.");

            var provider = Instantiate(binding.ProviderType, definition, catalog, settle, resourceManager);
            if (!ResourceIdentifier.TypeEquals(provider.TypeName, definition.Type))
            {
                throw new InvalidOperationException($"{binding.ProviderType.FullName} is attributed for '{binding.TypeName}' but reports TypeName '{provider.TypeName}'.");
            }

            registry.Register(provider);
            owners[normalized] = binding.ProviderType;
        }

        return registry;
    }

    /// <summary>
    /// Every (type, class) pair declared in <paramref name="assembly"/>, sorted by type then class name. A concrete
    /// <see cref="IResourceProvider"/> without the attribute is an error rather than a silent omission.
    /// </summary>
    public static IReadOnlyList<ProviderBinding> Discover(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var bindings = new List<ProviderBinding>();
        foreach (var type in assembly.GetTypes())
        {
            if (type.IsAbstract || type.IsInterface || type.IsGenericTypeDefinition || !typeof(IResourceProvider).IsAssignableFrom(type))
            {
                continue;
            }

            var attributes = type.GetCustomAttributes<EntraProviderAttribute>(inherit: false).ToList();
            if (attributes.Count == 0)
            {
                throw new InvalidOperationException($"{type.FullName} implements IResourceProvider but carries no [EntraProvider(\"Microsoft.Entra/<type>\")] attribute, so it cannot be registered (see docs/providers.md).");
            }

            foreach (var attribute in attributes)
            {
                bindings.Add(new ProviderBinding(attribute.TypeName, type));
            }
        }

        return bindings
            .OrderBy(b => b.TypeName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(b => b.ProviderType.FullName, StringComparer.Ordinal)
            .ToList();
    }

    // ---- edge kinds (part B handlers) ----

    /// <summary>
    /// The edge-kind registry for the edge pass: the engine's part A handlers (<see cref="EdgeKindRegistry.Default"/>)
    /// plus every <see cref="IEdgeKindHandler"/> in this assembly attributed <see cref="EntraEdgeKindAttribute"/>
    /// (part B: app role assignments, directory roles). Wire the result into <c>PlannerOptions.EdgeKinds</c>.
    /// </summary>
    public static EdgeKindRegistry CreateEdgeKinds(ResourceTypeCatalog catalog)
    {
        return CreateEdgeKinds(catalog, typeof(ProviderDiscovery).Assembly);
    }

    /// <summary>The part A registry plus every attributed handler in <paramref name="assembly"/>; a kind claimed twice (or a part A kind) is an error.</summary>
    public static EdgeKindRegistry CreateEdgeKinds(ResourceTypeCatalog catalog, Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(assembly);

        var registry = EdgeKindRegistry.Default;
        var owners = new Dictionary<EdgeKind, Type>();
        foreach (var binding in DiscoverEdgeKinds(assembly))
        {
            if (owners.TryGetValue(binding.Kind, out var other))
            {
                throw new InvalidOperationException($"Edge kind {binding.Kind} is claimed by both {other.FullName} and {binding.HandlerType.FullName}; a kind has exactly one handler.");
            }

            if (registry.Contains(binding.Kind))
            {
                throw new InvalidOperationException($"{binding.HandlerType.FullName} is attributed [EntraEdgeKind({binding.Kind})] but that kind is handled by the engine's part A registry; only part B kinds (AppRoleAssignment, DirectoryRole) are discovered here.");
            }

            var handler = InstantiateEdgeKind(binding.HandlerType, catalog);
            if (handler.Kind != binding.Kind)
            {
                throw new InvalidOperationException($"{binding.HandlerType.FullName} is attributed for {binding.Kind} but reports Kind {handler.Kind}.");
            }

            registry.Register(handler);
            owners[binding.Kind] = binding.HandlerType;
        }

        return registry;
    }

    /// <summary>
    /// Every (kind, class) pair declared in <paramref name="assembly"/>, sorted by kind then class name. A concrete
    /// <see cref="IEdgeKindHandler"/> without the attribute is an error rather than a silent omission.
    /// </summary>
    public static IReadOnlyList<EdgeKindBinding> DiscoverEdgeKinds(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var bindings = new List<EdgeKindBinding>();
        foreach (var type in assembly.GetTypes())
        {
            if (type.IsAbstract || type.IsInterface || type.IsGenericTypeDefinition || !typeof(IEdgeKindHandler).IsAssignableFrom(type))
            {
                continue;
            }

            var attribute = type.GetCustomAttribute<EntraEdgeKindAttribute>(inherit: false)
                ?? throw new InvalidOperationException($"{type.FullName} implements IEdgeKindHandler but carries no [EntraEdgeKind(EdgeKind.<kind>)] attribute, so it cannot be registered (see docs/providers.md).");
            bindings.Add(new EdgeKindBinding(attribute.Kind, type));
        }

        return bindings
            .OrderBy(b => b.Kind)
            .ThenBy(b => b.HandlerType.FullName, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Constructs a handler through <c>(ResourceTypeCatalog)</c> or <c>()</c>.</summary>
    private static IEdgeKindHandler InstantiateEdgeKind(Type handlerType, ResourceTypeCatalog catalog)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        (Type[] Signature, object[] Arguments)[] candidates =
        [
            ([typeof(ResourceTypeCatalog)], [catalog]),
            ([], []),
        ];

        foreach (var (signature, arguments) in candidates)
        {
            var constructor = handlerType.GetConstructor(flags, binder: null, signature, modifiers: null);
            if (constructor is null)
            {
                continue;
            }

            try
            {
                return (IEdgeKindHandler)constructor.Invoke(arguments);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                throw new InvalidOperationException($"{handlerType.FullName} could not be constructed: {ex.InnerException.Message}", ex.InnerException);
            }
        }

        throw new InvalidOperationException($"{handlerType.FullName} has no usable constructor; an edge-kind handler needs (ResourceTypeCatalog) or a parameterless constructor.");
    }

    // ---- construction ----

    /// <summary>
    /// Constructs a provider through the first constructor found among <c>(ResourceTypeDefinition, ResourceTypeCatalog, ArmClient?)</c> (a Resource
    /// Manager provider; the client is <c>null</c> offline), <c>(ResourceTypeDefinition, ResourceTypeCatalog, ReplicationWait)</c>,
    /// <c>(ResourceTypeDefinition, ResourceTypeCatalog)</c>, <c>(ResourceTypeDefinition)</c>, <c>(ResourceTypeCatalog)</c> and <c>()</c>.
    /// </summary>
    private static IResourceProvider Instantiate(Type providerType, ResourceTypeDefinition definition, ResourceTypeCatalog catalog, ReplicationWait settle, ArmClient? resourceManager)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        (Type[] Signature, object?[] Arguments)[] candidates =
        [
            ([typeof(ResourceTypeDefinition), typeof(ResourceTypeCatalog), typeof(ArmClient)], [definition, catalog, resourceManager]),
            ([typeof(ResourceTypeDefinition), typeof(ResourceTypeCatalog), typeof(ReplicationWait)], [definition, catalog, settle]),
            ([typeof(ResourceTypeDefinition), typeof(ResourceTypeCatalog)], [definition, catalog]),
            ([typeof(ResourceTypeDefinition)], [definition]),
            ([typeof(ResourceTypeCatalog)], [catalog]),
            ([], []),
        ];

        foreach (var (signature, arguments) in candidates)
        {
            var constructor = providerType.GetConstructor(flags, binder: null, signature, modifiers: null);
            if (constructor is null)
            {
                continue;
            }

            try
            {
                return (IResourceProvider)constructor.Invoke(arguments);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                throw new InvalidOperationException($"{providerType.FullName} could not be constructed for '{definition.Type}': {ex.InnerException.Message}", ex.InnerException);
            }
        }

        throw new InvalidOperationException($"{providerType.FullName} has no usable constructor; a provider needs (ResourceTypeDefinition, ResourceTypeCatalog, ArmClient), (ResourceTypeDefinition, ResourceTypeCatalog, ReplicationWait), (ResourceTypeDefinition, ResourceTypeCatalog), (ResourceTypeDefinition), (ResourceTypeCatalog) or a parameterless constructor.");
    }
}
