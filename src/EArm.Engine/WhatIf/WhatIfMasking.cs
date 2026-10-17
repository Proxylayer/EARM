using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Core.Json;
using EArm.Core.Secrets;
using EArm.Engine.Providers;

namespace EArm.Engine.WhatIf;

/// <summary>Path helpers shared by the what-if renderers.</summary>
internal static class WhatIfPaths
{
    /// <summary>
    /// A child delta's path relative to its parent delta (<c>properties.members[0]</c> under <c>properties.members</c>
    /// is <c>[0]</c>, <c>properties.appRoles['id']</c> under <c>properties.appRoles</c> is <c>['id']</c>); edge-pass
    /// children are already relative and come back unchanged; a path that does not nest under the parent is
    /// returned as is.
    /// </summary>
    public static string Relative(JsonPath path, JsonPath parent)
    {
        if (parent.IsRoot || !path.StartsWith(parent) || path.Depth == parent.Depth)
        {
            return path.ToString();
        }

        var relative = JsonPath.Root;
        for (int i = parent.Depth; i < path.Depth; i++)
        {
            relative = relative.Append(path.Segments[i]);
        }

        return relative.ToString();
    }
}

/// <summary>Secure-value masking and deploy-time detection shared by the what-if renderers.</summary>
internal static class WhatIfMasking
{
    private static readonly JsonPath PropertiesPath = JsonPath.Of("properties");

    /// <summary>A rendering copy of <paramref name="properties"/> with tainted leaves and the type's secure paths replaced by <c>***</c>.</summary>
    public static JsonObject MaskProperties(JsonObject properties, IReadOnlyList<JsonPath> secure)
    {
        var copy = (JsonObject)SecretJson.Redact(properties)!;
        foreach (var path in secure)
        {
            MaskAt(copy, path, 0);
        }

        return copy;
    }

    /// <summary>Deltas with <see cref="PropertyDelta.IsSecret"/> (or a path under a secure path) rendered as <c>***</c> on both sides, recursively.</summary>
    public static IReadOnlyList<PropertyDelta> MaskDeltas(IReadOnlyList<PropertyDelta> deltas, IReadOnlyList<JsonPath> secure)
    {
        if (deltas.Count == 0)
        {
            return deltas;
        }

        var masked = new List<PropertyDelta>(deltas.Count);
        foreach (var delta in deltas)
        {
            masked.Add(Mask(delta, secure, parentSecret: false));
        }

        return masked;
    }

    public static bool ContainsUnknown(PropertyDelta delta)
    {
        if (delta.IsUnknown)
        {
            return true;
        }

        return delta.Children is { Count: > 0 } children && children.Any(ContainsUnknown);
    }

    private static PropertyDelta Mask(PropertyDelta delta, IReadOnlyList<JsonPath> secure, bool parentSecret)
    {
        bool secret = parentSecret || delta.IsSecret || IsSecurePath(delta.Path, secure);
        IReadOnlyList<PropertyDelta>? children = delta.Children;
        if (children is { Count: > 0 })
        {
            var maskedChildren = new List<PropertyDelta>(children.Count);
            foreach (var child in children)
            {
                maskedChildren.Add(Mask(child, secure, secret));
            }

            children = maskedChildren;
        }

        if (!secret)
        {
            return ReferenceEquals(children, delta.Children) ? delta : delta with { Children = children };
        }

        return delta with
        {
            IsSecret = true,
            Before = delta.Before is null ? null : JsonValue.Create(SecretValue.Mask),
            After = delta.After is null ? null : JsonValue.Create(SecretValue.Mask),
            Children = children,
        };
    }

    /// <summary>True when the delta path (<c>properties.x.y</c>) is at or under one of the type's secure paths (relative to <c>properties</c>).</summary>
    private static bool IsSecurePath(JsonPath path, IReadOnlyList<JsonPath> secure)
    {
        if (secure.Count == 0 || !path.StartsWith(PropertiesPath))
        {
            return false;
        }

        foreach (var securePath in secure)
        {
            if (path.StartsWith(PropertiesPath.Append(securePath)))
            {
                return true;
            }
        }

        return false;
    }

    private static void MaskAt(JsonNode? node, JsonPath path, int index)
    {
        if (node is null)
        {
            return;
        }

        if (index == path.Depth)
        {
            return;
        }

        var segment = path.Segments[index];
        bool last = index == path.Depth - 1;
        switch (node)
        {
            case JsonObject obj when !segment.IsIndex:
                {
                    if (!obj.TryGetPropertyValue(segment.Name!, out var child) || child is null)
                    {
                        return;
                    }

                    if (last)
                    {
                        obj[segment.Name!] = JsonValue.Create(SecretValue.Mask);
                        return;
                    }

                    MaskAt(child, path, index + 1);
                    return;
                }

            case JsonArray array when segment.IsIndex:
                {
                    if (segment.Index < 0 || segment.Index >= array.Count || array[segment.Index] is null)
                    {
                        return;
                    }

                    if (last)
                    {
                        array[segment.Index] = JsonValue.Create(SecretValue.Mask);
                        return;
                    }

                    MaskAt(array[segment.Index], path, index + 1);
                    return;
                }

            case JsonArray array when !segment.IsIndex:
                {
                    // A secure path through an array of objects applies to every member.
                    foreach (var item in array)
                    {
                        MaskAt(item, path, index);
                    }

                    return;
                }
        }
    }
}
