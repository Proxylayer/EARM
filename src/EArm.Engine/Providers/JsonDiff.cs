using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using EArm.Core.Json;
using EArm.Core.Secrets;
using EArm.Expressions.Values;

namespace EArm.Engine.Providers;

/// <summary>
/// The shared diff: scalars replace; scalar arrays are order-insensitive sets; object arrays
/// compare by stable member key when the overlay names one, else by canonical JSON order-insensitively; nested
/// objects compare whole, generating an explicit <c>null</c> (<see cref="PropertyChangeType.Delete"/>) for every
/// live-only sub-property unless the overlay marks it <c>nullable: false</c>; GUIDs compare case-insensitively;
/// <c>null</c> and <c>[]</c> are absent; a secure value (<see cref="PropertyDelta.IsSecret"/>) is compared only as a scalar Graph reads back (a
/// <c>state</c> or a name from a secure parameter: <see cref="PropertyChangeType.Modify"/> when it differs, shown as <c>***</c>); a write-only
/// secret (a path the type declares <c>secure</c>, a value Graph does not return, a secure array) is <see cref="PropertyChangeType.NoEffect"/> on
/// an existing object, so it is not PATCHed again on every run (a create still sends it); a
/// deploy-time-unknown desired value yields an <see cref="PropertyDelta.IsUnknown"/> delta. Only declared
/// (desired) top-level properties take part: Graph PATCH is a merge and omitted properties stay untouched.
/// </summary>
public static partial class JsonDiff
{
    /// <summary>Compares the desired properties with the live (already stripped) object.</summary>
    public static IReadOnlyList<PropertyDelta> Diff(JsonObject? desired, JsonObject? live, JsonDiffOptions? options = null)
    {
        options ??= JsonDiffOptions.Default;
        var deltas = new List<PropertyDelta>();
        if (desired is null)
        {
            return deltas;
        }

        DiffMembers(desired, live, JsonPath.Root, string.Empty, nested: false, options, deltas);
        return deltas;
    }

    /// <summary>One <see cref="PropertyChangeType.Create"/> delta per desired top-level property (the what-if view of a create).</summary>
    public static IReadOnlyList<PropertyDelta> DescribeCreate(JsonObject? desired, JsonDiffOptions? options = null)
    {
        options ??= JsonDiffOptions.Default;
        var deltas = new List<PropertyDelta>();
        if (desired is null)
        {
            return deltas;
        }

        foreach (var property in desired)
        {
            var relative = JsonPath.Root.Append(property.Key);
            if (IsExcluded(relative, options) || JsonObjects.IsAbsent(property.Value))
            {
                continue;
            }

            var path = options.BasePath.Append(relative);
            bool secret = SecretJson.ContainsSecret(property.Value);
            bool unknown = !secret && UnknownJson.ContainsUnknown(property.Value);
            deltas.Add(new PropertyDelta(path, PropertyChangeType.Create, null, Render(property.Value), unknown, secret));
        }

        return deltas;
    }

    /// <summary>
    /// The PATCH body for the effective deltas: every changed top-level property in full (arrays and nested objects
    /// are replaced whole), nested objects carrying an explicit <c>null</c> for each live-only sub-property that is
    /// not <c>nullable: false</c> (<c>[]</c> when the live value is a collection: Graph refuses <c>null</c> for those), and
    /// <c>null</c> / <c>[]</c> for properties the template clears. Secret leaves are
    /// preserved (the provider reveals them right before the request).
    /// </summary>
    public static JsonObject BuildPatchBody(JsonObject desired, JsonObject? live, IReadOnlyList<PropertyDelta> deltas, JsonDiffOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(deltas);
        options ??= JsonDiffOptions.Default;

        var body = new JsonObject();
        foreach (var delta in deltas)
        {
            if (!delta.HasEffect || delta.Path.Depth <= options.BasePath.Depth)
            {
                continue;
            }

            var segment = delta.Path.Segments[options.BasePath.Depth];
            if (segment.IsIndex)
            {
                continue;
            }

            string key = JsonObjects.FindKey(desired, segment.Name!) ?? segment.Name!;
            if (body.ContainsKey(key))
            {
                continue;
            }

            JsonObjects.TryGetIgnoreCase(desired, key, out var desiredValue);
            JsonObjects.TryGetIgnoreCase(live, key, out var liveValue);

            if (delta.Kind == PropertyChangeType.Delete && JsonObjects.IsAbsent(desiredValue))
            {
                body[key] = liveValue is JsonArray ? new JsonArray() : null;
                continue;
            }

            if (desiredValue is JsonObject desiredObject && liveValue is JsonObject liveObject)
            {
                body[key] = MergeNested(desiredObject, liveObject, key, options);
                continue;
            }

            body[key] = JsonValues.Clone(desiredValue);
        }

        return body;
    }

