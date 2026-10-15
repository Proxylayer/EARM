using System.Text.Json.Nodes;
using EArm.Core.Secrets;

namespace EArm.Expressions.Values;

/// <summary>Taint propagation rules of the expression engine ("Secure taint").</summary>
public static class TaintHelper
{
    /// <summary>
    /// Returns a deep copy in which every leaf is tainted: plain leaves become secrets, unknown leaves keep their
    /// symbol and are marked tainted. <c>null</c> leaves inside a container stay <c>null</c>. The result of a computation has no leaf that could carry the taint when
    /// it is empty or <c>null</c>, yet that emptiness may be the secret (a <c>filter</c> by a secret verdict, a <c>take</c> by a secret count): every empty array and object
    /// is marked as a tainted empty (<see cref="SecretJson.MarkTaintedEmpty"/>) and a <c>null</c> result becomes a tainted <c>null</c>
    /// (<see cref="SecretJson.CreateNull"/>), so the value still counts as secret and cannot reach a plain output.
    /// </summary>
    public static JsonNode? Taint(JsonNode? node)
    {
        if (node is null)
        {
            return SecretJson.CreateNull();
        }

        return UnknownJson.Transform(node, static leaf =>
        {
            if (leaf is null)
            {
                return null;
            }

            if (UnknownJson.IsUnknown(leaf, out var unknown))
            {
                return UnknownJson.Create(unknown.Tainted ? unknown : unknown with { Tainted = true });
            }

            return SecretJson.Create(SecretValue.FromLeaf(leaf)!);
        }, UnknownJson.EmptyMarks.MarkAll);
    }

    /// <summary>Returns a deep copy with secret leaves revealed; unknown leaves are preserved.</summary>
    public static JsonNode? RevealSecrets(JsonNode? node)
    {
        return UnknownJson.Transform(node, static leaf => SecretJson.IsSecret(leaf, out var secret) ? secret.ToRevealedNode() : JsonValues.CloneLeaf(leaf), UnknownJson.EmptyMarks.Drop);
    }
}
