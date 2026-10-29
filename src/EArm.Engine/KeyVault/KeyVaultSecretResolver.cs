using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core;
using EArm.Core.Secrets;
using EArm.Core.Validation;
using EArm.Graph;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EArm.Engine.KeyVault;

/// <summary>A Key Vault parameter reference could not be resolved (invalid name, vault/secret missing, no access, sign-in failure).</summary>
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

    /// <summary>The failure, redacted.</summary>
    public string Detail { get; }
}

/// <summary>
/// Resolves parameter-file Key Vault references over HTTP with a Key Vault token of the signed-in identity
/// (<see cref="KeyVaultClient"/>: <c>GET https://{vault}.vault.azure.net/secrets/{name}[/{version}]?api-version=7.4</c>). The vault, secret and version
/// are validated before any URL is built. Every resolved value is secure-tainted (<see cref="SecretJson"/>) whatever the parameter's declared type.
/// <c>deployment validate</c> stays offline and never calls this.
/// </summary>
public sealed class KeyVaultSecretResolver
{
    /// <summary>The most secrets <see cref="ResolveAsync"/> reads at the same time.</summary>
    internal const int MaxConcurrentReads = 4;

    private readonly KeyVaultClient _client;
    private readonly ILogger _logger;

    public KeyVaultSecretResolver(KeyVaultClient client, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
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

        // The secrets are read concurrently (at most MaxConcurrentReads at once), then taken in parameter-name order: the first failure reported, the
        // constraint checks and the log lines are those of a one-by-one read. Every read is finished (or failed) before anything is reported, so
        // none is left running or unobserved.
        var pending = document.ParameterValues.Values.Where(b => b.PendingReference is not null).OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToList();
        using var throttle = new SemaphoreSlim(MaxConcurrentReads);
        var reads = pending.Select(bound => ReadThrottledAsync(throttle, bound.Name, bound.PendingReference!, cancellationToken)).ToList();
        await ((Task)Task.WhenAll(reads)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        for (int i = 0; i < pending.Count; i++)
        {
            var bound = pending[i];
            var reference = bound.PendingReference!;
            string secret = await reads[i].ConfigureAwait(false);
            SecretScrubber.Register(secret, secureDeclared: true);
            var value = SecretJson.CreateString(secret);

            // allowedValues / minValue / maxValue / minLength / maxLength apply to the resolved secret as they do to a supplied value; the message names the parameter
            // and the constraint, never the value.
            if (ParameterConstraints.Violation(bound.Definition, value) is { } violation)
            {
                throw new KeyVaultResolutionException(bound.Name, reference, "the resolved value is not valid: " + violation + ".");
            }

            values[bound.Name] = bound with
            {
                Value = value,
                PendingReference = null,
            };
            EngineLog.KeyVaultResolved(_logger, bound.Name, reference.ToString());
        }

        return document with { ParameterValues = values };
    }

    private async Task<string> ReadThrottledAsync(SemaphoreSlim throttle, string parameterName, KeyVaultReference reference, CancellationToken cancellationToken)
    {
        await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await GetSecretAsync(parameterName, reference, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            throttle.Release();
        }
    }

    /// <summary>Reads one secret's value. The returned string is the raw secret: wrap it (<see cref="SecretJson.CreateString"/>) before it travels anywhere.</summary>
    public async Task<string> GetSecretAsync(string parameterName, KeyVaultReference reference, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameterName);
        ArgumentNullException.ThrowIfNull(reference);

        try
        {
            return await _client.GetSecretAsync(reference.VaultName, reference.SecretName, reference.SecretVersion, cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            // Names that break the Key Vault rules never reach the network.
            throw new KeyVaultResolutionException(parameterName, reference, ex.Message, ex);
        }
        catch (GraphException ex)
        {
            throw new KeyVaultResolutionException(parameterName, reference, Describe(ex), ex);
        }
        catch (GraphTransportException ex)
        {
            throw new KeyVaultResolutionException(parameterName, reference, ex.Message, ex);
        }
    }

    private static string Describe(GraphException ex)
    {
        var detail = ex.Status switch
        {
            System.Net.HttpStatusCode.Forbidden => "access denied. The signed-in identity needs 'Key Vault Secrets User' (or the get permission on secrets) on this vault, and the vault's firewall must allow this machine.",
            System.Net.HttpStatusCode.NotFound => "the vault has no such secret (or no such version).",
            System.Net.HttpStatusCode.Unauthorized => "the Key Vault token was rejected; sign in again.",
            _ => $"Key Vault answered {(int)ex.Status}.",
        };
        var code = string.IsNullOrEmpty(ex.Code) ? string.Empty : $" ({ex.Code})";

        // GraphMessage is already redacted; keep the first line only (Key Vault appends the caller's identity details on later lines).
        var message = ex.GraphMessage.Split('\n', 2)[0].Trim();
        if (message.Length > 300)
        {
            message = message[..300] + "...";
        }

        return detail + code + (message.Length == 0 ? string.Empty : " " + message);
    }
}