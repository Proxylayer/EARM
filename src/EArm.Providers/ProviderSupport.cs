using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EArm.Core.Types;
using EArm.Engine;
using EArm.Engine.Providers;
using EArm.Graph;
using EArm.Expressions.Values;

namespace EArm.Providers;

/// <summary>The warning codes the providers attach to <see cref="AppliedResource.Warnings"/> or put in a what-if row's reasons.</summary>
public static class ProviderWarnings
{
    /// <summary>A <c>/directory/deletedItems</c> probe was refused (403) or rejected (400): the create proceeds without knowing whether a soft-deleted object holds the key.</summary>
    public const string SoftDeleteProbeSkipped = EngineDiagnosticCodes.SoftDeleteProbeSkipped;

    /// <summary>A password credential expires (or expired) and rotating it would not extend its life (the declared or default end date is not later).</summary>
    public const string SecretExpiresSoon = EngineDiagnosticCodes.SecretExpiresSoon;

    /// <summary>A live password credential's displayName carries no rotation tag while the template declares one (a credential created before the tag was carried in the name): it is treated as matching and the next rotation writes the tag into the name.</summary>
    public const string RotationTagNotStamped = EngineDiagnosticCodes.RotationTagNotStamped;

    /// <summary>A group is created without owners declared: created with an application token it is ownerless ("anonymous") and cannot be managed by anyone but an administrator.</summary>
    public const string GroupHasNoOwnerAtCreate = EngineDiagnosticCodes.GroupHasNoOwnerAtCreate;

    /// <summary>A <c>addPassword</c> call failed ambiguously and a credential with the same name that EARM did not record exists: it is reported, never removed.</summary>
    public const string PasswordCredentialOrphaned = EngineDiagnosticCodes.PasswordCredentialOrphaned;

    /// <summary>A FIDO2 authentication method configuration declares the deprecated <c>isAttestationEnforced</c> / <c>keyRestrictions</c> (removed October 2027; use <c>passkeyProfiles</c>).</summary>
    public const string DeprecatedAuthenticationMethodProperty = EngineDiagnosticCodes.DeprecatedAuthenticationMethodProperty;

    /// <summary>The text of a reason / diagnostic: <c>Code: message</c>.</summary>
    public static string Format(string code, string message)
    {
        return code + ": " + message;
    }
}

/// <summary>
/// Helpers shared by the providers: OData literal escaping, the per-read <c>$select</c> (managed set ∪ declared
/// top-level properties), the overlay's <c>ignoreLiveOnly</c> pruning, and the classification of a failed
/// <c>/directory/deletedItems</c> probe.
/// </summary>
public static class ProviderSupport
{
    private static readonly Regex PropertyName = new(@"^[A-Za-z][A-Za-z0-9_]*$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>The value of an OData string literal: every <c>'</c> doubled.</summary>
    public static string EscapeLiteral(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Replace("'", "''", StringComparison.Ordinal);
    }

    // ---- key ----

    /// <summary>
    /// The one key value of an alternate-key type, after checking that the key really is the type's single <paramref name="expectedName"/> key
    /// (the catalog's <c>nameMapsTo</c>): a key that carries another property name (a composite key, an <c>appId</c> key handed to a
    /// <c>uniqueName</c> type, a key of the wrong type) must never be read as "the name" - it would be sent to Graph as an alternate key of the wrong kind.
    /// </summary>
    /// <exception cref="ProviderException"><see cref="EngineDiagnosticCodes.ResourceKeyUnresolved"/> when the key does not consist of exactly the <paramref name="expectedName"/> value.</exception>
    public static string SingleKey(ResourceKey key, string expectedName)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedName);
        if (!key.TryGet(expectedName, out var value))
        {
            throw new ProviderException(EngineDiagnosticCodes.ResourceKeyUnresolved, $"{key.Type}: the key {key.Describe()} carries no {expectedName}.");
        }

        if (key.Values.Count != 1)
        {
            throw new ProviderException(EngineDiagnosticCodes.ResourceKeyUnresolved, $"{key.Type}: the key {key.Describe()} must consist of the single property '{expectedName}'.");
        }