    /// <summary>True when the delta touches an immutable path (the delta is at, below, or above an immutable path).</summary>
    public static bool IsImmutable(PropertyDelta delta, IReadOnlySet<JsonPath> immutable, JsonPath basePath)
    {
        ArgumentNullException.ThrowIfNull(delta);
        ArgumentNullException.ThrowIfNull(immutable);
        ArgumentNullException.ThrowIfNull(basePath);
        if (immutable.Count == 0 || !delta.Path.StartsWith(basePath))
        {
            return false;
        }

        var relative = StripPrefix(delta.Path, basePath);
        foreach (var path in immutable)
        {
            if (relative.StartsWith(path) || path.StartsWith(relative))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Structural equivalence under the diff rules (GUIDs case-insensitive, arrays order-insensitive, <c>null</c>/<c>[]</c> absent).</summary>
    public static bool AreEquivalent(JsonNode? left, JsonNode? right)
    {
        bool leftAbsent = JsonObjects.IsAbsent(left);
        bool rightAbsent = JsonObjects.IsAbsent(right);
        if (leftAbsent || rightAbsent)
        {
            return leftAbsent && rightAbsent;
        }

        switch (left)
        {
            case JsonObject leftObject:
                {
                    if (right is not JsonObject rightObject)
                    {
                        return false;
                    }

                    var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var property in leftObject)
                    {
                        keys.Add(property.Key);
                    }

                    foreach (var property in rightObject)
                    {
                        keys.Add(property.Key);
                    }

                    foreach (var key in keys)
                    {
                        JsonObjects.TryGetIgnoreCase(leftObject, key, out var l);
                        JsonObjects.TryGetIgnoreCase(rightObject, key, out var r);
                        if (!AreEquivalent(l, r))
                        {
                            return false;
                        }
                    }

                    return true;
                }

            case JsonArray leftArray:
                return right is JsonArray rightArray && SetEquals(leftArray, rightArray);

            default:
                return ScalarEquals(left, right);
        }
    }

    // ---- members ----

    private static void DiffMembers(JsonObject desired, JsonObject? live, JsonPath relative, string dotted, bool nested, JsonDiffOptions options, List<PropertyDelta> deltas)
    {
        foreach (var property in desired)
        {
            var childRelative = relative.Append(property.Key);
            if (IsExcluded(childRelative, options))
            {
                continue;
            }

            JsonObjects.TryGetIgnoreCase(live, property.Key, out var liveValue);
            DiffValue(property.Value, liveValue, childRelative, Join(dotted, property.Key), options, deltas);
        }

        if (!nested || live is null)
        {
            return;
        }

        foreach (var property in live)
        {
            if (JsonObjects.FindKey(desired, property.Key) is not null || JsonObjects.IsAbsent(property.Value))
            {
                continue;
            }

            var childRelative = relative.Append(property.Key);
            if (IsExcluded(childRelative, options))
            {
                continue;
            }

            string childDotted = Join(dotted, property.Key);
            bool nullable = !(options.Nullable.TryGetValue(childDotted, out var flag) && !flag);
            deltas.Add(new PropertyDelta(options.BasePath.Append(childRelative), nullable ? PropertyChangeType.Delete : PropertyChangeType.NoEffect, Render(property.Value), null));
        }
    }

    private static void DiffValue(JsonNode? desired, JsonNode? live, JsonPath relative, string dotted, JsonDiffOptions options, List<PropertyDelta> deltas)
    {
        var path = options.BasePath.Append(relative);
        bool liveAbsent = JsonObjects.IsAbsent(live);

        if (SecretJson.ContainsSecret(desired))
        {
            // Graph does not return write-only secrets (passwords, client secrets), so comparing them would always "differ": every run would PATCH them
            // (and --exit-code-on-changes would always fire). Diff only runs against an existing object: a write-only secret was written when the object
            // was created (a create-only path is excluded on update altogether), so on update it is NoEffect, and a new one reaches an existing object
            // through the type's own rotation (a password credential's rotationTag) or a replacement. A secure scalar Graph does read back (a policy
            // state, a display name passed as a secure parameter) is compared like any value and sent when it differs, masked in the what-if; equality
            // with a value the deployer can read anyway reveals nothing new. The non-secret members of a secret-bearing object are still compared; the
            // container stays IsSecret so the what-if masks it whole.
            if (desired is JsonObject desiredObject && live is JsonObject liveObject)
            {
                var children = new List<PropertyDelta>();
                DiffMembers(desiredObject, liveObject, relative, dotted, nested: true, options, children);
                deltas.Add(children.Exists(child => child.HasEffect)
                    ? new PropertyDelta(path, PropertyChangeType.Modify, RenderLive(live, desired), Render(desired), IsSecret: true, Children: children)
                    : new PropertyDelta(path, PropertyChangeType.NoEffect, RenderLive(live, desired), Render(desired), IsSecret: true));
                return;
            }

            if (IsReadBackSecretScalar(desired, live, relative, options))
            {
                if (!AreEquivalent(SecretJson.Reveal(desired), SecretJson.Reveal(live)))
                {
                    deltas.Add(new PropertyDelta(path, PropertyChangeType.Modify, RenderLive(live, desired), Render(desired), IsSecret: true));
                }

                return;
            }

            deltas.Add(new PropertyDelta(path, PropertyChangeType.NoEffect, RenderLive(live, desired), Render(desired), IsSecret: true));
            return;
        }

        if (UnknownJson.ContainsUnknown(desired))
        {
            deltas.Add(new PropertyDelta(path, liveAbsent ? PropertyChangeType.Create : PropertyChangeType.Modify, Render(live), Render(desired), IsUnknown: true));
            return;
        }

        bool desiredAbsent = JsonObjects.IsAbsent(desired);
        if (desiredAbsent && liveAbsent)
        {
            return;
        }

        if (desiredAbsent)
        {
            deltas.Add(new PropertyDelta(path, PropertyChangeType.Delete, Render(live), Render(desired)));
            return;
        }

        if (liveAbsent)
        {
            deltas.Add(new PropertyDelta(path, PropertyChangeType.Create, null, Render(desired)));
            return;
        }

        switch (desired)
        {
            case JsonObject desiredObject when live is JsonObject liveObject:
                {
                    var children = new List<PropertyDelta>();
                    DiffMembers(desiredObject, liveObject, relative, dotted, nested: true, options, children);
                    if (children.Count > 0)
                    {
                        deltas.Add(new PropertyDelta(path, PropertyChangeType.Modify, Render(live), Render(desired), Children: children));
                    }

                    return;
                }

            case JsonArray desiredArray when live is JsonArray liveArray:
                DiffArray(desiredArray, liveArray, relative, dotted, options, deltas);
                return;

            default:
                if (!ScalarEquals(desired, live))
                {
                    deltas.Add(new PropertyDelta(path, PropertyChangeType.Modify, Render(live), Render(desired)));
                }

                return;
        }
    }

    private static JsonObject MergeNested(JsonObject desired, JsonObject live, string dotted, JsonDiffOptions options)
    {
        var copy = (JsonObject)JsonValues.Clone(desired)!;
        foreach (var property in live)
        {
            string childDotted = Join(dotted, property.Key);
            string? desiredKey = JsonObjects.FindKey(desired, property.Key);
            if (desiredKey is null)
            {
                if (JsonObjects.IsAbsent(property.Value) || (options.Nullable.TryGetValue(childDotted, out var flag) && !flag))
                {
                    continue;
                }

                // A collection is cleared with [] (Graph rejects null for its non-nullable collections, such as web.redirectUris); anything else with null.
                copy[property.Key] = property.Value is JsonArray ? new JsonArray() : null;
                continue;
            }

            if (desired[desiredKey] is JsonObject desiredChild && property.Value is JsonObject liveChild)
            {
                copy[desiredKey] = MergeNested(desiredChild, liveChild, childDotted, options);
            }
        }

        return copy;
    }

    // ---- helpers ----

    /// <summary>
    /// True for a secure scalar whose live counterpart Graph returned (a non-null scalar) at a path the type does not declare <c>secure</c>: such a
    /// value can be compared. A write-only secret has no live value (or a declared secure path); a secure array or object is not judged whole.
    /// </summary>
    private static bool IsReadBackSecretScalar(JsonNode? desired, JsonNode? live, JsonPath relative, JsonDiffOptions options)
    {
        if (desired is not JsonValue || live is not JsonValue || JsonObjects.IsAbsent(live))
        {
            return false;
        }

        foreach (var secure in options.Secure)
        {
            if (relative.StartsWith(secure))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsExcluded(JsonPath relative, JsonDiffOptions options)
    {
        foreach (var excluded in options.Exclude)
        {
            if (relative.StartsWith(excluded))
            {
                return true;
            }
        }

        return false;
    }

    private static JsonPath StripPrefix(JsonPath path, JsonPath prefix)
    {
        var result = JsonPath.Root;
        for (int i = prefix.Depth; i < path.Depth; i++)
        {
            result = result.Append(path.Segments[i]);
        }

        return result;
    }

    private static string Join(string dotted, string key)
    {
        return dotted.Length == 0 ? key : dotted + "." + key;
    }

    /// <summary>Scalar equality: strings that are both GUIDs compare case-insensitively; everything else by value.</summary>
    public static bool ScalarEquals(JsonNode? left, JsonNode? right)
    {
        if (JsonValues.TryGetString(left, out var l) && JsonValues.TryGetString(right, out var r))
        {
            if (Guid.TryParse(l, out var leftGuid) && Guid.TryParse(r, out var rightGuid))
            {
                return leftGuid == rightGuid;
            }

            return string.Equals(l, r, StringComparison.Ordinal);
        }

        return JsonValues.AreEqual(left, right);
    }
}
