using System;
using System.Collections.Generic;
using System.Linq;
using EntraArm.Core;

namespace EntraArm.Engine.Providers;

/// <summary>Providers keyed by resource type (case-insensitive, namespace casing canonicalised).</summary>
public sealed class ProviderRegistry
{
    private readonly Dictionary<string, IResourceProvider> _providers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A registry with no providers: every type is unknown until a provider work item registers one.</summary>
    public static ProviderRegistry Empty => new();

    public IReadOnlyCollection<IResourceProvider> Providers => _providers.Values;

    /// <summary>Registered types, sorted.</summary>
    public IReadOnlyList<string> Types => _providers.Keys.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();

    public int Count => _providers.Count;

    /// <summary>Adds a provider; a second provider for the same type replaces the first only when <paramref name="replace"/> is set.</summary>
    public void Register(IResourceProvider provider, bool replace = false)
    {
        ArgumentNullException.ThrowIfNull(provider);
        string type = ResourceIdentifier.NormalizeType(provider.TypeName);
        if (!replace && _providers.ContainsKey(type))
        {
            throw new InvalidOperationException($"A provider for '{type}' is already registered.");
        }

        _providers[type] = provider;
    }

    public bool TryGet(string? type, out IResourceProvider provider)
    {
        provider = null!;
        return type is not null && _providers.TryGetValue(ResourceIdentifier.NormalizeType(type), out provider!);
    }

    public bool Contains(string? type)
    {
        return TryGet(type, out _);
    }

    /// <summary>The provider for a type; throws <see cref="UnknownProviderException"/> when none is registered.</summary>
    public IResourceProvider Get(string type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (TryGet(type, out var provider))
        {
            return provider;
        }

        throw new UnknownProviderException([ResourceIdentifier.NormalizeType(type)], Types);
    }

    /// <summary>The distinct types among <paramref name="types"/> that have no provider, in first-seen order.</summary>
    public IReadOnlyList<string> MissingTypes(IEnumerable<string> types)
    {
        ArgumentNullException.ThrowIfNull(types);
        var missing = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in types)
        {
            string normalized = ResourceIdentifier.NormalizeType(type);
            if (!_providers.ContainsKey(normalized) && seen.Add(normalized))
            {
                missing.Add(normalized);
            }
        }

        return missing;
    }

    /// <summary>Throws <see cref="UnknownProviderException"/> listing every type in <paramref name="types"/> that has no provider.</summary>
    public void RequireAll(IEnumerable<string> types)
    {
        var missing = MissingTypes(types);
        if (missing.Count > 0)
        {
            throw new UnknownProviderException(missing, Types);
        }
    }
}

/// <summary>No provider is registered for one or more resource types the template uses.</summary>
public sealed class UnknownProviderException : Exception
{
    public UnknownProviderException(IReadOnlyList<string> types, IReadOnlyList<string> registeredTypes)
        : base(FormatMessage(types, registeredTypes))
    {
        Types = types;
        RegisteredTypes = registeredTypes;
    }

    /// <summary>The types without a provider.</summary>
    public IReadOnlyList<string> Types { get; }

    public IReadOnlyList<string> RegisteredTypes { get; }

    private static string FormatMessage(IReadOnlyList<string> types, IReadOnlyList<string> registeredTypes)
    {
        ArgumentNullException.ThrowIfNull(types);
        ArgumentNullException.ThrowIfNull(registeredTypes);
        string missing = string.Join(", ", types);
        string registered = registeredTypes.Count == 0 ? "none" : string.Join(", ", registeredTypes);
        string noun = types.Count == 1 ? "type" : "types";
        return $"No resource provider is registered for {noun} {missing}. Registered providers: {registered}.";
    }
}
