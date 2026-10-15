using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using EArm.Core.Json;
using System.Threading;
using System.Threading.Tasks;
using EArm.Az;
using EArm.Core;
using EArm.Core.Secrets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EArm.Engine.KeyVault;

/// <summary>A Key Vault parameter reference could not be resolved (vault/secret missing, no access, az failure).</summary>
public sealed class KeyVaultResolutionException : Exception
{
    public KeyVaultResolutionException(string parameterName, KeyVaultReference reference, string detail, Exception? inner = null)
        : base($"Parameter '{parameterName}' could not be resolved from {reference}: {detail}", inner)
    {
        ParameterName = parameterName;
        Reference = reference;
        Detail = detail;
    }

    public string Code => EngineDiagnosticCodes.KeyVaultReferenceUnresolved;

    public string ParameterName { get; }

    public KeyVaultReference Reference { get; }

    /// <summary>The az failure, redacted.</summary>
    public string Detail { get; }
}

/// <summary>
/// Resolves parameter-file Key Vault references (plan section 1.6) through the Azure CLI session:
/// <c>az keyvault secret show --vault-name &lt;vault&gt; --name &lt;secret&gt; [--version &lt;v&gt;] --query value</c>
/// (the runner always asks for JSON output, so the value arrives as a JSON string). Every resolved value is
/// secure-tainted (<see cref="SecretJson"/>) whatever the parameter's declared type. <c>deployment validate</c> stays
/// offline and never calls this.
/// </summary>
public sealed class KeyVaultSecretResolver
{
    private readonly IAzRunner _runner;
    private readonly ILogger _logger;

    public KeyVaultSecretResolver(IAzRunner runner, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(runner);
        _runner = runner;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>True when any bound parameter is still a Key Vault reference.</summary>
    public static bool HasPendingReferences(TemplateDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.ParameterValues.Values.Any(b => b.PendingReference is not null);
    }

    /// <summary>
    /// Returns a copy of the document in which every pending Key Vault reference is replaced by the secret's value
    /// (tainted). The original document is not modified.
    /// </summary>
    /// <exception cref="KeyVaultResolutionException">A secret could not be read.</exception>
    public async Task<TemplateDocument> ResolveAsync(TemplateDocument document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!HasPendingReferences(document))
        {
            return document;
        }

        var values = new Dictionary<string, BoundParameter>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in document.ParameterValues)
        {
            values[pair.Key] = pair.Value;
        }

        foreach (var bound in document.ParameterValues.Values.Where(b => b.PendingReference is not null).OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase))
        {
            var reference = bound.PendingReference!;
            string secret = await GetSecretAsync(bound.Name, reference, cancellationToken).ConfigureAwait(false);
            values[bound.Name] = bound with
            {
                Value = SecretJson.CreateString(secret),
                PendingReference = null,
            };
            EngineLog.KeyVaultResolved(_logger, bound.Name, reference.ToString());
        }

        return document with { ParameterValues = values };
    }

    /// <summary>Reads one secret's value. The returned string is the raw secret: wrap it (<see cref="SecretJson.CreateString"/>) before it travels anywhere.</summary>
    public async Task<string> GetSecretAsync(string parameterName, KeyVaultReference reference, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameterName);
        ArgumentNullException.ThrowIfNull(reference);

        var arguments = new List<string> { "--vault-name", reference.VaultName, "--name", reference.SecretName };
        if (reference.SecretVersion is not null)
        {
            arguments.Add("--version");
            arguments.Add(reference.SecretVersion);
        }

        arguments.Add("--query");
        arguments.Add("value");

        var request = AzRequest.ForCommand("keyvault", "secret", "show").WithArguments([.. arguments]);
        AzResult result;
        try
        {
            result = await _runner.RunAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (ToolchainException ex)
        {
            throw new KeyVaultResolutionException(parameterName, reference, ex.Message, ex);
        }

        if (!result.Succeeded)
        {
            var failure = AzCliException.FromResult(result, "az keyvault secret show");
            throw new KeyVaultResolutionException(parameterName, reference, failure.Message, failure);
        }

        return ParseValue(parameterName, reference, result.StandardOutput);
    }

    /// <summary>The secret from az's stdout: a JSON string (<c>-o json --query value</c>), or raw text as a fallback (<c>-o tsv</c>).</summary>
    internal static string ParseValue(string parameterName, KeyVaultReference reference, string standardOutput)
    {
        string text = standardOutput.Trim();
        if (text.Length == 0)
        {
            throw new KeyVaultResolutionException(parameterName, reference, "az returned no value (the secret may be disabled or empty).");
        }

        if (text.StartsWith('"'))
        {
            try
            {
                if (JsonNode.Parse(LoneSurrogates.Replace(text)) is JsonValue value && value.TryGetValue<string>(out var parsed))
                {
                    return parsed;
                }
            }
            catch (JsonException)
            {
                // Not JSON after all; treat as raw text below.
            }
        }

        if (text.StartsWith('{') || text.StartsWith('['))
        {
            throw new KeyVaultResolutionException(parameterName, reference, "az returned a JSON object instead of the secret value; expected the output of --query value.");
        }

        return text;
    }
}
