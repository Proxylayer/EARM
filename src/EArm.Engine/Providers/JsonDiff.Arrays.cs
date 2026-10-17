using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using EArm.Core.Json;
using EArm.Core.Secrets;
using EArm.Expressions.Values;

namespace EArm.Engine.Providers;

public static partial class JsonDiff
{
    // ---- arrays ----

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
