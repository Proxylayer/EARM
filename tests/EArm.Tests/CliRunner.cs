using System;
using System.CommandLine;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Cli;
using Xunit;

namespace EArm.Tests;

/// <summary>The captured outcome of one in-process <c>earm</c> invocation.</summary>
internal sealed record CliResult(int ExitCode, string StandardOutput, string StandardError)
{
    /// <summary>The standard output parsed as a JSON object (for <c>-o json</c> runs).</summary>
    public JsonObject Json
    {
        get
        {
            return JsonNode.Parse(StandardOutput) as JsonObject
                ?? throw new InvalidOperationException("Standard output is not a JSON object: " + StandardOutput);
        }
    }

    public JsonArray Diagnostics => Json["diagnostics"]!.AsArray();

    public JsonArray Resources => Json["resources"]!.AsArray();
}

/// <summary>
/// Drives the real command line in-process through the production entry point (<see cref="Program.InvokeAsync"/>: root command, parser
/// configuration, output sanitizing and the catch-all), with standard output and error captured.
/// </summary>
internal static class CliRunner
{
    public static async Task<CliResult> RunAsync(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var configuration = new InvocationConfiguration { Output = stdout, Error = stderr };

        int exitCode = await Program.InvokeAsync(args, configuration, TestContext.Current.CancellationToken);

        return new CliResult(exitCode, stdout.ToString(), stderr.ToString());
    }

    /// <summary>Runs <c>deployment validate -f &lt;template&gt; -o json</c> plus any extra arguments.</summary>
    public static Task<CliResult> ValidateJsonAsync(string templateFile, params string[] extraArgs)
    {
        string[] args = ["deployment", "validate", "-f", templateFile, "-o", "json", .. extraArgs];
        return RunAsync(args);
    }
}

/// <summary>
/// Test classes that build the root command share one collection so they run sequentially: the global
/// <c>-o</c> option instance is static and is added to every root command that is built.
/// </summary>
[CollectionDefinition(Name)]
public sealed class CliCollection
{
    public const string Name = "Cli";
}
