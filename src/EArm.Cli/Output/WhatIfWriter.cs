using System;
using System.IO;
using EArm.Cli.Commands;
using EArm.Engine.Planning;
using EArm.Engine.WhatIf;
using Microsoft.Extensions.Logging;

namespace EArm.Cli.Output;

/// <summary>
/// Writes a what-if in the selected output format: the ARM-style text (<c>-o table</c>, coloured on a terminal), the
/// ARM-shaped JSON (<c>-o json</c>, compact with <c>--no-pretty-print</c>) or nothing (<c>-o none</c>). Returns the
/// report so the caller can decide the exit code (<c>--exit-code-on-changes</c>).
/// </summary>
internal static class WhatIfWriter
{
    /// <summary>Set to any non-empty value to disable colours (https://no-color.org).</summary>
    public const string NoColorVariable = "NO_COLOR";

    /// <summary>The switch of the Azure CLI's what-if (<c>true</c> / <c>1</c> / <c>yes</c> / <c>on</c>), honoured so both tools behave alike.</summary>
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

        var view = View(report, invocation);
        switch (invocation.Output)
        {
            case OutputFormat.Json:
                stdout.WriteLine(WhatIfJson.Serialize(WhatIfJson.Build(view), prettyPrint: !invocation.NoPrettyPrint));
                break;

            case OutputFormat.None:
                break;

            default:
                RenderText(view, invocation, stdout, UseColors(stdout));
                break;
        }
    }

    /// <summary>What the what-if of <paramref name="invocation"/> shows: its result format and <c>--exclude-change-types</c> applied to <paramref name="report"/>.</summary>
    public static WhatIfView View(WhatIfReport report, DeploymentInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        return WhatIfView.Of(report, invocation.ResultFormat, invocation.ExcludeChangeTypes);
    }

    /// <summary>The what-if text of <paramref name="view"/>; <c>--verbose</c> adds the NoChange payloads, the NoEffect deltas and the skipped resources.</summary>
    public static void RenderText(WhatIfView view, DeploymentInvocation invocation, TextWriter writer, bool colors)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        WhatIfText.Render(view, writer, new WhatIfTextOptions(view.Format, Verbose: invocation.LogLevel <= LogLevel.Debug, Colors: colors));
    }

    /// <summary>ANSI colours only when writing to a real terminal and neither <c>NO_COLOR</c> nor <c>AZURE_CORE_NO_COLOR</c> switches them off.</summary>
    internal static bool UseColors(TextWriter stdout)
    {
        if (!ReferenceEquals(ConsoleSanitizer.Unwrap(stdout), Console.Out) || Console.IsOutputRedirected)
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
