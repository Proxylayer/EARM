using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace EArm.Core;

/// <summary>
/// The ARM-style identity of a resource: full type plus one name segment per type level.
/// <see cref="Id"/> renders as <c>/providers/Microsoft.Entra/&lt;type&gt;/&lt;name&gt;[/&lt;childType&gt;/&lt;childName&gt;]</c>.
/// Types compare case-insensitively (ARM rule), names case-sensitively.
/// </summary>
/// <remarks>
/// Type segments after the namespace are matched to name segments from the right: a type with more segments than
/// names (for example <c>Microsoft.Entra/conditionalAccess/policies</c> with one name) emits its leading segments
/// unpaired: <c>/providers/Microsoft.Entra/conditionalAccess/policies/CA-1</c>.
/// </remarks>
public sealed class ResourceIdentifier : IEquatable<ResourceIdentifier>
{
    public const string ProviderNamespace = "Microsoft.Entra";
    public const string ProvidersPrefix = "/providers/";

    private readonly string[] _nameSegments;
    private string? _id;

    public ResourceIdentifier(string type, IReadOnlyList<string> nameSegments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentNullException.ThrowIfNull(nameSegments);
        if (nameSegments.Count == 0)
        {
            throw new ArgumentException("A resource needs at least one name segment.", nameof(nameSegments));
        }

        Type = NormalizeType(type);
        TypeSegments = Type.Split('/').Skip(1).ToArray();
        if (TypeSegments.Count == 0)
        {
            throw new ArgumentException($"'{type}' is not a resource type: expected '{ProviderNamespace}/<type>'.", nameof(type));
        }

        if (nameSegments.Count > TypeSegments.Count)
        {
            throw new ArgumentException($"Type '{Type}' has {TypeSegments.Count} level(s) but {nameSegments.Count} name segments were supplied.", nameof(nameSegments));
        }

        _nameSegments = [.. nameSegments];
        foreach (var segment in _nameSegments)
        {
            if (string.IsNullOrEmpty(segment))
            {
                throw new ArgumentException("Name segments must not be empty.", nameof(nameSegments));
            }
        }
    }

    /// <summary>Full type, for example <c>Microsoft.Entra/applications/federatedIdentityCredentials</c>.</summary>
    public string Type { get; }

    /// <summary>The type segments after the provider namespace.</summary>
    public IReadOnlyList<string> TypeSegments { get; }

    /// <summary>The last type segment.</summary>
    public string TypeLeaf => TypeSegments[^1];

    public IReadOnlyList<string> NameSegments => _nameSegments;

    /// <summary>The ARM-style name: segments joined with <c>/</c>.</summary>
    public string Name => string.Join('/', _nameSegments);

    /// <summary>The last name segment (the resource's own name).</summary>
    public string NameLeaf => _nameSegments[^1];

    /// <summary>The tenant-scope ARM-style resource id.</summary>
    public string Id => _id ??= Render();

    /// <summary>The parent identity, or <c>null</c> for a top-level resource.</summary>
    public ResourceIdentifier? Parent
    {
        get
        {
            if (_nameSegments.Length < 2)
            {
                return null;
            }

            var parentTypeSegments = TypeSegments.Take(TypeSegments.Count - 1);
            return new ResourceIdentifier(ProviderNamespace + "/" + string.Join('/', parentTypeSegments), _nameSegments[..^1]);
        }
    }

    /// <summary>Creates an identity from a full type and an ARM-style name (<c>parent/child</c> segments).</summary>
    public static ResourceIdentifier Create(string type, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return new ResourceIdentifier(type, name.Split('/'));
    }

    public static bool TryCreate(string? type, string? name, out ResourceIdentifier? identifier)
    {
        identifier = null;
        if (string.IsNullOrWhiteSpace(type) || string.IsNullOrEmpty(name))
        {
            return false;
        }

        try
        {
            identifier = Create(type, name);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Canonicalises the namespace casing and trims separators; the rest of the type keeps the author's casing
    /// (the catalog supplies canonical casing for known types).
    /// </summary>
    public static string NormalizeType(string type)
    {
        ArgumentNullException.ThrowIfNull(type);
        string trimmed = type.Trim().Trim('/');
        int slash = trimmed.IndexOf('/');
        string ns = slash < 0 ? trimmed : trimmed[..slash];
        if (ns.Equals(ProviderNamespace, StringComparison.OrdinalIgnoreCase))
        {
            return slash < 0 ? ProviderNamespace : ProviderNamespace + trimmed[slash..];
        }

        return trimmed;
    }

    /// <summary>True when the two type strings denote the same type (case-insensitive).</summary>
    public static bool TypeEquals(string? left, string? right)
    {
        if (left is null || right is null)
        {
            return false;
        }

        return NormalizeType(left).Equals(NormalizeType(right), StringComparison.OrdinalIgnoreCase);
    }

    private string Render()
    {
        var sb = new StringBuilder(ProvidersPrefix).Append(ProviderNamespace);
        int unpaired = TypeSegments.Count - _nameSegments.Length;
        for (int i = 0; i < TypeSegments.Count; i++)
        {
            sb.Append('/').Append(TypeSegments[i]);
            if (i >= unpaired)
            {
                sb.Append('/').Append(_nameSegments[i - unpaired]);
            }
        }

        return sb.ToString();
    }

    public bool Equals(ResourceIdentifier? other)
    {
        if (other is null)
        {
            return false;
        }

        if (!Type.Equals(other.Type, StringComparison.OrdinalIgnoreCase) || _nameSegments.Length != other._nameSegments.Length)
        {
            return false;
        }

        for (int i = 0; i < _nameSegments.Length; i++)
        {
            if (!string.Equals(_nameSegments[i], other._nameSegments[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj)
    {
        return Equals(obj as ResourceIdentifier);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Type, StringComparer.OrdinalIgnoreCase);
        foreach (var segment in _nameSegments)
        {
            hash.Add(segment, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    public override string ToString()
    {
        return Id;
    }
}
