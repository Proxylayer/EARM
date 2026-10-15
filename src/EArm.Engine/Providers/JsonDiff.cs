using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using EArm.Core.Json;
using EArm.Core.Secrets;
using EArm.Core.Types;
using EArm.Expressions.Values;

namespace EArm.Engine.Providers;

/// <summary>Per-type knobs of <see cref="JsonDiff"/>, usually built from the catalog overlay.</summary>
public sealed record JsonDiffOptions
{
    public static JsonDiffOptions Default { get; } = new();

    /// <summary>Stable member key per array-of-objects property (dotted path relative to <c>properties</c> → key property).</summary>
    public IReadOnlyDictionary<string, string> MemberKey { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Sub-property paths Graph refuses to set to <c>null</c> (<c>false</c>): live-only values there are reported as <see cref="PropertyChangeType.NoEffect"/>.</summary>
    public IReadOnlyDictionary<string, bool> Nullable { get; init; } = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Paths (relative to <c>properties</c>) that are never compared (create-only properties on an existing object).</summary>
    public IReadOnlyList<JsonPath> Exclude { get; init; } = [];

    /// <summary>The prefix every delta path carries; ARM uses <c>properties</c>.</summary>
    public JsonPath BasePath { get; init; } = JsonPath.Of("properties");

    /// <summary>Options for a type: member keys and nullability from the overlay; create-only paths excluded when diffing against an existing object.</summary>
    public static JsonDiffOptions ForDefinition(ResourceTypeDefinition definition, bool existing)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return new JsonDiffOptions
        {
            MemberKey = definition.MemberKey,
            Nullable = definition.Nullable,
            Exclude = existing ? definition.CreateOnly : [],
        };
    }
}

