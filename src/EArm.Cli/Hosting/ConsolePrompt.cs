using System;
using System.IO;

namespace EArm.Cli.Hosting;

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

        // The prompt goes to standard error so that standard output stays the machine-readable result (-o json).
        stderr.Write(question + " (y/n): ");
        stderr.Flush();
        string? answer = readLine();
        return answer is not null && (answer.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) || answer.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase));
    }
}
