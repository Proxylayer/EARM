using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Cli;

namespace EArm.Integration.Tests;

/// <summary>The captured outcome of one in-process <c>earm</c> invocation.</summary>
internal sealed record CliResult(int ExitCode, string StandardOutput, string StandardError)
{
    /// <summary>Standard output and error, for failure messages and substring checks (the CLI redacts secrets).</summary>
    public string Combined => StandardOutput + Environment.NewLine + StandardError;

    /// <summary>Standard output parsed as a JSON object (for <c>-o json</c> runs).</summary>
    public JsonObject Json
    {
        get
        {
            return JsonNode.Parse(StandardOutput) as JsonObject
                ?? throw new InvalidOperationException("Standard output is not a JSON object: " + Truncate(Combined));
        }
    }

    /// <summary><c>changes[]</c> of a what-if document.</summary>
    public IReadOnlyList<JsonObject> Changes => ArrayOf("changes");

    /// <summary><c>operations[]</c> of a create document.</summary>
    public IReadOnlyList<JsonObject> Operations => ArrayOf("operations");

    /// <summary><c>diagnostics[]</c> of a what-if or create document.</summary>
    public IReadOnlyList<JsonObject> Diagnostics => ArrayOf("diagnostics");

    /// <summary>The change type (<c>Create</c>, <c>Modify</c>, <c>Delete</c>, <c>NoChange</c>, <c>Ignore</c>) of the what-if row whose resource id ends with the suffix.</summary>
    public string? ChangeTypeOf(string resourceIdSuffix)
    {
        return Changes
            .Where(c => ((string?)c["resourceId"])?.EndsWith(resourceIdSuffix, StringComparison.OrdinalIgnoreCase) == true)
            .Select(c => (string?)c["changeType"])
            .FirstOrDefault();
    }

    /// <summary>One entry per what-if row (<c>NoChange /providers/...</c>) for assertion messages.</summary>
    public string DescribeChanges()
    {
        return string.Join("; ", Changes.Select(c => $"{(string?)c["changeType"]} {(string?)c["resourceId"]}"));
    }

    /// <summary>One entry per applied operation (<c>Succeeded /providers/... error</c>) for assertion messages.</summary>
    public string DescribeOperations()
    {
        return string.Join("; ", Operations.Select(o => $"{(string?)o["status"]} {(string?)o["resourceId"]} {(string?)o["error"]}".TrimEnd()));
    }

    /// <summary>True when a diagnostic with the code is present.</summary>
    public bool HasDiagnostic(string code)
    {
        return Diagnostics.Any(d => string.Equals((string?)d["code"], code, StringComparison.Ordinal));
    }

    private IReadOnlyList<JsonObject> ArrayOf(string name)
    {
        return Json[name] is JsonArray array ? array.OfType<JsonObject>().ToList() : [];
    }

    private static string Truncate(string text)
    {
        return text.Length <= 600 ? text : text[..600] + "...";
    }
}

/// <summary>One deployment of one template: the state name, the template file and its parameters.</summary>
internal sealed record DeploymentRun(string Name, string TemplateFile, IReadOnlyList<string> ParameterTokens)
{
    public DeploymentRun WithParameter(string name, string value)
    {
        return this with { ParameterTokens = [.. ParameterTokens, "-p", name + "=" + value] };
    }

    public DeploymentRun WithTemplate(string templateFile)
    {
        return this with { TemplateFile = templateFile };
    }
}

/// <summary>
/// Drives the real command line in-process (<see cref="Program.BuildRootCommand"/> with the production parser
/// configuration) against the tenant. Every call carries <c>--state-dir</c> (the run's private state directory); sign-in comes from
/// the environment (<c>AZURE_*</c>, <c>EARM_AUTH</c>) or <c>earm auth login</c>. Calls must not overlap: the global <c>-o</c> option is
/// static, so every test class shares one xUnit collection.
/// </summary>
internal sealed class CliDriver
{
    private readonly string _stateDirectory;
    public CliDriver(string stateDirectory)
    {
        _stateDirectory = stateDirectory;
    }

    public async Task<CliResult> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var argv = new List<string>(args) { "--state-dir", _stateDirectory };
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var configuration = new InvocationConfiguration { Output = stdout, Error = stderr };
        var parseResult = Program.BuildRootCommand().Parse(argv.ToArray(), Program.CreateParserConfiguration());
        int exitCode = await parseResult.InvokeAsync(configuration, cancellationToken);
        return new CliResult(exitCode, stdout.ToString(), stderr.ToString());
    }

    /// <summary><c>deployment what-if -f … -n … -p … -o json</c> plus the extra arguments.</summary>
    public Task<CliResult> WhatIfAsync(DeploymentRun run, CancellationToken cancellationToken, params string[] extra)
    {
        return RunAsync(["deployment", "what-if", "-f", run.TemplateFile, "-n", run.Name, .. run.ParameterTokens, "-o", "json", .. extra], cancellationToken);
    }

    /// <summary><c>deployment create -f … -n … -p … -y -o json</c> plus the extra arguments.</summary>
    public Task<CliResult> CreateAsync(DeploymentRun run, CancellationToken cancellationToken, params string[] extra)
    {
        return RunAsync(["deployment", "create", "-f", run.TemplateFile, "-n", run.Name, .. run.ParameterTokens, "-y", "-o", "json", .. extra], cancellationToken);
    }

    /// <summary><c>deployment delete -n … --delete-resources -y</c>.</summary>
    public Task<CliResult> DeleteAllAsync(string deploymentName, CancellationToken cancellationToken)
    {
        return RunAsync(["deployment", "delete", "-n", deploymentName, "--delete-resources", "-y"], cancellationToken);
    }
}
