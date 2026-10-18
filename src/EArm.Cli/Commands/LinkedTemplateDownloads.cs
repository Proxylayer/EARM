using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using EArm.Engine.Planning.Nested;

namespace EArm.Cli.Commands;

/// <summary>
/// The reader of linked templates and parameter files of nested deployments for the verbs that plan (<c>what-if</c>, <c>create</c>, <c>delete</c>,
/// <c>permissions</c>): local files next to the template, and https URLs downloaded with the rules of <c>-p https://...</c> parameter files
/// (<see cref="ParameterFileDownloader"/>: https only, no credentials, no redirects, bounded size and time, the query string never shown).
/// <c>deployment validate</c> stays offline and uses <see cref="LinkedTemplateReader.LocalOnly"/>.
/// </summary>
internal static class LinkedTemplateDownloads
{
    /// <summary>Downloads every https link it is asked for (one offline expansion: <c>permissions</c>, <c>auth login -f</c>, <c>auth status -f</c>).</summary>
    public static LinkedTemplateReader Reader { get; } = new(DownloadAsync);

    /// <summary>
    /// A reader for one deployment run: the run expands the template offline for its delegated scopes before sign-in, validates it and plans it, and
    /// each step reads the same links; this reader downloads each https URL (query string included) once and serves the later reads from memory. A
    /// failed download is not kept, so a later step tries again.
    /// </summary>
    public static LinkedTemplateReader ForRun()
    {
        return ForRun(DownloadAsync);
    }

    /// <summary><see cref="ForRun()"/> over <paramref name="download"/>.</summary>
    internal static LinkedTemplateReader ForRun(Func<Uri, CancellationToken, Task<byte[]>> download)
    {
        ArgumentNullException.ThrowIfNull(download);
        var downloaded = new ConcurrentDictionary<string, byte[]>(StringComparer.Ordinal);
        return new LinkedTemplateReader(async (uri, cancellationToken) =>
        {
            if (downloaded.TryGetValue(uri.AbsoluteUri, out byte[]? content))
            {
                return content;
            }

            content = await download(uri, cancellationToken).ConfigureAwait(false);
            return downloaded.GetOrAdd(uri.AbsoluteUri, content);
        });
    }

    private static Task<byte[]> DownloadAsync(Uri uri, CancellationToken cancellationToken)
    {
        return ParameterFileDownloader.Shared.DownloadAsync(uri, "linked file", cancellationToken);
    }
}
