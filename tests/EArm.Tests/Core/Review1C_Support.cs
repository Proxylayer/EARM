using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Loading;
using EArm.Core.Types;
using EArm.Core.Validation;
using EArm.Expressions.Validation;

namespace EArm.Tests;

/// <summary>Shared helpers of the review 1C regression tests: in-process validation of template text and a child-process runner for the CLI.</summary>
internal static class Review1C
{
    public const string SchemaUri = "https://schema.earm.proxylayer.net/schemas/2026-10-01/deploymentTemplate.json#";

    public const string Header = "{ \"$schema\": \"" + SchemaUri + "\", \"contentVersion\": \"1.0.0.0\", ";

    /// <summary>A complete template: the envelope plus <paramref name="body"/> (the members after <c>contentVersion</c>).</summary>
    public static string Template(string body)
    {
        return Header + body + " }";
    }

    /// <summary>A group resource declaration whose <c>properties</c> are <paramref name="properties"/> (JSON object text).</summary>
    public static string Group(string name, string properties, string extra = "")
    {
        return "{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": " + name + extra + ", \"properties\": " + properties + " }";
    }

    /// <summary>The diagnostics <c>earm deployment validate</c> would print for the text: the core validator and the expression validator, sorted.</summary>
    public static List<Diagnostic> Validate(string templateText, ParameterSet? parameters = null)
    {
        var catalog = ResourceTypeCatalog.Default;
        var document = new TemplateLoader(catalog).Parse(templateText, "review.entra.json", parameters ?? ParameterSet.Empty);
        var diagnostics = new List<Diagnostic>(new TemplateValidator(catalog).Validate(document));
        diagnostics.AddRange(new ExpressionValidator(catalog).Analyze(document).Diagnostics);
        diagnostics.Sort(DiagnosticOrder.Instance);
        return diagnostics;
    }

    /// <summary>JSON text of the template members <c>variables</c> for a chain <c>v0 = "a"</c>, <c>v{i} = [wrap(variables('v{i-1}'))]</c>.</summary>
    public static string VariableChain(int count, string wrap)
    {
        var sb = new StringBuilder("\"variables\": { \"v0\": \"a\"");
        for (int i = 1; i < count; i++)
        {
            sb.Append(", \"v").Append(i).Append("\": \"[").Append(wrap).Append("(variables('v").Append(i - 1).Append("'))]\"");
        }

        sb.Append(" }");
        return sb.ToString();
    }

    /// <summary>The compiled CLI of the test output folder.</summary>
    public static string EarmDll => Path.Combine(AppContext.BaseDirectory, "earm.dll");

    /// <summary>The outcome of one run of the CLI in its own process, with the peak working set seen while it ran.</summary>
    public sealed record ChildResult(int ExitCode, string StandardOutput, string StandardError, long PeakWorkingSetBytes, TimeSpan Elapsed);

    /// <summary>
    /// Runs <c>dotnet earm.dll &lt;args&gt;</c> as a separate process (a stack overflow or an out-of-memory failure would take the test host down
    /// if it happened in-process) and samples its peak working set until it exits. Fails the run when it does not exit within
    /// <paramref name="timeout"/> (the process is killed).
    /// </summary>
    public static ChildResult RunChild(IEnumerable<string> args, TimeSpan timeout, IReadOnlyDictionary<string, string>? environment = null)
    {
        var info = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add(EarmDll);
        foreach (string arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        if (environment is not null)
        {
            foreach (var pair in environment)
            {
                info.Environment[pair.Key] = pair.Value;
            }
        }

        var watch = Stopwatch.StartNew();
        using var process = Process.Start(info) ?? throw new InvalidOperationException("The child process did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        long peak = 0;
        while (!process.HasExited)
        {
            if (watch.Elapsed > timeout)
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("The child process ran longer than " + timeout + ".");
            }

            try
            {
                process.Refresh();
                peak = Math.Max(peak, process.PeakWorkingSet64);
            }
            catch (InvalidOperationException)
            {
                // The process ended between the check and the read.
            }

            Thread.Sleep(20);
        }

        process.WaitForExit();
        return new ChildResult(process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult(), peak, watch.Elapsed);
    }

    /// <summary>A nested array literal of the given depth around <paramref name="leaf"/>: <c>[[[leaf]]]</c>.</summary>
    public static string NestedArrays(int depth, string leaf = "1")
    {
        return new string('[', depth) + leaf + new string(']', depth);
    }

    /// <summary>A hand-built tree nested <paramref name="depth"/> arrays deep (built in a loop, never recursively).</summary>
    public static JsonNode DeepTree(int depth)
    {
        JsonNode node = JsonValue.Create("leaf");
        for (int i = 0; i < depth; i++)
        {
            node = new JsonArray(node);
        }

        return node;
    }
}
