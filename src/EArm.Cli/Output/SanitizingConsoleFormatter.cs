using System;
using System.Globalization;
using System.IO;
using EArm.Core.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace EArm.Cli.Output;

/// <summary>
/// The console formatter of the logger: one line per entry in the shape of the built-in single-line formatter
/// (<c>HH:mm:ss level: Category[eventId] message</c>) without colours, with every control character, escape sequence and bidirectional override of
/// the message shown as a visible escape and any line that would read as a CI log command neutralised (see <see cref="ConsoleSanitizer"/>).
/// Log messages echo names and Graph error text that originate outside the process, so they get the same treatment as everything else written to
/// the terminal. An exception is reduced to its type and message on the same line; <c>--debug</c> adds nothing more here (stack traces are for the
/// debugger, not for CI logs).
/// </summary>
internal sealed class SanitizingConsoleFormatter : ConsoleFormatter
{
    /// <summary>The name the formatter is registered under.</summary>
    public const string FormatterName = "earm";

    public SanitizingConsoleFormatter()
        : base(FormatterName)
    {
    }

    public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        ArgumentNullException.ThrowIfNull(textWriter);
        string message = logEntry.Formatter?.Invoke(logEntry.State, logEntry.Exception) ?? string.Empty;
        if (message.Length == 0 && logEntry.Exception is null)
        {
            return;
        }

        string line = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTime.Now:HH:mm:ss} {LevelText(logEntry.LogLevel)}: {logEntry.Category}[{logEntry.EventId.Id}] {message}");
        if (logEntry.Exception is { } exception)
        {
            line += " " + exception.GetType().Name + ": " + exception.Message;
        }

        textWriter.WriteLine(SafeText.NeutralizeCommandPrefix(SafeText.Escape(line)));
    }

    private static string LevelText(LogLevel level)
    {
        return level switch
        {
            LogLevel.Trace => "trce",
            LogLevel.Debug => "dbug",
            LogLevel.Information => "info",
            LogLevel.Warning => "warn",
            LogLevel.Error => "fail",
            LogLevel.Critical => "crit",
            _ => "none",
        };
    }
}
