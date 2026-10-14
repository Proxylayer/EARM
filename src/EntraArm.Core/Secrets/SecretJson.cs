using System;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace EntraArm.Core.Secrets;

/// <summary>
/// Taint marking for <see cref="JsonNode"/> trees.
/// <para>
/// The marker is the <see cref="SecretValue"/> itself: a tainted leaf is a <see cref="JsonValue"/> whose payload is a
/// <see cref="SecretValue"/> (created through <see cref="Create"/>). No side table is needed, so the mark survives
/// <see cref="JsonNode.DeepClone"/>, re-parenting and serialization: <see cref="JsonNode.ToJsonString"/> and every
/// serializer without the <see cref="RevealingJsonConverter"/> write <c>***</c>. Containers are never tainted as a
/// whole; <see cref="Taint"/> marks every leaf, which keeps the structure of a <c>secureObject</c> visible while
/// hiding its values.
/// </para>
/// <para>
/// Caveats: (1) <see cref="JsonNode.DeepClone"/> does NOT preserve the taint: the runtime clones a customized value
/// through its serialized form, so the copy holds the plain string <c>***</c>. Always copy trees that may contain
/// secrets with <see cref="Clone"/>. (2) <see cref="JsonNode.DeepEquals"/> compares tainted leaves through their
/// redacted form, so any two secrets compare equal; compare revealed copies when that matters.
/// (3) <see cref="JsonNode.ToJsonString"/> with caller-supplied <see cref="JsonSerializerOptions"/> that have no
/// <see cref="JsonSerializerOptions.TypeInfoResolver"/> (a fresh <c>new JsonSerializerOptions { ... }</c>) throws
/// <see cref="InvalidOperationException"/> on a tree with a secret: the runtime makes the options read-only before
/// consulting any converter, and a customized leaf cannot opt out. Use <see cref="RevealingOptions"/>, options whose
/// resolver is set, or <c>JsonSerializer.Serialize&lt;JsonNode&gt;(tree, options)</c> (which populates the
/// resolver); plain <see cref="JsonNode.ToJsonString"/> without options always writes <c>***</c>.
/// </para>
/// </summary>
public static class SecretJson
{
    /// <summary>
    /// Serializer options that reveal secrets. Use only where the real value must be written
    /// (Graph request bodies, <c>--reveal-secure-outputs</c>).
    /// </summary>
    public static JsonSerializerOptions RevealingOptions { get; } = CreateRevealingOptions();

    /// <summary>Creates a tainted JSON leaf.</summary>
    public static JsonValue Create(SecretValue secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        return JsonValue.Create(secret, SecretJsonContext.Default.SecretValue)!;
    }

    /// <summary>Creates a tainted JSON string leaf.</summary>
    public static JsonValue CreateString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Create(new SecretValue(value, JsonValueKind.String));
    }

    /// <summary>True when <paramref name="node"/> is a tainted leaf.</summary>
    public static bool IsSecret(JsonNode? node, [NotNullWhen(true)] out SecretValue? secret)
    {
        secret = null;
        if (node is JsonValue value && value.TryGetValue<SecretValue>(out var found) && found is not null)
        {
            secret = found;
            return true;
        }

        return false;
    }

    /// <summary>True when <paramref name="node"/> is a tainted leaf.</summary>
    public static bool IsSecret(JsonNode? node)
    {
        return IsSecret(node, out _);
    }

    /// <summary>True when any leaf under <paramref name="node"/> is tainted.</summary>
    public static bool ContainsSecret(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return false;
            case JsonObject obj:
                foreach (var property in obj)
                {
                    if (ContainsSecret(property.Value))
                    {
                        return true;
                    }
                }

                return false;
            case JsonArray array:
                foreach (var item in array)
                {
                    if (ContainsSecret(item))
                    {
                        return true;
                    }
                }

                return false;
            default:
                return IsSecret(node);
        }
    }

    /// <summary>
    /// Deep-copies a tree preserving tainted leaves. Use this instead of <see cref="JsonNode.DeepClone"/> for any
    /// tree that may contain secrets (see the type remarks).
    /// </summary>
    public static JsonNode? Clone(JsonNode? node)
    {
        return Transform(node, static leaf => IsSecret(leaf, out var secret) ? Create(secret) : leaf?.DeepClone());
    }

    /// <summary>
    /// Returns a deep copy in which every leaf is tainted. <c>null</c> leaves stay <c>null</c>.
    /// </summary>
    public static JsonNode? Taint(JsonNode? node)
    {
        return Transform(node, static leaf => leaf is null ? null : Create(SecretValue.FromLeaf(leaf)!));
    }

    /// <summary>Returns a deep copy with every tainted leaf replaced by its real value.</summary>
    public static JsonNode? Reveal(JsonNode? node)
    {
        return Transform(node, static leaf => IsSecret(leaf, out var secret) ? secret.ToRevealedNode() : leaf?.DeepClone());
    }

    /// <summary>Returns a deep copy with every tainted leaf replaced by the plain string <c>***</c>.</summary>
    public static JsonNode? Redact(JsonNode? node)
    {
        return Transform(node, static leaf => IsSecret(leaf) ? JsonValue.Create(SecretValue.Mask) : leaf?.DeepClone());
    }

    private static JsonNode? Transform(JsonNode? node, Func<JsonNode?, JsonNode?> leaf)
    {
        switch (node)
        {
            case JsonObject obj:
                {
                    var copy = new JsonObject();
                    foreach (var property in obj)
                    {
                        copy[property.Key] = Transform(property.Value, leaf);
                    }

                    return copy;
                }

            case JsonArray array:
                {
                    var copy = new JsonArray();
                    foreach (var item in array)
                    {
                        copy.Add(Transform(item, leaf));
                    }

                    return copy;
                }

            default:
                return leaf(node);
        }
    }

    private static JsonSerializerOptions CreateRevealingOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.General);
        options.Converters.Add(new RevealingJsonConverter());

        // A tainted leaf is a customized JsonValue: JsonNode.WriteTo(writer, options) makes foreign options read-only
        // without populating a resolver, so the options must carry one from the start or ToJsonString(RevealingOptions)
        // throws until some unrelated JsonSerializer call has configured the instance. The source-generated context
        // serves SecretValue (honouring the options-level revealing converter); reflection serves everything else.
        options.TypeInfoResolver = JsonTypeInfoResolver.Combine(SecretJsonContext.Default, new DefaultJsonTypeInfoResolver());
        options.MakeReadOnly();
        return options;
    }
}
