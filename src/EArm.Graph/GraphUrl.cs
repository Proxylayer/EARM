using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace EArm.Graph;

/// <summary>
/// URL construction and the origin check. Every URL the client sends a bearer token to goes through
/// <see cref="EnsureAllowed"/>: scheme <c>https</c>, host exactly the cloud's Graph host, no userinfo, the default port and a
/// path under <c>/v1.0/</c> or <c>/beta/</c>. This is the guard against token exfiltration through a hostile
/// <c>@odata.nextLink</c> (or a path value that tries to climb out of the API root).
/// </summary>
internal static class GraphUrl
{
    /// <summary>The absolute URI of a request: <c>{origin}/{v1.0|beta}{path}?{query}</c>, validated.</summary>
    public static Uri Build(GraphCloud cloud, GraphRequest request)
    {
        ArgumentNullException.ThrowIfNull(cloud);
        ArgumentNullException.ThrowIfNull(request);

        var text = new StringBuilder(request.Api.BaseUrl(cloud)).Append(request.Path);
        var query = BuildQuery(request.UrlParameters);
        if (query.Length > 0)
        {
            text.Append('?').Append(query);
        }

        if (!Uri.TryCreate(text.ToString(), UriKind.Absolute, out var uri))
        {
            throw new GraphTransportException(GraphTransportFailure.UnsafeUrl, $"The request path '{Truncate(request.Path)}' does not form a valid URL.");
        }

        EnsureAllowed(cloud, uri, "request");
        EnsurePathIntact(uri, "request");

        // The path must still sit under the API version that was asked for (a collapsed "/v1.0/x/../beta/y" would not).
        var apiPrefix = "/" + request.Api.Segment() + "/";
        if (!uri.AbsolutePath.StartsWith(apiPrefix, StringComparison.Ordinal))
        {
            throw new GraphTransportException(GraphTransportFailure.UnsafeUrl, $"The request path '{Truncate(request.Path)}' does not stay under {apiPrefix} after URL normalisation; nothing was sent.");
        }

        return uri;
    }

    /// <summary>
    /// Fail-closed check that <see cref="Uri"/> did not rewrite the path it was given: no <c>.</c> / <c>..</c> segment (raw or percent-encoded:
    /// <see cref="Uri"/> collapses both), and the normalised path has the same segments as the original once each is percent-decoded
    /// (escape normalisation such as <c>%41</c> to <c>A</c> or hex case is the only difference allowed).
    /// </summary>
    internal static void EnsurePathIntact(Uri uri, string what)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var originalSegments = PathOf(uri.OriginalString).Split('/');
        var normalisedSegments = uri.AbsolutePath.Split('/');
        var intact = originalSegments.Length == normalisedSegments.Length;
        for (var i = 0; intact && i < originalSegments.Length; i++)
        {
            var decoded = SafeUnescape(originalSegments[i]);
            if (decoded is "." or ".." || !string.Equals(decoded, SafeUnescape(normalisedSegments[i]), StringComparison.Ordinal))
            {
                intact = false;
            }
        }

