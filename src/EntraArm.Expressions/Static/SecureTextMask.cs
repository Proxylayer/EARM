using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using EntraArm.Core.Diagnostics;
using EntraArm.Core.Secrets;
using EntraArm.Expressions.Evaluation;
using EntraArm.Expressions.Values;

namespace EntraArm.Expressions.Static;

/// <summary>
/// Defence in depth for diagnostics, not the primary protection. Messages and unknown symbols are built from redacted
/// forms (<see cref="Functions.FunctionCall.Redact(int, string)"/> and friends), so by construction none carries
/// secure text. Should a path be missed, any literal occurrence of a secure parameter's string value is still masked
/// here before the diagnostics leave the static layer.
/// <para>
/// Only string secrets of at least <see cref="MinLength"/> characters are considered: a boolean or numeric leaf of a
/// <c>secureObject</c> (<c>true</c>, <c>42</c>) would collide with ordinary message text, and so would a trivially
/// short string. Secrets that are only known later (a <c>reference()</c> result at deployment) are outside its reach;
/// they are protected by the redaction rule alone.
/// </para>
/// </summary>
internal static class SecureTextMask
{
    internal const int MinLength = 3;

    /// <summary>Replaces, in place, every occurrence of a known secure parameter value in the messages of <paramref name="diagnostics"/>.</summary>
    public static void Apply(LazyParameters parameters, List<Diagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(diagnostics);

        var secrets = CollectSecrets(parameters);
        if (secrets.Count == 0)
        {
            return;
        }

        for (int i = 0; i < diagnostics.Count; i++)
        {
            string message = diagnostics[i].Message;
            string masked = message;
            foreach (var secret in secrets)
            {
                masked = masked.Replace(secret, SecretValue.Mask, StringComparison.Ordinal);
            }

            if (!string.Equals(masked, message, StringComparison.Ordinal))
            {
                diagnostics[i] = diagnostics[i] with { Message = masked };
            }
        }
    }

    private static List<string> CollectSecrets(LazyParameters parameters)
    {
        var secrets = new List<string>();
        foreach (var name in parameters.Keys)
        {
            if (!parameters.TryGetBound(name, out var bound) || !bound.IsSecure)
            {
                continue;
            }

            JsonNode? value;
            try
            {
                // Memoised: a secure default has already been evaluated (and tainted) by the static evaluator; one that
                // failed to evaluate has been reported and has no value to mask.
                value = parameters.Get(name);
            }
            catch (ExpressionException)
            {
                continue;
            }

            UnknownJson.Visit(value, leaf =>
            {
                if (SecretJson.IsSecret(leaf, out var secret) && secret.Kind == JsonValueKind.String && secret.Value.Length >= MinLength)
                {
                    secrets.Add(secret.Value);
                }
            });
        }

        return secrets;
    }
}
