using System;
using System.IO;
using EArm.Engine.State;
using Microsoft.Extensions.Logging;

namespace EArm.Cli.Hosting;

/// <summary>Opens the file state store for a command: <c>--state-dir</c> → <c>EARM_STATE_DIR</c> → <c>.earm/state</c> next to the template (or the working directory).</summary>
internal static class StateHost
{
    public static FileStateStore OpenStore(string? stateDir, string? templateFile, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        return new FileStateStore(StateDirectory.Resolve(stateDir, templateFile), logger: loggerFactory.CreateLogger<FileStateStore>());
    }
}

/// <summary>Console confirmation prompts shared by the verbs that change something.</summary>
internal static class ConsolePrompt
{
    /// <summary>Reads a confirmation line from the console.</summary>
    public static readonly Func<string?> DefaultReadLine = Console.ReadLine;

    /// <summary>Asks <paramref name="question"/> and returns true for <c>y</c>/<c>yes</c>; refuses when stdin is not interactive and the default reader is in use.</summary>
    public static bool Confirm(string question, TextWriter stdout, TextWriter stderr, Func<string?> readLine)
    {
        if (Console.IsInputRedirected && ReferenceEquals(readLine, DefaultReadLine))
        {
            stderr.WriteLine("Standard input is not interactive; pass --yes to confirm.");
            return false;
        }

        stdout.Write(question + " (y/n): ");
        stdout.Flush();
        string? answer = readLine();
        return answer is not null && (answer.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) || answer.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase));
    }
}
