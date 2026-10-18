using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph.Auth;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EArm.Graph;

/// <summary>
/// Reads one secret from the Key Vault data plane: <c>GET https://{vault}.{suffix}/secrets/{name}[/{version}]?api-version=7.4</c> with a bearer
/// token for the cloud's Key Vault scope. The vault and secret names are validated against the documented Key Vault rules BEFORE the URL is built, so a
/// parameter file cannot steer the request to another host or path; the host is always <c>{vault}.{suffix}</c> from the fixed cloud table. Redirects are
/// never followed. The secret value is only ever returned to the caller: it is not logged and appears in no exception message.
/// </summary>
public sealed class KeyVaultClient
{
    public const string ApiVersion = "7.4";

    /// <summary>
    /// The environment variable that restricts which vaults a parameter file may read: a comma-separated list of vault names (compared case-insensitively).
    /// When set, a reference to any other vault is refused before a token is requested or a request is built.
    /// </summary>
    public const string AllowedVaultsVariable = "EARM_KEYVAULT_ALLOWED";

    /// <summary>The largest response body read, in bytes: a secret is at most 25 KiB, so 1 MiB is generous and a hostile endpoint cannot stream without end.</summary>
    public const long MaxResponseBytes = 1_048_576;

    private readonly HttpClient _http;
    private readonly IGraphTokenProvider _tokens;
    private readonly RetryPolicy _retryPolicy;
    private readonly GraphCloud _cloud;
    private readonly TimeSpan _timeout;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly string _userAgent;
    private readonly TokenScopes _scopes;

    /// <param name="scopes">The Key Vault token scopes. <c>null</c> means <paramref name="cloud"/>'s <see cref="GraphCloud.KeyVaultScopes"/> (<c>.default</c>); explicit scopes must belong to the cloud's vault resource.</param>
    public KeyVaultClient(HttpClient http, IGraphTokenProvider tokens, RetryPolicy retryPolicy, GraphCloud cloud, TimeSpan? timeout = null, ILogger? logger = null, TimeProvider? timeProvider = null, string? userAgentVersion = null, TokenScopes? scopes = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(retryPolicy);
        ArgumentNullException.ThrowIfNull(cloud);
        _http = http;
        _tokens = tokens;
        _retryPolicy = retryPolicy;
        _cloud = cloud;
        _timeout = timeout ?? TimeSpan.FromSeconds(60);
        _logger = logger ?? NullLogger.Instance;
        _time = timeProvider ?? TimeProvider.System;
        _userAgent = "EARM/" + (userAgentVersion ?? GraphVersion.Current);
        AllowedVaults = ParseAllowedVaults(Environment.GetEnvironmentVariable(AllowedVaultsVariable));
        _scopes = TokenScopeGuard.Resolve(scopes, cloud.KeyVaultScopes, cloud, nameof(scopes));
    }

    /// <summary>The scopes of the Key Vault token this client attaches.</summary>
    public TokenScopes Scopes => _scopes;

    /// <summary>
    /// The vault names this client may read (<see cref="AllowedVaultsVariable"/>, read when the client is created); <c>null</c> means every vault. A configured but
    /// empty list (for example <c>,</c>) allows none. Settable for tests.
    /// </summary>
    public IReadOnlyList<string>? AllowedVaults { get; init; }

