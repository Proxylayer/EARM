using System;
using System.IO;
using EntraArm.Cli.Commands;
using EntraArm.Engine.Planning;
using EntraArm.Engine.WhatIf;
using Microsoft.Extensions.Logging;

namespace EntraArm.Cli.Output;

/// <summary>
/// Writes a what-if in the selected output format: the ARM-style text (<c>-o table</c>, coloured on a terminal), the
/// ARM-shaped JSON (<c>-o json</c>, compact with <c>--no-pretty-print</c>) or nothing (<c>-o none</c>). Returns the
/// report so the caller can decide the exit code (<c>--exit-code-on-changes</c>).
/// </summary>
internal static class WhatIfWriter
{
    /// <summary>Set to any non-empty value to disable colours (https://no-color.org).</summary>
    public const string NoColorVariable = "NO_COLOR";

    /// <summary>The Azure CLI's own switch (<c>true</c> / <c>1</c> / <c>yes</c> / <c>on</c>), honoured so both tools behave alike.</summary>
    public const string AzureNoColorVariable = "AZURE_CORE_NO_COLOR";

    public static WhatIfReport Write(DeploymentPlan plan, DeploymentInvocation invocation, TextWriter stdout)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var report = WhatIfReport.From(plan);
        Write(report, invocation, stdout);
        return report;
    }

    public static void Write(WhatIfReport report, DeploymentInvocation invocation, TextWriter stdout)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(stdout);

        switch (invocation.Output)
        {
            case OutputFormat.Json:
                stdout.WriteLine(WhatIfJson.Serialize(WhatIfJson.Build(report, invocation.ResultFormat), prettyPrint: !invocation.NoPrettyPrint));
                break;

            case OutputFormat.None:
                break;

            default:
                WhatIfText.Render(report, stdout, new WhatIfTextOptions(invocation.ResultFormat, Verbose: invocation.LogLevel <= LogLevel.Debug, Colors: UseColors(stdout)));
                break;
        }
    }

    /// <summary>ANSI colours only when writing to a real terminal and neither <c>NO_COLOR</c> nor <c>AZURE_CORE_NO_COLOR</c> switches them off.</summary>
    internal static bool UseColors(TextWriter stdout)
    {
        if (!ReferenceEquals(stdout, Console.Out) || Console.IsOutputRedirected)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(NoColorVariable)))
        {
            return false;
        }

        string? azure = Environment.GetEnvironmentVariable(AzureNoColorVariable)?.Trim();
        return azure is null || !(azure.Equals("true", StringComparison.OrdinalIgnoreCase)
            || azure.Equals("1", StringComparison.Ordinal)
            || azure.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || azure.Equals("on", StringComparison.OrdinalIgnoreCase));
    }
}