        return value;
    }

    // ---- soft-delete probes ----

    /// <summary>
    /// True for the failures that mean "this identity cannot run the soft-delete probe": 403 (the listing needs
    /// <c>Application.Read.All</c> / <c>Directory.Read.All</c> / the type's read permission, which an
    /// <c>Application.ReadWrite.OwnedBy</c>-only identity lacks) and 400 (the filter is not supported on this endpoint).
    /// The create proceeds and the provider reports <see cref="ProviderWarnings.SoftDeleteProbeSkipped"/>.
    /// </summary>
    public static bool IsProbeUnavailable(GraphException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.Status is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest;
    }

    /// <summary>The warning for a skipped probe of <paramref name="what"/> (<c>application</c>, <c>user</c>...) holding <paramref name="key"/>.</summary>
    public static ProviderWarning ProbeSkipped(string what, string key, GraphException cause)
    {
        ArgumentNullException.ThrowIfNull(cause);
        return new ProviderWarning(
            ProviderWarnings.SoftDeleteProbeSkipped,
            $"the soft-delete probe for {what} '{key}' (GET /directory/deletedItems/microsoft.graph.{what}) was refused ({(int)cause.Status} {cause.Code}); creating without checking for a soft-deleted holder of the key. Grant the read permission of the type (for example Application.Read.All) to enable the probe.");
    }

    // ---- ignoreLiveOnly ----

    /// <summary>The overlay's <c>x-earm.ignoreLiveOnly</c> dotted paths (empty when the type has none).</summary>
    public static IReadOnlyList<string> IgnoreLiveOnlyPaths(ResourceTypeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Schema["x-earm"] is not JsonObject overlay || overlay["ignoreLiveOnly"] is not JsonArray paths)
        {
            return [];
        }

        var result = new List<string>();
        foreach (var path in paths)
        {
            if (path is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
            {
                result.Add(text);
            }
        }

        return result;
    }

    /// <summary>
    /// Applies the overlay's <c>ignoreLiveOnly</c> paths: at each path, live sub-properties (at any depth) that the
    /// desired object does not declare are removed from a copy of the live diff view, so Graph's server-defaulted
    /// children (<c>sessionControls.signInFrequency.authenticationType</c>, <c>web.redirectUriSettings</c>...) are neither
    /// drift nor nulled in the PATCH body. A path the template does not declare is left alone.
    /// </summary>
    public static ExistingResource PruneLiveOnly(ResourceTypeDefinition definition, JsonObject desired, ExistingResource existing)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(existing);
        var paths = IgnoreLiveOnlyPaths(definition);
        if (paths.Count == 0)
        {
            return existing;
        }

        var live = (JsonObject)JsonValues.Clone(existing.Properties)!;
        bool changed = false;
        foreach (string path in paths)
        {
            if (JsonObjects.TryGetDotted(desired, path, out var desiredNode) && desiredNode is JsonObject desiredObject
                && JsonObjects.TryGetDotted(live, path, out var liveNode) && liveNode is JsonObject liveObject)
            {
                changed |= RemoveLiveOnly(desiredObject, liveObject);
            }
        }

        return changed ? existing with { Properties = live } : existing;
    }

    private static bool RemoveLiveOnly(JsonObject desired, JsonObject live)
    {
        bool changed = false;
        foreach (string key in live.Select(p => p.Key).ToList())
        {
            if (JsonObjects.FindKey(desired, key) is not { } desiredKey)
            {
                live.Remove(key);
                changed = true;
                continue;
            }

            if (desired[desiredKey] is JsonObject desiredChild && live[key] is JsonObject liveChild)
            {
                changed |= RemoveLiveOnly(desiredChild, liveChild);
            }
        }

        return changed;
    }

    // ---- $select ----

    /// <summary>
    /// The top-level property names a declaration adds to a read's <c>$select</c>: every name that is a plain Graph
    /// property identifier and not an edge property, a create-only / secure root, a server-managed (<c>stripOnRead</c>)
    /// root or one of <paramref name="exclude"/>.
    /// </summary>
    public static IReadOnlyList<string> DeclaredSelectNames(ResourceTypeDefinition definition, JsonObject? declared, IEnumerable<string>? exclude = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (declared is null)
        {
            return [];
        }

        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relationship in definition.Relationships)
        {
            skip.Add(relationship.Property);
        }

        foreach (var path in definition.CreateOnly.Concat(definition.Secure).Concat(definition.StripOnRead))
        {
            if (!path.IsRoot && path.Segments[0].Name is { } root)
            {
                skip.Add(root);
            }
        }

        if (exclude is not null)
        {
            foreach (string name in exclude)
            {
                skip.Add(name);
            }
        }

        var names = new List<string>();
        foreach (var property in declared)
        {
            if (PropertyName.IsMatch(property.Key) && !skip.Contains(property.Key) && !names.Contains(property.Key, StringComparer.OrdinalIgnoreCase))
            {
                names.Add(property.Key);
            }
        }

        return names;
    }

    /// <summary>The managed set plus the declared names that are not in it (case-insensitive), in that order.</summary>
    public static string[] Union(IEnumerable<string> managed, IEnumerable<string> declared)
    {
        ArgumentNullException.ThrowIfNull(managed);
        ArgumentNullException.ThrowIfNull(declared);
        var result = managed.ToList();
        foreach (string name in declared)
        {
            if (!result.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(name);
            }
        }

        return [.. result];
    }
}

/// <summary>
/// Remembers, per resource key, the top-level properties a template declares so a later <c>ReadAsync(key, …)</c> can
/// select them next to the provider's managed set (the engine reads by key before it plans; a property outside the
/// managed <c>$select</c> would otherwise never be compared and be PATCHed on every run). Filled by the provider's
/// <c>ResolveKey</c>, consulted by its reads; keys the template never declared (existing-resource lookups, deletes)
/// select the managed set only.
/// </summary>
public sealed class DeclaredSelects
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _byKey = new(StringComparer.Ordinal);

    /// <summary>Records the names the instance declares for <paramref name="key"/>.</summary>
    public void Register(ResourceKey key, ResourceTypeDefinition definition, ResourceInstance desired, IEnumerable<string>? exclude = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(desired);
        var source = desired.Properties ?? desired.Declaration.Properties as JsonObject;
        var names = ProviderSupport.DeclaredSelectNames(definition, source, exclude);
        if (names.Count > 0)
        {
            _byKey[KeyOf(key)] = names;
        }
        else
        {
            _byKey.TryRemove(KeyOf(key), out _);
        }
    }

    /// <summary>The managed set plus what was registered for <paramref name="key"/>.</summary>
    public string[] For(ResourceKey key, IEnumerable<string> managed)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(managed);
        return _byKey.TryGetValue(KeyOf(key), out var declared) ? ProviderSupport.Union(managed, declared) : [.. managed];
    }

    private static string KeyOf(ResourceKey key)
    {
        return key.Type + "|" + key.Api + "|" + key.Describe();
    }
}