    /// <summary>The names of a comma-separated allow list; <c>null</c> for a missing or blank setting (no restriction).</summary>
    public static IReadOnlyList<string>? ParseAllowedVaults(string? setting)
    {
        if (string.IsNullOrWhiteSpace(setting))
        {
            return null;
        }

        return setting.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>A vault name: 3-24 characters, letters, digits and hyphens, starting with a letter, ending with a letter or digit, no two hyphens in a row.</summary>
    public static bool IsValidVaultName(string? name)
    {
        if (name is null || name.Length is < 3 or > 24 || !char.IsAsciiLetter(name[0]) || !char.IsAsciiLetterOrDigit(name[^1]))
        {
            return false;
        }

        var previousHyphen = false;
        foreach (var c in name)
        {
            if (c == '-')
            {
                if (previousHyphen)
                {
                    return false;
                }

                previousHyphen = true;
            }
            else if (char.IsAsciiLetterOrDigit(c))
            {
                previousHyphen = false;
            }
            else
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A secret name: 1-127 characters, letters, digits and hyphens.</summary>
    public static bool IsValidSecretName(string? name)
    {
        if (name is null || name.Length is < 1 or > 127)
        {
            return false;
        }

        foreach (var c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A secret version: the 32 hexadecimal characters Key Vault generates.</summary>
    public static bool IsValidVersion(string? version)
    {
        if (version is null || version.Length != 32)
        {
            return false;
        }

        foreach (var c in version)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The validated secret URL.</summary>
    /// <exception cref="ArgumentException">The vault name, secret name or version breaks the Key Vault naming rules.</exception>
    public Uri BuildSecretUri(string vaultName, string secretName, string? version)
    {
        if (!IsValidVaultName(vaultName))
        {
            throw new ArgumentException($"'{Shorten(vaultName)}' is not a valid Key Vault name (3-24 characters: letters, digits and hyphens; starts with a letter, ends with a letter or digit; no consecutive hyphens).", nameof(vaultName));
        }

        if (!IsValidSecretName(secretName))
        {
            throw new ArgumentException($"'{Shorten(secretName)}' is not a valid Key Vault secret name (1-127 characters: letters, digits and hyphens).", nameof(secretName));
        }

        if (version is not null && !IsValidVersion(version))
        {
            throw new ArgumentException($"'{Shorten(version)}' is not a valid Key Vault secret version (32 hexadecimal characters).", nameof(version));
        }

        var host = vaultName.ToLowerInvariant() + "." + _cloud.KeyVaultSuffix;
        var path = "/secrets/" + secretName + (version is null ? string.Empty : "/" + version);
        var uri = new Uri("https://" + host + path + "?api-version=" + ApiVersion, UriKind.Absolute);
        if (!string.Equals(uri.Host, host, StringComparison.Ordinal) || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("The Key Vault URL did not keep the expected origin.", nameof(vaultName));
        }

        return uri;
    }

    /// <summary>Reads the secret's value. The returned string is the raw secret: wrap it before it travels anywhere.</summary>
    /// <exception cref="ArgumentException">An invalid vault name, secret name or version (nothing is sent).</exception>
    /// <exception cref="GraphException">Key Vault answered with an error status (403 access denied, 404 no such vault secret, ...).</exception>
    /// <exception cref="GraphTransportException">No token, the vault host could not be reached, the call timed out, or the answer was unusable.</exception>
    public async Task<string> GetSecretAsync(string vaultName, string secretName, string? version, CancellationToken cancellationToken)
    {
        var uri = BuildSecretUri(vaultName, secretName, version);
        if (AllowedVaults is { } allowed && !allowed.Contains(vaultName, StringComparer.OrdinalIgnoreCase))
        {
            // Before any token is requested or request is sent.
            throw new ArgumentException($"Key Vault '{Shorten(vaultName)}' is not allowed: {AllowedVaultsVariable} permits only {(allowed.Count == 0 ? "no vault" : string.Join(", ", allowed.Select(Shorten)))}.", nameof(vaultName));
        }

        var scope = _scopes;
        return await _retryPolicy.ExecuteAsync(
            token => SendOnceAsync(uri, scope, token),
            RetryHint.Default,
            _ =>
            {
                _tokens.Invalidate(scope);
                return Task.CompletedTask;
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> SendOnceAsync(Uri uri, TokenScopes scope, CancellationToken cancellationToken)
    {
        var clientRequestId = Guid.NewGuid().ToString();
        var graphToken = await _tokens.GetTokenAsync(scope, cancellationToken).ConfigureAwait(false);
        var stopwatch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        using var message = new HttpRequestMessage(HttpMethod.Get, uri);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", graphToken.Value);
        message.Headers.TryAddWithoutValidation("Accept", "application/json");
        message.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
        message.Headers.TryAddWithoutValidation("x-ms-client-request-id", clientRequestId);

        var target = uri.AbsolutePath;
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            GraphLog.GraphTransportFailed(_logger, "GET", target, stopwatch.ElapsedMilliseconds, "timeout", clientRequestId);
            throw new GraphTransportException(GraphTransportFailure.Timeout, $"Key Vault {uri.Host} did not answer within {_timeout.TotalSeconds:F0} s.");
        }
        catch (HttpRequestException ex)
        {
            GraphLog.GraphTransportFailed(_logger, "GET", target, stopwatch.ElapsedMilliseconds, ex.GetType().Name, clientRequestId);
            throw new GraphTransportException(GraphTransportFailure.Network, $"Key Vault {uri.Host} could not be reached ({ex.InnerException?.Message ?? ex.Message}). Check the vault name and the cloud ({_cloud.Key}).", ex);
        }

        using (response)
        {
            byte[] bytes;
            try
            {
                bytes = await BoundedContent.ReadAsync(
                    response.Content,
                    MaxResponseBytes,
                    () => new GraphTransportException(GraphTransportFailure.ResponseTooLarge, $"Key Vault {uri.Host} answered with more than {MaxResponseBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)} bytes (after decompression); the response was discarded."),
                    timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new GraphTransportException(GraphTransportFailure.Timeout, $"Key Vault {uri.Host} did not answer within {_timeout.TotalSeconds:F0} s.");
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                throw new GraphTransportException(GraphTransportFailure.Network, $"Key Vault {uri.Host} dropped the connection ({ex.Message}).", ex);
            }

            var status = response.StatusCode;
            GraphLog.GraphResponse(_logger, "GET", target, (int)status, stopwatch.ElapsedMilliseconds, null, clientRequestId);
            var code = (int)status;
            if (code is >= 200 and < 300)
            {
                return ParseValue(bytes);
            }

            // The error body of a failed call carries no secret; a successful body is never put into a message.
            var text = Encoding.UTF8.GetString(bytes);
            if (code is >= 300 and < 400)
            {
                throw new GraphException(status, "UnexpectedRedirect", $"Key Vault answered with a redirect ({code}); redirects are never followed.", RetryHint.Default);
            }

            throw GraphErrorParser.FromResponse(status, text, response.Headers, clientRequestId, RetryHint.Default, _time);
        }
    }

    private static string ParseValue(byte[] bytes)
    {
        try
        {
            var text = Encoding.UTF8.GetString(bytes);
            if (JsonNode.Parse(JsonSurrogateSanitizer.Replace(text)) is JsonObject obj && obj["value"] is JsonValue value && value.TryGetValue<string>(out var secret))
            {
                return secret;
            }
        }
        catch (JsonException)
        {
            // Fall through: the message must not echo the body.
        }

        throw new GraphTransportException(GraphTransportFailure.InvalidResponse, "Key Vault answered without a secret value (the secret may be disabled, expired or not a string).");
    }

    private static string Shorten(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var cleaned = new StringBuilder();
        foreach (var c in text.Length > 40 ? text[..40] : text)
        {
            cleaned.Append(char.IsControl(c) ? '?' : c);
        }

        return text.Length > 40 ? cleaned + "..." : cleaned.ToString();
    }
}
