using System;
using System.Collections.Generic;
using System.Linq;
using EArm.Core.Types;

namespace EArm.Engine.Edges;

/// <summary>
/// The authoritative set of one target-side relationship property (<c>members</c>, <c>owners</c>) of the edge pass (<see cref="EdgePlanner"/>): the
/// principals declared on the target, merged with the principal-side declarations that name the same target (de-duplication across sides).
/// </summary>
internal sealed class AuthoritativeEdgeSet
{
    public AuthoritativeEdgeSet(EdgeRow row, EdgePropertyPlan property, string targetId, string? collection)
    {
        Row = row;
        Property = property;
        TargetId = targetId;
        Collection = collection;
    }

    public EdgeRow Row { get; }

    public EdgePropertyPlan Property { get; }

    public IEdgeKindHandler Handler => Property.Handler;

    public string TargetId { get; }

    public string? Collection { get; }

    /// <summary>Desired principals by normalised id: declared on the target, or on principals naming this target.</summary>
    public Dictionary<string, DesiredPrincipal> Desired { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void Add(string principalId, EdgeDeclaration declaration)
    {
        string key = Edge.NormalizeId(principalId);
        if (!Desired.TryGetValue(key, out var desired))
        {
            desired = new DesiredPrincipal(principalId);
            Desired[key] = desired;
        }

        desired.Declarations.Add(declaration);
    }

    public sealed class DesiredPrincipal
    {
        public DesiredPrincipal(string id)
        {
            Id = id;
        }

        public string Id { get; }

        public List<EdgeDeclaration> Declarations { get; } = [];
    }

    private static string SetKey(EdgeKind kind, string targetId)
    {
        return kind + "|" + Edge.NormalizeId(targetId);
    }

    public static Dictionary<string, AuthoritativeEdgeSet> BuildAll(List<EdgeRow> rows)
    {
        var sets = new Dictionary<string, AuthoritativeEdgeSet>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row.SelfId is null)
            {
                continue;
            }

            foreach (var property in row.Properties.Values)
            {
                if (property.Spec.Side != RelationshipSide.Target || !property.IsDeclared || property.Handler.IsSingleValued)
                {
                    continue;
                }

                string? collection = property.Declarations.FirstOrDefault(d => d.TargetCollection is not null)?.TargetCollection ?? EdgeGraphContext.CollectionOf(row.Declarer.Type);
                var set = new AuthoritativeEdgeSet(row, property, row.SelfId, collection);
                sets[SetKey(property.Handler.Kind, row.SelfId)] = set;
                foreach (var declaration in property.Declarations)
                {
                    if (declaration.OtherId is not null)
                    {
                        set.Add(declaration.OtherId, declaration);
                    }
                }
            }
        }

        // Principal-side declarations naming an authoritative target are merged into its set (de-duplication across sides).
        foreach (var row in rows)
        {
            foreach (var property in row.Properties.Values)
            {
                if (property.Spec.Side != RelationshipSide.Principal || !property.IsDeclared || property.Handler.IsSingleValued)
                {
                    continue;
                }

                foreach (var declaration in property.Declarations)
                {
                    if (declaration.OtherId is null || row.SelfId is null || !sets.TryGetValue(SetKey(property.Handler.Kind, declaration.OtherId), out var set))
                    {
                        continue;
                    }

                    declaration.GovernedByTarget = true;
                    set.Add(row.SelfId, declaration);
                }
            }
        }

        return sets;
    }
}
