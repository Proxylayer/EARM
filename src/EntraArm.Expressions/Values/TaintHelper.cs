using System.Text.Json.Nodes;
using EntraArm.Core.Secrets;

namespace EntraArm.Expressions.Values;

/// <summary>Taint propagation rules of the expression engine (plan section 1.5, "Secure taint").</summary>
public static class TaintHelper
{
    /// <summary>
    /// Returns a deep copy in which every leaf is tainted: plain leaves become secrets, unknown leaves keep their
    /// symbol and are marked tainted. <c>null</c> leaves stay <c>null</c>.
    /// </summary>
    public static JsonNode? Taint(JsonNode? node)
    {
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
        });
    }

    /// <summary>Returns a deep copy with secret leaves revealed; unknown leaves are preserved.</summary>
    public static JsonNode? RevealSecrets(JsonNode? node)
    {
        return UnknownJson.Transform(node, static leaf => SecretJson.IsSecret(leaf, out var secret) ? secret.ToRevealedNode() : JsonValues.CloneLeaf(leaf));
    }
}
