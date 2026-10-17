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
    public static LinkedTemplateReader Reader { get; } = new((uri, cancellationToken) => ParameterFileDownloader.Shared.DownloadAsync(uri, "linked file", cancellationToken));
}
