using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core;
using EArm.Core.Loading;

namespace EArm.Cli.Commands;

/// <summary>The parameter set built from the <c>-p</c> values, and the name of the last parameter file (or URL, without its query) among them.</summary>
internal sealed record ParameterArgumentsResult(ParameterSet Parameters, string? ParameterFile);

/// <summary>
/// The <c>-p</c> / <c>--parameters</c> option and its values, in the forms <c>az deployment ... --parameters</c> accepts: <c>@file.json</c> or a bare path to an
/// existing file (an ARM <c>deploymentParameters</c> document), an <c>https://</c> URL of one (<see cref="ParameterFileDownloader"/>), an inline JSON object
/// (<c>{"a":{"value":1}}</c>, either shape), <c>name=value</c>, and <c>name=@file</c> (the file content is the value, converted to the parameter's type
/// like any inline value). One <c>-p</c> takes several values (<c>-p @base.json env=dev</c>) and the option repeats; values apply in command-line order and
/// a later value wins, whichever form it has. A <c>.bicepparam</c> file is refused: it needs the Bicep compiler.
/// </summary>
internal static class ParameterArguments
{
    public const string Description = "Parameter values: @file.json or a path to a parameter file (ARM deploymentParameters shape), an https:// URL of one, an inline JSON object, name=value, or name=@file (the file content is the value). Several values per -p; repeatable; later values win.";

    /// <summary>A <c>-p</c> option taking one or more values per occurrence.</summary>
    public static Option<string[]> CreateOption()
    {
        return new Option<string[]>("--parameters", "-p")
        {
            Description = Description,
            AllowMultipleArgumentsPerToken = true,
            Arity = ArgumentArity.OneOrMore,
            DefaultValueFactory = _ => [],
        };
    }

    /// <summary>
    /// Builds the parameter set from <paramref name="tokens"/> in order. Throws <see cref="TemplateLoadException"/> for a file or download that cannot be
    /// loaded and <see cref="ArgumentException"/> for a value in no accepted form. <paramref name="downloader"/> defaults to <see cref="ParameterFileDownloader.Shared"/>.
    /// </summary>
    public static async Task<ParameterArgumentsResult> BuildAsync(IReadOnlyList<string> tokens, CancellationToken cancellationToken, ParameterFileDownloader? downloader = null)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        string? parameterFile = null;
        var set = ParameterSet.Empty;
        foreach (var token in tokens)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            if (token.StartsWith('@'))
            {
                string path = token[1..];
                RefuseBicepParam(path);
                set = set.Merge(ParameterSet.FromFile(path));
                parameterFile = path;
                continue;
            }

            RefuseBicepParam(token);
            if (File.Exists(token))
            {
                set = set.Merge(ParameterSet.FromFile(token));
                parameterFile = token;
                continue;
            }

            if (ParameterFileDownloader.IsUrl(token))
            {
                var uri = ParameterFileDownloader.Validate(token);
                byte[] bytes = await (downloader ?? ParameterFileDownloader.Shared).DownloadAsync(uri, cancellationToken).ConfigureAwait(false);
                string shown = ParameterFileDownloader.Display(token);
                set = set.Merge(ParameterSet.Parse(bytes, shown));
                parameterFile = shown;
                continue;
            }

            if (token.TrimStart().StartsWith('{'))
            {
                // An inline document has no file: diagnostics about it carry no file name.
                set = set.Merge(ParameterSet.Parse(token));
                continue;
            }

            if (ParameterSet.TryParseInline(token, out string name, out string rawText))
            {
                set = set.WithInline(name, rawText.StartsWith('@') ? ReadValueFile(name, rawText[1..]) : rawText);
                continue;
            }

            if (token.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                // Looks like a parameter file that is not there: the loader's message names the missing file.
                set = set.Merge(ParameterSet.FromFile(token));
                continue;
            }

            throw new ArgumentException($"Parameter argument '{token}' is not valid; expected '@file.json', a parameter file path, an https:// URL, a JSON object, 'name=value' or 'name=@file'.");
        }

        return new ParameterArgumentsResult(set, parameterFile);
    }

    /// <summary>The text of the file of <c>name=@file</c> (UTF-8, a byte order mark dropped, nothing else trimmed, as the Azure CLI reads it).</summary>
    private static string ReadValueFile(string name, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new ArgumentException($"Parameter '{name}': the value '@{path}' names a file that does not exist ('name=@file' takes the value from a file). To pass a value that starts with '@', use a parameter file or an inline JSON object.");
        }

        byte[] bytes = InputFiles.ReadAllBytes(path, "Parameter value");
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false).GetString(bytes).TrimStart((char)0xFEFF);
    }

    private static void RefuseBicepParam(string path)
    {
        if (path.EndsWith(".bicepparam", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"'{path}' is a Bicep parameter file (.bicepparam), which EARM does not support: it needs the Bicep compiler. Use a JSON parameter file (ARM deploymentParameters shape) instead; 'bicep build-params' converts one.");
        }
    }
}
