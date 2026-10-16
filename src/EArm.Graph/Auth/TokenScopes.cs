using System;
using System.Collections.Generic;
using System.Linq;

namespace EArm.Graph.Auth;

/// <summary>
/// The scopes of ONE token request: a single resource (<c>https://graph.microsoft.com</c>, <c>https://vault.azure.net</c>) and either its
/// <c>.default</c> scope or an explicit, ordered, distinct list of its scopes, always fully qualified (<c>https://graph.microsoft.com/User.Read</c>).
/// Follows the Microsoft Entra rules for a token request: one resource per request, and <c>.default</c> is never combined with explicit scopes.
/// Two sets with the same scopes in any order or case are equal and share one <see cref="CacheKey"/>.
/// </summary>
public sealed class TokenScopes : IEquatable<TokenScopes>
{
    private const string DefaultName = ".default";

    /// <summary>OpenID Connect scopes MSAL adds to every request itself; they belong to no resource, so a set never lists them.</summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase) { "openid", "profile", "offline_access" };

    private TokenScopes(string resource, IReadOnlyList<string> names, bool isDefault)
    {
        Resource = resource;
        Names = names;
        IsDefault = isDefault;
        Scopes = names.Select(name => resource + "/" + name).ToArray();
        CacheKey = string.Join(' ', Scopes.Select(scope => scope.ToLowerInvariant()).Order(StringComparer.Ordinal));
    }

    /// <summary>The resource origin every scope belongs to, without a trailing slash (<c>https://graph.microsoft.com</c>).</summary>
    public string Resource { get; }

    /// <summary>The scope names relative to <see cref="Resource"/> in request order (<c>User.Read</c>, or <c>.default</c>), as a token's <c>scp</c> claim lists them.</summary>
    public IReadOnlyList<string> Names { get; }

    /// <summary>The fully qualified scopes in request order, as the token request sends them.</summary>
    public IReadOnlyList<string> Scopes { get; }

    /// <summary>True for the single <c>.default</c> scope (every permission granted to the identity).</summary>
    public bool IsDefault { get; }

    /// <summary>The canonical key of the set: the scopes lower-cased, sorted and space-joined. Equal sets have equal keys.</summary>
    public string CacheKey { get; }

    /// <summary>The <c>.default</c> scope of <paramref name="resource"/> (<c>https://graph.microsoft.com/.default</c>).</summary>
    /// <exception cref="ArgumentException">The resource is not an absolute origin (scheme and host, no path, query or fragment).</exception>
    public static TokenScopes Default(string resource)
    {
        return new TokenScopes(NormalizeResource(resource), [DefaultName], isDefault: true);
    }

    /// <summary>
    /// Explicit scopes of <paramref name="resource"/>. Each entry is a name (<c>User.Read</c>) or a scope already qualified with the same resource
    /// (<c>https://graph.microsoft.com/User.Read</c>); duplicates (case-insensitive) keep their first position.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// No scopes; a blank or malformed scope; a scope of another resource; <c>.default</c> among the scopes (use <see cref="Default"/>); or an OpenID Connect
    /// scope (<c>openid</c>, <c>profile</c>, <c>offline_access</c>), which the identity library adds itself.
    /// </exception>
    public static TokenScopes Explicit(string resource, IEnumerable<string> scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        var origin = NormalizeResource(resource);
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var scope in scopes)
        {
            var name = NameOf(origin, scope);
            if (seen.Add(name))
            {
                names.Add(name);
            }
        }

        if (names.Count == 0)
        {
            throw new ArgumentException($"At least one scope of {origin} is required; use TokenScopes.Default for its .default scope.", nameof(scopes));
        }

        return new TokenScopes(origin, names, isDefault: false);
    }

    /// <inheritdoc cref="Explicit(string, IEnumerable{string})"/>
    public static TokenScopes Explicit(string resource, params string[] scopes)
    {
        return Explicit(resource, (IEnumerable<string>)scopes);
    }

    public bool Equals(TokenScopes? other)
    {
        return other is not null && string.Equals(CacheKey, other.CacheKey, StringComparison.Ordinal);
    }

    public override bool Equals(object? obj)
    {
        return Equals(obj as TokenScopes);
    }

    public override int GetHashCode()
    {
        return StringComparer.Ordinal.GetHashCode(CacheKey);
    }

    /// <summary>The fully qualified scopes, space-separated (safe to log: scopes are not secrets).</summary>
    public override string ToString()
    {
        return string.Join(' ', Scopes);
    }

    private static string NormalizeResource(string resource)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);
        if (!Uri.TryCreate(resource.Trim(), UriKind.Absolute, out var uri)
            || string.IsNullOrEmpty(uri.Host)
            || uri.AbsolutePath != "/"
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArgumentException($"'{resource}' is not a resource origin (scheme and host only, such as https://graph.microsoft.com).", nameof(resource));
        }

        return uri.GetLeftPart(UriPartial.Authority);
    }

    private static string NameOf(string origin, string scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            throw new ArgumentException("A scope is blank.", nameof(scope));
        }

        var name = scope.Trim();
        if (name.Contains("://", StringComparison.Ordinal))
        {
            if (!name.StartsWith(origin + "/", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"The scope '{scope}' does not belong to {origin}: one token request covers one resource.", nameof(scope));
            }

            name = name[(origin.Length + 1)..];
        }

        if (name.Length == 0 || name.Any(c => char.IsWhiteSpace(c) || c == '/'))
        {
            throw new ArgumentException($"'{scope}' is not a scope name of {origin}.", nameof(scope));
        }

        if (string.Equals(name, DefaultName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"'{scope}' cannot be combined with explicit scopes: request {origin}/.default on its own (TokenScopes.Default).", nameof(scope));
        }

        if (ReservedNames.Contains(name))
        {
            throw new ArgumentException($"'{scope}' is an OpenID Connect scope the identity library adds itself; list only scopes of {origin}.", nameof(scope));
        }

        return name;
    }
}
