using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core.Loading;

namespace EArm.Engine.Planning.Nested;

/// <summary>Where a linked template or parameter file is: a local file (full path) or an https URL.</summary>
/// <param name="Value">The full path, or the absolute URL including its query string.</param>
/// <param name="IsRemote">True for an https URL.</param>
public sealed record LinkedLocation(string Value, bool IsRemote)
{
    /// <summary>What messages show: the path, or the URL without its query string, fragment and user info (a SAS token is never echoed).</summary>
    public string Display => IsRemote ? LinkedTemplateReader.DisplayUrl(Value) : Value;

    /// <summary>The identity used to detect cycles: the full path (case-insensitive on Windows), or the URL without its query string.</summary>
    public string Key => LinkedTemplateReader.NormalizeLocation(Value);
}

/// <summary>
/// Locates and reads linked templates and parameter files (<c>templateLink</c>, <c>parametersLink</c>,
/// https://learn.microsoft.com/azure/azure-resource-manager/templates/linked-templates):
/// <list type="bullet">
/// <item><c>relativePath</c> is relative to the template that declares it: the directory of its file, or, for a template read from a URL, that URL
/// (ARM combines the parent URL and the relative path; the parent's query string is kept). It must be relative (no drive, root or scheme); <c>..</c>
/// is allowed, as in a URL.</item>
/// <item><c>uri</c> must be an absolute https URL without credentials; a <c>queryString</c> is appended. Downloads follow the rules of the CLI's
/// https parameter files (no redirects, no credentials, bounded size and time); the query string is never shown in a message.</item>
/// <item>A local file is read with the template input limits (<see cref="InputFiles"/>).</item>
/// </list>
/// Without a download function (offline validation) an https location is not fetched: <see cref="CanFetch"/> says so.
/// </summary>
public sealed class LinkedTemplateReader
{
    private readonly Func<Uri, CancellationToken, Task<byte[]>>? _download;

    /// <param name="download">Downloads an https URL (already checked); <c>null</c> reads local files only.</param>
    public LinkedTemplateReader(Func<Uri, CancellationToken, Task<byte[]>>? download)
    {
        _download = download;
    }

    /// <summary>Reads local files only; https links are not fetched.</summary>
    public static LinkedTemplateReader LocalOnly { get; } = new(null);

    /// <summary>True when https links can be downloaded.</summary>
    public bool CanFetchRemote => _download is not null;

    /// <summary>True when <paramref name="location"/> can be read by this reader.</summary>
    public bool CanFetch(LinkedLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        return !location.IsRemote || CanFetchRemote;
    }

    /// <summary>
    /// Resolves <c>relativePath</c> against <paramref name="parentLocation"/> (the declaring template's file or URL). Throws
    /// <see cref="ArgumentException"/> with the reason when it cannot.
    /// </summary>
    public static LinkedLocation ResolveRelative(string relativePath, string? parentLocation, string? queryString)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException("'relativePath' is empty.");
        }

        // A drive-letter path (C:/x, C:x) is absolute on every OS: Path.IsPathRooted only knows drive letters on Windows.
        bool driveLetter = relativePath.Length >= 2 && char.IsAsciiLetter(relativePath[0]) && relativePath[1] == ':';
        if (relativePath.Contains("://", StringComparison.Ordinal) || driveLetter || Path.IsPathRooted(relativePath) || relativePath.StartsWith('/') || relativePath.StartsWith('\\'))
        {
            throw new ArgumentException($"'relativePath' must be relative to the template ('{relativePath}' is absolute); use 'uri' for an https URL.");
        }

        if (parentLocation is null)
        {
            throw new ArgumentException("'relativePath' needs the declaring template to be a file or a URL; this template was not read from either.");
        }

        if (IsUrl(parentLocation))
        {
            var parent = new Uri(parentLocation, UriKind.Absolute);
            var combined = new Uri(parent, relativePath.Replace('\\', '/'));
            var builder = new UriBuilder(combined) { Query = AppendQuery(parent.Query, queryString) };
            return new LinkedLocation(builder.Uri.AbsoluteUri, IsRemote: true);
        }

        string directory = Path.GetDirectoryName(Path.GetFullPath(parentLocation)) ?? Directory.GetCurrentDirectory();
        return new LinkedLocation(Path.GetFullPath(Path.Combine(directory, relativePath)), IsRemote: false);
    }

    /// <summary>Checks an absolute <c>uri</c> (https only, no credentials) and appends <paramref name="queryString"/>; throws <see cref="ArgumentException"/>.</summary>
    public static LinkedLocation ResolveUri(string uri, string? queryString)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException($"'{DisplayUrl(uri)}' is not an absolute https URL; a local file is linked with 'relativePath'.");
        }

        if (parsed.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException($"'{DisplayUrl(uri)}' uses http; a linked template is downloaded over https only.");
        }

        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            throw new ArgumentException($"'{DisplayUrl(uri)}' carries a user name or password; EARM sends no credentials with a download.");
        }

        if (string.IsNullOrEmpty(queryString))
        {
            return new LinkedLocation(parsed.AbsoluteUri, IsRemote: true);
        }

        var builder = new UriBuilder(parsed) { Query = AppendQuery(parsed.Query, queryString) };
        return new LinkedLocation(builder.Uri.AbsoluteUri, IsRemote: true);
    }

    /// <summary>Reads the file or downloads the URL; throws <see cref="TemplateLoadException"/> (with the reason) when it cannot.</summary>
    /// <param name="location">What to read.</param>
    /// <param name="kind">"Linked template" or "Linked parameter file", for messages.</param>
    /// <param name="cancellationToken">Cancels a download.</param>
    public byte[] Read(LinkedLocation location, string kind, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(kind);
        if (!location.IsRemote)
        {
            return InputFiles.ReadAllBytes(location.Value, kind);
        }

        if (_download is null)
        {
            throw new InvalidOperationException($"{kind} {location.Display} is an https URL and this reader does not download.");
        }

        // The planner is synchronous at this point (expansion); the console host has no synchronization context, so waiting cannot deadlock.
        return _download(new Uri(location.Value, UriKind.Absolute), cancellationToken).GetAwaiter().GetResult();
    }

    /// <summary>The URL without its query string, fragment and user info.</summary>
    public static string DisplayUrl(string url)
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

    /// <summary>A template location in canonical form, for comparisons: a URL without its query string, a path as a full path (upper case on Windows, where paths ignore case).</summary>
    public static string NormalizeLocation(string location)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (IsUrl(location))
        {
            return DisplayUrl(location);
        }

        string full = Path.GetFullPath(location);
        return OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full;
    }

    /// <summary>True for an <c>http(s)://</c> location.</summary>
    public static bool IsUrl(string location)
    {
        return location.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || location.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
    }

    private static string AppendQuery(string existing, string? extra)
    {
        string current = existing.TrimStart('?');
        string added = (extra ?? string.Empty).TrimStart('?');
        if (current.Length == 0)
        {
            return added;
        }

        return added.Length == 0 ? current : current + "&" + added;
    }
}
