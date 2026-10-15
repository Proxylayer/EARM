using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using EArm.Core;
using EArm.Core.Secrets;
using EArm.Graph;

namespace EArm.Engine.Execution;

/// <summary>
/// Feeds <see cref="SecretScrubber"/> with every secret value the engine learns (review finding S-02): secure parameter values (inline,
/// from a file, resolved from Key Vault), tainted properties of evaluated resources and secrets a provider hands back. A Graph error
/// that echoes a request body then reaches logs, state and the console with the value already masked.
/// <para>
/// <see cref="SecretScrubber"/> keeps its registry in an async-local holder: the CLI (or a test) calls <see cref="SecretScrubber.Begin"/> at the
/// top of the run so the holder is shared by the planner, the executor and their parallel work.
/// </para>
/// </summary>
public static class SecretRegistration
{
    /// <summary>Registers the value of every secure parameter of the document.</summary>
    public static void RegisterDocument(TemplateDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        foreach (var bound in document.ParameterValues.Values)
        {
            if (!bound.IsSecure || bound.IsMissing)
            {
                continue;
            }

            RegisterAllLeaves(bound.Value);
        }
    }

    /// <summary>Registers every tainted leaf under <paramref name="node"/> as a DERIVED value (<c>secureDeclared: false</c>).</summary>
    public static void RegisterTainted(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return;
            case JsonObject obj:
                foreach (var property in obj)
                {
                    RegisterTainted(property.Value);
                }

                return;
            case JsonArray array:
                foreach (var item in array)
                {
                    RegisterTainted(item);
                }

                return;
            default:
                if (SecretJson.IsSecret(node, out var secret) && secret.Kind == JsonValueKind.String)
                {
                    // A derived tainted leaf (the first letter of a password, a short fragment): not declared secure, so a short one is masked on word boundaries only.
                    SecretScrubber.Register(secret.Value, secureDeclared: false);
                }

                return;
        }
    }

    /// <summary>Registers every leaf under <paramref name="node"/> (the whole value of a secure parameter is secret, tainted or not).</summary>
    public static void RegisterAllLeaves(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return;
            case JsonObject obj:
                foreach (var property in obj)
                {
                    RegisterAllLeaves(property.Value);
                }

                return;
            case JsonArray array:
                foreach (var item in array)
                {
                    RegisterAllLeaves(item);
                }

                return;
            default:
                if (SecretJson.IsSecret(node, out var secret))
                {
                    // Numbers and booleans are not registered: masking "true" or "42" would shred every message.
                    if (secret.Kind == JsonValueKind.String)
                    {
                        SecretScrubber.Register(secret.Value, secureDeclared: true);
                    }
                }
                else if (node is JsonValue value && value.TryGetValue<string>(out string? text))
                {
                    SecretScrubber.Register(text, secureDeclared: true);
                }

                return;
        }
    }

    /// <summary>The text with every registered secret masked.</summary>
    public static string Scrub(string? text)
    {
        return SecretScrubber.Scrub(text);
    }
}