        if (!intact)
        {
            throw new GraphTransportException(GraphTransportFailure.UnsafeUrl, $"Refusing the {what} URL: its path contains a '.' or '..' segment (or was rewritten by URL normalisation), which could address a different resource. Nothing was sent.");
        }
    }

    /// <summary>True when a path (without query) has a <c>.</c> or <c>..</c> segment, raw or percent-encoded.</summary>
    internal static bool HasDotSegment(string path)
    {
        foreach (var segment in path.Split('/'))
        {
            if (SafeUnescape(segment) is "." or "..")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The path part of an absolute URL string: after the authority, before <c>?</c> or <c>#</c>.</summary>
    private static string PathOf(string url)
    {
        var start = url.IndexOf("://", StringComparison.Ordinal);
        start = start < 0 ? 0 : url.IndexOf('/', start + 3);
        if (start < 0)
        {
            return "/";
        }

        var end = url.IndexOfAny(['?', '#'], start);
        return end < 0 ? url[start..] : url[start..end];
    }

    private static string PathOnly(string url)
    {
        var end = url.IndexOfAny(['?', '#']);
        return end < 0 ? url : url[..end];
    }

    /// <summary>
    /// The query string (without <c>?</c>): names keep a literal <c>$</c>, every name and value is percent-encoded as UTF-8
    /// with everything but the RFC 3986 unreserved characters (<c>A-Z a-z 0-9 - . _ ~</c>) escaped, so <c>&amp;</c>, <c>=</c>,
    /// <c>+</c>, <c>%</c>, quotes, spaces and non-ASCII text round-trip exactly. A lone surrogate becomes U+FFFD.
    /// </summary>
    public static string BuildQuery(IReadOnlyDictionary<string, string>? parameters)
    {
        if (parameters is null || parameters.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var pair in parameters)
        {
            if (builder.Length > 0)
            {
                builder.Append('&');
            }

            builder.Append(EncodeName(pair.Key)).Append('=').Append(EncodeValue(pair.Value));
        }

        return builder.ToString();
    }

    public static string EncodeValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Uri.EscapeDataString(ReplaceLoneSurrogates(value));
    }

    public static string EncodeName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Uri.EscapeDataString(ReplaceLoneSurrogates(name)).Replace("%24", "$", StringComparison.Ordinal);
    }

    /// <summary>Parses and validates an absolute <c>@odata.nextLink</c>.</summary>
    /// <exception cref="GraphTransportException">The link is not an https URL on the Graph origin.</exception>
    public static Uri ParseNextLink(GraphCloud cloud, string nextLink)
    {
        ArgumentNullException.ThrowIfNull(cloud);
        ArgumentException.ThrowIfNullOrWhiteSpace(nextLink);
        if (!Uri.TryCreate(nextLink, UriKind.Absolute, out var uri))
        {
            throw new GraphTransportException(GraphTransportFailure.UnsafeUrl, "Refusing to follow @odata.nextLink: it is not an absolute URL.");
        }

        EnsureAllowed(cloud, uri, "@odata.nextLink");
        EnsurePathIntact(uri, "@odata.nextLink");
        return uri;
    }

    /// <summary>A <c>$batch</c> sub-request URL must be relative to the API root: <c>/path?query</c>, never absolute or protocol-relative.</summary>
    public static void ValidateBatchUrl(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        var ok = url.Length > 1
            && url[0] == '/'
            && url[1] != '/'
            && url[1] != '\\'
            && !url.Contains("://", StringComparison.Ordinal)
            && !url.Contains('\\', StringComparison.Ordinal)
            && !HasControlCharacter(url)
            && !HasDotSegment(PathOnly(url));
        if (!ok)
        {
            throw new GraphTransportException(GraphTransportFailure.UnsafeUrl, $"Refusing a $batch sub-request URL that is not a path relative to the API root: '{Truncate(url)}'.");
        }
    }

    /// <summary>Throws <see cref="GraphTransportException"/> (<see cref="GraphTransportFailure.UnsafeUrl"/>) unless <paramref name="uri"/> is on the cloud's Graph origin.</summary>
    public static void EnsureAllowed(GraphCloud cloud, Uri uri, string what)
    {
        var problem = Check(cloud, uri);
        if (problem is not null)
        {
            throw new GraphTransportException(GraphTransportFailure.UnsafeUrl, $"Refusing to send a request to '{DescribeForError(uri)}' ({what}): {problem}. Credentials are only ever sent to https://{cloud.GraphHost}/.");
        }
    }

    /// <summary>The reason a URI is not allowed, or null when it is.</summary>
    public static string? Check(GraphCloud cloud, Uri uri)
    {
        ArgumentNullException.ThrowIfNull(cloud);
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri)
        {
            return "the URL is not absolute";
        }

        var loopback = cloud.LoopbackPort != 0;
        if (!string.Equals(uri.Scheme, loopback ? Uri.UriSchemeHttp : Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            return loopback ? "the scheme is not http (loopback test cloud)" : "the scheme is not https";
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return "the URL carries user information";
        }

        if (uri.HostNameType != (loopback ? UriHostNameType.IPv4 : UriHostNameType.Dns) || !string.Equals(uri.Host, cloud.GraphHost, StringComparison.OrdinalIgnoreCase))
        {
            return "the host is not " + cloud.GraphHost;
        }

        if (loopback ? uri.Port != cloud.LoopbackPort : !uri.IsDefaultPort)
        {
            return "the port is not the default https port";
        }

        if (uri.Fragment.Length > 0)
        {
            return "the URL carries a fragment";
        }

        var path = uri.AbsolutePath;
        if (!path.StartsWith("/v1.0/", StringComparison.OrdinalIgnoreCase) && !path.StartsWith("/beta/", StringComparison.OrdinalIgnoreCase))
        {
            return "the path is not under /v1.0/ or /beta/";
        }

        return null;
    }

    /// <summary><c>/path?query</c> with the values of secret-looking parameters masked (paging tokens are not secrets). For logs only.</summary>
    public static string RedactForLog(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var query = uri.Query;
        if (query.Length <= 1)
        {
            return uri.AbsolutePath;
        }

        var builder = new StringBuilder(uri.AbsolutePath).Append('?');
        var first = true;
        foreach (var part in query[1..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!first)
            {
                builder.Append('&');
            }

            first = false;
            var separator = part.IndexOf('=', StringComparison.Ordinal);
            if (separator < 0)
            {
                builder.Append(part);
                continue;
            }

            var rawName = part[..separator];
            var name = SafeUnescape(rawName);
            builder.Append(rawName).Append('=').Append(SecretRedactor.IsSecretName(name) ? SecretRedactor.Mask : SecretRedactor.RedactText(part[(separator + 1)..]));
        }

        return builder.ToString();
    }

    /// <summary>A URL for an error message: scheme, host and port only, never the query (it may carry tokens).</summary>
    public static string DescribeForError(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return uri.IsAbsoluteUri ? uri.Scheme + "://" + uri.Host + (uri.IsDefaultPort ? string.Empty : ":" + uri.Port.ToString(CultureInfo.InvariantCulture)) : "(relative)";
    }

    private static string SafeUnescape(string text)
    {
        try
        {
            return Uri.UnescapeDataString(text);
        }
        catch (UriFormatException)
        {
            return text;
        }
    }

    private static bool HasControlCharacter(string text)
    {
        foreach (var c in text)
        {
            if (char.IsControl(c))
            {
                return true;
            }
        }

        return false;
    }

    private static string Truncate(string text)
    {
        var cleaned = new StringBuilder();
        foreach (var c in text)
        {
            cleaned.Append(char.IsControl(c) ? '?' : c);
        }

        var result = cleaned.ToString();
        return result.Length <= 80 ? result : result[..80] + "...";
    }

    private static string ReplaceLoneSurrogates(string value)
    {
        StringBuilder? builder = null;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            var lone = false;
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                {
                    builder?.Append(c).Append(value[i + 1]);
                    i++;
                    continue;
                }

                lone = true;
            }
            else if (char.IsLowSurrogate(c))
            {
                lone = true;
            }

            if (lone && builder is null)
            {
                builder = new StringBuilder(value.Length).Append(value, 0, i);
            }

            builder?.Append(lone ? '�' : c);
        }

        return builder?.ToString() ?? value;
    }
}