/// <summary>
/// The shared diff: scalars replace; scalar arrays are order-insensitive sets; object arrays
/// compare by stable member key when the overlay names one, else by canonical JSON order-insensitively; nested
/// objects compare whole, generating an explicit <c>null</c> (<see cref="PropertyChangeType.Delete"/>) for every
/// live-only sub-property unless the overlay marks it <c>nullable: false</c>; GUIDs compare case-insensitively;
/// <c>null</c> and <c>[]</c> are absent; secrets are never compared (<see cref="PropertyDelta.IsSecret"/>: on an existing object a secret value is
/// <see cref="PropertyChangeType.NoEffect"/>, so it is not PATCHed again on every run; a create still sends it); a
/// deploy-time-unknown desired value yields an <see cref="PropertyDelta.IsUnknown"/> delta. Only declared
/// (desired) top-level properties take part: Graph PATCH is a merge and omitted properties stay untouched.
/// </summary>
public static class JsonDiff
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
    /// not <c>nullable: false</c>, and <c>null</c> / <c>[]</c> for properties the template clears. Secret leaves are
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

    /// <summary>A rendering copy: unknown leaves as <c>(deploy-time)</c>, secret leaves as <c>***</c>.</summary>
    public static JsonNode? Render(JsonNode? node)
    {
        return SecretJson.Redact(UnknownJson.Render(node));
    }

    /// <summary>A rendering copy of an object (see <see cref="Render(JsonNode?)"/>).</summary>
    public static JsonObject? RenderObject(JsonObject? node)
    {
        return Render(node) as JsonObject;
    }

    /// <summary>
    /// A rendering copy of a LIVE node with the counterparts of the secret desired leaves masked: where
    /// <paramref name="desired"/> holds a secret, the live value at the same position (which on a rerun IS the
    /// secret) is shown as <c>***</c>, never verbatim. A secret leaf masks its live leaf; a container that holds a
    /// secret and has no structural counterpart (a secret-bearing array, a shape mismatch) masks every live leaf
    /// below it; objects are matched by property name (case-insensitive) and recursed.
    /// </summary>
    public static JsonNode? RenderLive(JsonNode? live, JsonNode? desired)
    {
        if (live is null)
        {
            return null;
        }

        if (!SecretJson.ContainsSecret(desired))
        {
            return Render(live);
        }

        if (desired is JsonObject desiredObject && live is JsonObject liveObject)
        {
            var copy = new JsonObject();
            foreach (var property in liveObject)
            {
                JsonObjects.TryGetIgnoreCase(desiredObject, property.Key, out var desiredValue);
                copy[property.Key] = RenderLive(property.Value, desiredValue);
            }

            return copy;
        }

        return MaskLeaves(live);
    }

    /// <summary>A rendering copy of a live object (see <see cref="RenderLive(JsonNode?, JsonNode?)"/>).</summary>
    public static JsonObject? RenderLiveObject(JsonObject? live, JsonObject? desired)
    {
        return RenderLive(live, desired) as JsonObject;
    }

    /// <summary>
    /// Masks, in place, every leaf under the given overlay <c>secure</c> paths of a rendering copy (property names
    /// case-insensitive, array indexes literal; a segment that does not exist is skipped), so a live value that the
    /// type declares secret never renders even when the template does not declare it.
    /// </summary>
    public static void MaskSecurePaths(JsonObject? rendered, IReadOnlyList<JsonPath> securePaths)
    {
        ArgumentNullException.ThrowIfNull(securePaths);
        if (rendered is null)
        {
            return;
        }

        foreach (var path in securePaths)
        {
            if (!path.IsRoot)
            {
                MaskAt(rendered, path.Segments, 0);
            }
        }
    }

    private static void MaskAt(JsonNode? node, IReadOnlyList<JsonPathSegment> segments, int depth)
    {
        var segment = segments[depth];
        bool last = depth == segments.Count - 1;
        switch (node)
        {
            case JsonObject obj when !segment.IsIndex && JsonObjects.FindKey(obj, segment.Name!) is { } key:
                if (last)
                {
                    obj[key] = MaskLeaves(obj[key]);
                }
                else
                {
                    MaskAt(obj[key], segments, depth + 1);
                }

                break;

            case JsonArray array when segment.IsIndex && segment.Index < array.Count:
                if (last)
                {
                    array[segment.Index] = MaskLeaves(array[segment.Index]);
                }
                else
                {
                    MaskAt(array[segment.Index], segments, depth + 1);
                }

                break;
        }
    }

    /// <summary>A copy of <paramref name="node"/> with every non-null leaf replaced by <c>***</c>.</summary>
    private static JsonNode? MaskLeaves(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonObject obj:
                {
                    var copy = new JsonObject();
                    foreach (var property in obj)
                    {
                        copy[property.Key] = MaskLeaves(property.Value);
                    }

                    return copy;
                }

            case JsonArray array:
                {
                    var copy = new JsonArray();
                    foreach (var item in array)
                    {
                        copy.Add(MaskLeaves(item));
                    }

                    return copy;
                }

            default:
                return JsonValue.Create(SecretValue.Mask);
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
            // Secrets are never compared, and Graph does not return most of them, so a secret-bearing value always "differed": every run PATCHed it (and
            // --exit-code-on-changes always fired). Diff only runs against an existing object: the secret was written when the object was created (a
            // create-only path is excluded on update altogether), so on update it is NoEffect. A new secret reaches an existing object through
            // the type's own rotation (a password credential's rotationTag) or a replacement. The non-secret members of a secret-bearing object are still
            // compared; the container stays IsSecret so the what-if masks it whole.
            if (desired is JsonObject desiredObject && live is JsonObject liveObject)
            {
                var children = new List<PropertyDelta>();
                DiffMembers(desiredObject, liveObject, relative, dotted, nested: true, options, children);
                deltas.Add(children.Exists(child => child.HasEffect)
                    ? new PropertyDelta(path, PropertyChangeType.Modify, RenderLive(live, desired), Render(desired), IsSecret: true, Children: children)
                    : new PropertyDelta(path, PropertyChangeType.NoEffect, RenderLive(live, desired), Render(desired), IsSecret: true));
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

    private static void DiffArray(JsonArray desired, JsonArray live, JsonPath relative, string dotted, JsonDiffOptions options, List<PropertyDelta> deltas)
    {
        var path = options.BasePath.Append(relative);
        List<PropertyDelta> children;
        if (options.MemberKey.TryGetValue(dotted, out var memberKey) && AllObjects(desired) && AllObjects(live))
        {
            children = DiffKeyedArray(desired, live, memberKey, relative, dotted, options);
        }
        else
        {
            children = DiffSetArray(desired, live, relative, options);
        }

        if (children.Count > 0)
        {
            deltas.Add(new PropertyDelta(path, PropertyChangeType.Array, Render(live), Render(desired), Children: children));
        }
    }

    private static List<PropertyDelta> DiffSetArray(JsonArray desired, JsonArray live, JsonPath relative, JsonDiffOptions options)
    {
        var liveCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in live)
        {
            string canonical = SetKey(item);
            liveCounts[canonical] = liveCounts.GetValueOrDefault(canonical) + 1;
        }

        var desiredCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in desired)
        {
            string canonical = SetKey(item);
            desiredCounts[canonical] = desiredCounts.GetValueOrDefault(canonical) + 1;
        }

        var children = new List<PropertyDelta>();
        var remainingLive = new Dictionary<string, int>(liveCounts, StringComparer.Ordinal);
        for (int i = 0; i < desired.Count; i++)
        {
            string canonical = SetKey(desired[i]);
            if (remainingLive.GetValueOrDefault(canonical) > 0)
            {
                remainingLive[canonical]--;
                continue;
            }

            children.Add(new PropertyDelta(options.BasePath.Append(relative.Append(i)), PropertyChangeType.Create, null, Render(desired[i])));
        }

        var remainingDesired = new Dictionary<string, int>(desiredCounts, StringComparer.Ordinal);
        for (int j = 0; j < live.Count; j++)
        {
            string canonical = SetKey(live[j]);
            if (remainingDesired.GetValueOrDefault(canonical) > 0)
            {
                remainingDesired[canonical]--;
                continue;
            }

            children.Add(new PropertyDelta(options.BasePath.Append(relative.Append(j)), PropertyChangeType.Delete, Render(live[j]), null));
        }

        return children;
    }

    private static List<PropertyDelta> DiffKeyedArray(JsonArray desired, JsonArray live, string memberKey, JsonPath relative, string dotted, JsonDiffOptions options)
    {
        var children = new List<PropertyDelta>();
        var liveByKey = new List<(string Key, JsonObject Member)>();
        var liveMatched = new bool[live.Count];
        for (int j = 0; j < live.Count; j++)
        {
            var member = (JsonObject)live[j]!;
            liveByKey.Add((JsonObjects.GetString(member, memberKey) ?? string.Empty, member));
        }

        for (int i = 0; i < desired.Count; i++)
        {
            var member = (JsonObject)desired[i]!;
            string? key = JsonObjects.GetString(member, memberKey);
            int match = -1;
            if (key is not null)
            {
                for (int j = 0; j < liveByKey.Count; j++)
                {
                    if (!liveMatched[j] && liveByKey[j].Key.Length > 0 && JsonObjects.KeyEquals(liveByKey[j].Key, key))
                    {
                        match = j;
                        break;
                    }
                }
            }

            var memberRelative = relative.Append(key ?? i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (match < 0)
            {
                children.Add(new PropertyDelta(options.BasePath.Append(memberRelative), PropertyChangeType.Create, null, Render(member)));
                continue;
            }

            liveMatched[match] = true;
            var memberChildren = new List<PropertyDelta>();
            DiffMembers(member, liveByKey[match].Member, memberRelative, dotted, nested: true, options, memberChildren);
            if (memberChildren.Count > 0)
            {
                children.Add(new PropertyDelta(options.BasePath.Append(memberRelative), PropertyChangeType.Modify, Render(liveByKey[match].Member), Render(member), Children: memberChildren));
            }
        }

        for (int j = 0; j < liveByKey.Count; j++)
        {
            if (liveMatched[j])
            {
                continue;
            }

            string key = liveByKey[j].Key.Length > 0 ? liveByKey[j].Key : j.ToString(System.Globalization.CultureInfo.InvariantCulture);
            children.Add(new PropertyDelta(options.BasePath.Append(relative.Append(key)), PropertyChangeType.Delete, Render(liveByKey[j].Member), null));
        }

        return children;
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

                copy[property.Key] = null;
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

    private static bool AllObjects(JsonArray array)
    {
        foreach (var item in array)
        {
            if (item is not JsonObject)
            {
                return false;
            }
        }

        return true;
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

    /// <summary>Canonical JSON of a node with GUID strings lower-cased, for order-insensitive array comparison.</summary>
    public static string Canonical(JsonNode? node)
    {
        return CanonicalJson.Serialize(NormalizeGuids(node));
    }

    /// <summary>
    /// Multiset equality of two arrays under the diff rules: the same canonical <see cref="SetKey"/> per element, with
    /// duplicates counted (<c>[1,1,2]</c> differs from <c>[1,2,2]</c>), regardless of order. <see cref="Diff"/> uses the same key.
    /// </summary>
    private static bool SetEquals(JsonArray left, JsonArray right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        var leftKeys = new List<string>(left.Count);
        foreach (var item in left)
        {
            leftKeys.Add(SetKey(item));
        }

        var rightKeys = new List<string>(right.Count);
        foreach (var item in right)
        {
            rightKeys.Add(SetKey(item));
        }

        leftKeys.Sort(StringComparer.Ordinal);
        rightKeys.Sort(StringComparer.Ordinal);
        return leftKeys.SequenceEqual(rightKeys, StringComparer.Ordinal);
    }

    /// <summary>
    /// Order-insensitive canonical form of an array element: <see cref="Canonical"/> (GUIDs lower-cased, <c>null</c>/<c>[]</c>
    /// members dropped) with object keys lower-cased and every nested array sorted, so two elements are equal exactly when
    /// they are equivalent regardless of member case or the order of any array below them.
    /// </summary>
    private static string SetKey(JsonNode? node)
    {
        return CanonicalJson.Serialize(OrderInsensitive(NormalizeGuids(node)));
    }

    private static JsonNode? OrderInsensitive(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                {
                    var copy = new JsonObject();
                    foreach (var property in obj)
                    {
                        string key = property.Key.ToLowerInvariant();
                        if (!copy.ContainsKey(key))
                        {
                            copy[key] = OrderInsensitive(property.Value);
                        }
                    }

                    return copy;
                }

            case JsonArray array:
                {
                    var items = new List<(string Key, JsonNode? Node)>(array.Count);
                    foreach (var item in array)
                    {
                        var form = OrderInsensitive(item);
                        items.Add((CanonicalJson.Serialize(form), form));
                    }

                    items.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
                    var copy = new JsonArray();
                    foreach (var item in items)
                    {
                        copy.Add(item.Node);
                    }

                    return copy;
                }

            default:
                return node?.DeepClone();
        }
    }
    private static JsonNode? NormalizeGuids(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                {
                    var copy = new JsonObject();
                    foreach (var property in obj)
                    {
                        if (JsonObjects.IsAbsent(property.Value))
                        {
                            // null / [] members are absent for comparison purposes.
                            continue;
                        }

                        copy[property.Key] = NormalizeGuids(property.Value);
                    }

                    return copy;
                }

            case JsonArray array:
                {
                    var copy = new JsonArray();
                    foreach (var item in array)
                    {
                        copy.Add(NormalizeGuids(item));
                    }

                    return copy;
                }

            case JsonValue value:
                if (!SecretJson.IsSecret(value) && !UnknownJson.IsUnknown(value) && value.GetValueKind() == JsonValueKind.String)
                {
                    string text = value.GetValue<string>();
                    return Guid.TryParse(text, out var guid) ? JsonValue.Create(guid.ToString("D")) : JsonValue.Create(text);
                }

                return JsonValues.Clone(value);

            default:
                return null;
        }
    }
}
