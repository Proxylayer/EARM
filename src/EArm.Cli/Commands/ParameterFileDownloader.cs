using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core.Diagnostics;
using EArm.Core.Loading;
using EArm.Graph;

namespace EArm.Cli.Commands;

/// <summary>
/// Fetches a parameter file named by <c>-p https://...</c> (as <c>az deployment ... --parameters https://mysite/params.json</c>) with the safety of the Key Vault
/// fetch (<see cref="KeyVaultClient"/>): HTTPS only (an <c>http://</c> URL is refused before anything is sent), no credentials of any kind (no token, no cookie;
/// a URL with a user name or password is refused), redirects are never followed (a redirect is an error naming its status), the answer is bounded to
/// <see cref="KeyVaultClient.MaxResponseBytes"/> after decompression and the whole request to <see cref="Timeout"/>. The query string of the URL (a SAS
/// token, for example) is sent but never echoed: messages and the reported parameter file name show the URL without it.
/// </summary>
internal sealed class ParameterFileDownloader
{
    /// <summary>The diagnostic code of a parameter file that could not be downloaded (CLI only).</summary>
    public const string DownloadFailedCode = CliDiagnosticCodes.ParameterDownloadFailed;

    /// <summary>The time one download may take (connection, headers and body), as the per-call timeout of the Graph and Key Vault requests.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    private readonly HttpMessageInvoker _http;

    public ParameterFileDownloader(HttpMessageInvoker http)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
    }

    /// <summary>The downloader of the process: a handler without redirects or cookies, shared by every download.</summary>
    public static ParameterFileDownloader Shared { get; } = new(new HttpMessageInvoker(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        ConnectTimeout = TimeSpan.FromSeconds(30),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    }));

    /// <summary>True when <paramref name="token"/> is a URL (<c>https://</c> or <c>http://</c>, any case), the form a <c>-p</c> value is downloaded from.</summary>
    public static bool IsUrl(string token)
    {
        return token.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || token.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The URL without its query string, fragment and user info: what messages and the parameter file name show.</summary>
    public static string Display(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        int cut = url.IndexOfAny(['?', '#']);
        string shown = cut < 0 ? url : url[..cut];
        int scheme = shown.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            int authorityEnd = shown.IndexOf('/', scheme + 3);
            string authority = authorityEnd < 0 ? shown[(scheme + 3)..] : shown[(scheme + 3)..authorityEnd];
            int at = authority.LastIndexOf('@');
            if (at >= 0)
            {
                shown = shown[..(scheme + 3)] + authority[(at + 1)..] + (authorityEnd < 0 ? string.Empty : shown[authorityEnd..]);
            }
        }

        return shown;
    }

    /// <summary>Checks <paramref name="url"/> and returns it as an absolute HTTPS URI; throws <see cref="ArgumentException"/> for anything else.</summary>
    public static Uri Validate(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException($"Parameter URL '{Display(url)}' is not a valid absolute URL.");
        }

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException($"Parameter URL '{Display(url)}' uses http; a parameter file is downloaded over https only.");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArgumentException($"Parameter URL '{Display(url)}' carries a user name or password; EARM sends no credentials with a parameter download.");
        }

        return uri;
    }

    /// <summary>Downloads <paramref name="uri"/> (already <see cref="Validate">validated</see>); throws <see cref="TemplateLoadException"/> when it cannot.</summary>
    public Task<byte[]> DownloadAsync(Uri uri, CancellationToken cancellationToken)
    {
        return DownloadAsync(uri, "parameter file", cancellationToken);
    }

    /// <summary>
    /// Downloads <paramref name="uri"/> with the same rules for another kind of file (<paramref name="what"/> names it in messages: a linked template of a
    /// nested deployment); throws <see cref="TemplateLoadException"/> when it cannot.
    /// </summary>
    public async Task<byte[]> DownloadAsync(Uri uri, string what, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentException.ThrowIfNullOrWhiteSpace(what);
        string shown = Display(uri.OriginalString);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("User-Agent", "earm/" + VersionInfo.InformationalVersion);
        try
        {
            using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            int status = (int)response.StatusCode;
            if (status is >= 300 and < 400)
            {
                throw Failed(what, shown, string.Create(CultureInfo.InvariantCulture, $"the server answered with a redirect ({status}); redirects are never followed. Use the final https URL."));
            }

            if (status is < 200 or >= 300)
            {
                throw Failed(what, shown, string.Create(CultureInfo.InvariantCulture, $"the server answered {status} ({response.ReasonPhrase})."));
            }

            return await ReadBoundedAsync(response.Content, what, shown, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Failed(what, shown, string.Create(CultureInfo.InvariantCulture, $"no answer within {Timeout.TotalSeconds:F0} s."));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw Failed(what, shown, $"the server could not be reached ({ex.InnerException?.Message ?? ex.Message}).");
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, string what, string shown, CancellationToken cancellationToken)
    {
        long max = KeyVaultClient.MaxResponseBytes;
        if (content.Headers.ContentLength is { } declared && declared > max)
        {
            throw TooLarge(what, shown, max);
        }

        var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            while (true)
            {
                int read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return buffer.ToArray();
                }

                if (buffer.Length + read > max)
                {
                    throw TooLarge(what, shown, max);
                }

                buffer.Write(chunk, 0, read);
            }
        }
    }

    private static TemplateLoadException Failed(string what, string shown, string why)
    {
        return new TemplateLoadException(DownloadFailedCode, $"The {what} {shown} could not be downloaded: {why}", shown);
    }

    private static TemplateLoadException TooLarge(string what, string shown, long max)
    {
        return new TemplateLoadException(DiagnosticCodes.InputTooLarge, string.Create(CultureInfo.InvariantCulture, $"The {what} {shown} is larger than {max:N0} bytes (the download limit); the answer was discarded."), shown);
    }
}
