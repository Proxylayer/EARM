using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using EntraArm.Core.Json;
using EntraArm.Core.Types;
using EntraArm.Engine.Providers;

namespace EntraArm.Engine.Edges;

/// <summary>
/// One normalised relationship: <c>(kind, target, principal[, extra])</c> (plan section 1.4). Ids are Graph object
/// ids; <see cref="TargetCollection"/> is the Graph collection of the target when the kind needs it to build the
/// path (<c>applications</c> / <c>servicePrincipals</c> / <c>groups</c> for ownership); <see cref="Extra"/> carries
/// the kind-specific payload of part B edges (app role id, directory scope...).
/// </summary>
public sealed record Edge(EdgeKind Kind, string TargetId, string PrincipalId, string? TargetCollection = null, JsonObject? Extra = null)
{
    /// <summary>Case-insensitive, GUID-aware id equality.</summary>
    public static bool IdEquals(string? left, string? right)
    {
        return JsonObjects.KeyEquals(left, right);
    }

    /// <summary>A normalised id for dictionary keys (GUIDs lower-case "D" form; other text lower-cased).</summary>
    public static string NormalizeId(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return Guid.TryParse(id, out var guid) ? guid.ToString("D") : id.Trim().ToLowerInvariant();
    }

    public override string ToString()
    {
        return $"{Kind}: {PrincipalId} -> {TargetId}";
    }
}

/// <summary>
/// One entry of a relationship property after normalisation: who declared it (the instance and the property),
/// the id written on the other side (<c>null</c> while it is a deploy-time placeholder), the declarer's own Graph id
/// (<c>null</c> when the declarer does not exist yet, in what-if) and the kind-specific extras.
/// </summary>
public sealed class EdgeDeclaration
{
    public EdgeDeclaration(ResourceInstance declarer, RelationshipSpec spec, EdgeKind kind, int index, JsonPath path)
    {
        ArgumentNullException.ThrowIfNull(declarer);
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(path);
        Declarer = declarer;
        Spec = spec;
        Kind = kind;
        Index = index;
        Path = path;
    }

    public ResourceInstance Declarer { get; }

    public RelationshipSpec Spec { get; }

    public EdgeKind Kind { get; }

    public EdgeSide Side => EdgeKinds.ToEdgeSide(Spec.Side);

    public string Property => Spec.Property;

    /// <summary>Position in the declared array; <c>-1</c> for a single-valued property (<c>manager</c>).</summary>
    public int Index { get; }

    /// <summary>ARM-style path of the entry (<c>properties.members[0]</c>), for messages.</summary>
    public JsonPath Path { get; }

    /// <summary>The declarer's Graph id; <c>null</c> when the declarer is still to be created (what-if).</summary>
    public string? SelfId { get; set; }

    /// <summary>The id on the other side of the edge; <c>null</c> while unknown (deploy-time placeholder) or for an explicit <c>null</c>.</summary>
    public string? OtherId { get; set; }

    /// <summary>The Graph collection of the target (ownership), when known.</summary>
    public string? TargetCollection { get; set; }

    public JsonObject? Extra { get; set; }

    /// <summary>True when the entry is a deploy-time placeholder (<c>reference()</c> of a resource that does not exist yet).</summary>
    public bool IsUnknown { get; set; }

    /// <summary>The placeholder's symbol, for messages.</summary>
    public string? Symbol { get; set; }

    /// <summary>An explicit <c>null</c> on a single-valued property: remove the edge.</summary>
    public bool IsClear { get; set; }

    /// <summary>Set when the edge is governed by an authoritative target-side declaration of the same template.</summary>
    public bool GovernedByTarget { get; set; }

    /// <summary>The edge once both ids are known; <c>null</c> otherwise.</summary>
    public Edge? Edge
    {
        get
        {
            if (SelfId is null || OtherId is null)
            {
                return null;
            }

            return Side == EdgeSide.Target
                ? new Edge(Kind, SelfId, OtherId, TargetCollection, Extra)
                : new Edge(Kind, OtherId, SelfId, TargetCollection, Extra);
        }
    }

    public string Describe()
    {
        return $"{Declarer.Describe()} {Path}";
    }
}

/// <summary>What the edge pass knows about a target after reading it.</summary>
/// <param name="Exists">False when the object is not in the tenant.</param>
/// <param name="IsDynamic">A dynamic group or administrative unit (rejects manual membership).</param>
/// <param name="Collection">The Graph collection the target lives in, when discovered.</param>
/// <param name="Edges">The principals attached to the target (principal id → live extra), or <c>null</c> when not read.</param>
public sealed record EdgeTargetState(bool Exists, bool IsDynamic, string? Collection, IReadOnlyDictionary<string, JsonObject?>? Edges)
{
    public static EdgeTargetState Missing { get; } = new(false, false, null, null);
}

/// <summary>The targets a principal is attached to, read from the principal's side (target id → live extra).</summary>
public sealed record EdgePrincipalState(bool Exists, IReadOnlyDictionary<string, JsonObject?> Edges)
{
    public static EdgePrincipalState Missing { get; } = new(false, new Dictionary<string, JsonObject?>(StringComparer.OrdinalIgnoreCase));
}

/// <summary>The outcome of normalising one declared item (see <see cref="IEdgeKindHandler.ResolveItemAsync"/>).</summary>
public sealed record EdgeItem(string? OtherId, string? TargetCollection = null, JsonObject? Extra = null, bool IsUnknown = false, string? Symbol = null, bool IsClear = false, string? Error = null)
{
    public static EdgeItem Unknown(string symbol)
    {
        return new EdgeItem(null, IsUnknown: true, Symbol: symbol);
    }

    public static EdgeItem Invalid(string error)
    {
        return new EdgeItem(null, Error: error);
    }

    public static EdgeItem Clear { get; } = new(null, IsClear: true);
}
