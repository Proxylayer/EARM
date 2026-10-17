using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using EArm.Core;
using EArm.Core.Json;

namespace EArm.Cli.Hosting;

/// <summary>
/// Asks on the console for the parameters that have neither a value nor a default, as <c>az deployment ... create</c> does on an interactive terminal
/// (<c>Please provide string value for 'name' (? for help): </c>; <c>?</c> prints the parameter's description and allowed values). A secure parameter is read
/// without echo. The prompts go to standard error, so standard output stays the result document. <c>--no-prompt</c> turns this off, and nothing is asked
/// when standard input or standard error is redirected: the run then fails with <c>MissingParameterValue</c> as before. The answers are applied as inline
/// values (<c>-p name=value</c>), converted to the parameter's type by the loader.
/// </summary>
internal sealed class ParameterPrompt
{
    private readonly TextWriter _stderr;
    private readonly Func<string?> _readLine;
    private readonly Func<string?> _readSecret;

    public ParameterPrompt(TextWriter stderr, Func<string?> readLine, Func<string?> readSecret)
    {
        ArgumentNullException.ThrowIfNull(stderr);
        ArgumentNullException.ThrowIfNull(readLine);
        ArgumentNullException.ThrowIfNull(readSecret);
        _stderr = stderr;
        _readLine = readLine;
        _readSecret = readSecret;
    }

    /// <summary>A prompt on the process console, or <c>null</c> when standard input or standard error is redirected (nobody can answer).</summary>
    public static ParameterPrompt? ForConsole(TextWriter stderr)
    {
        return Console.IsInputRedirected || Console.IsErrorRedirected ? null : new ParameterPrompt(stderr, Console.ReadLine, ReadHidden);
    }

    /// <summary>
    /// Asks for each of <paramref name="missing"/> in order; returns the answers (name, text), or <c>null</c> when input ends before every parameter has
    /// one (the run then reports the missing values as usual).
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>>? Ask(IEnumerable<ParameterDefinition> missing)
    {
        ArgumentNullException.ThrowIfNull(missing);
        var answers = new List<KeyValuePair<string, string>>();
        foreach (var definition in missing)
        {
            string? answer = AskOne(definition);
            if (answer is null)
            {
                return null;
            }

            answers.Add(new KeyValuePair<string, string>(definition.Name, answer));
        }

        return answers;
    }

    private string? AskOne(ParameterDefinition definition)
    {
        string type = definition.Type ?? "string";
        while (true)
        {
            _stderr.Write($"Please provide {type} value for '{definition.Name}' (? for help): ");
            _stderr.Flush();
            string? answer = definition.IsSecure ? _readSecret() : _readLine();
            if (answer is null)
            {
                _stderr.WriteLine();
                return null;
            }

            if (answer.Trim() != "?")
            {
                return answer;
            }

            _stderr.WriteLine(Help(definition));
        }
    }

    private static string Help(ParameterDefinition definition)
    {
        var help = new StringBuilder();
        string? description = definition.Metadata is { } metadata ? TemplateJson.AsString(metadata["description"]) : null;
        help.Append(string.IsNullOrWhiteSpace(description) ? "(no description)" : description);
        if (definition.AllowedValues is { Count: > 0 } allowed)
        {
            help.Append(" Allowed values: ").Append(string.Join(", ", allowed.Select(value => value?.ToJsonString() ?? "null"))).Append('.');
        }

        if (!string.Equals(definition.Type, "string", StringComparison.OrdinalIgnoreCase) && !definition.IsSecure)
        {
            help.Append(" Enter JSON for an object, an array, an integer or a boolean.");
        }

        return help.ToString();
    }

    /// <summary>Reads a line from the console without echoing it (backspace edits); <c>null</c> at end of input.</summary>
    private static string? ReadHidden()
    {
        var text = new StringBuilder();
        while (true)
        {
            ConsoleKeyInfo key;
            try
            {
                key = Console.ReadKey(intercept: true);
            }
            catch (InvalidOperationException)
            {
                return Console.ReadLine();
            }

            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    Console.Error.WriteLine();
                    return text.ToString();
                case ConsoleKey.Backspace:
                    if (text.Length > 0)
                    {
                        text.Length--;
                    }

                    break;
                default:
                    if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
                    {
                        text.Append(key.KeyChar);
                    }

                    break;
            }
        }
    }
}
