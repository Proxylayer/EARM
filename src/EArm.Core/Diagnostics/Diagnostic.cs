using System.Text;
using EArm.Core.Json;

namespace EArm.Core.Diagnostics;

/// <summary>
/// One validation or load finding. <see cref="Path"/> is the JSON path inside <see cref="Source"/> (template or
/// parameter file); <see cref="Location"/> is the line/column of that path when the source map knows it.
/// </summary>
public sealed record Diagnostic(
    DiagnosticSeverity Severity,
    string Code,
    string Message,
    JsonPath? Path = null,
    SourceLocation? Location = null,
    string? Source = null)
{
    private readonly string _message = SafeText.Escape(Message);

    /// <summary>
    /// The text of the finding. Messages echo names and values from the template, so control characters (line breaks, escape sequences),
    /// DEL and bidirectional overrides are replaced by visible escapes (<see cref="SafeText.Escape"/>) however the diagnostic was created:
    /// a message is always a single printable line and cannot smuggle a CI log command onto a line of its own.
    /// </summary>
    public string Message
    {
        get => _message;
        init => _message = SafeText.Escape(value);
    }

    public bool IsError => Severity == DiagnosticSeverity.Error;

    public static Diagnostic Error(string code, string message, JsonPath? path = null, SourceLocation? location = null, string? source = null)
    {
        return new Diagnostic(DiagnosticSeverity.Error, code, message, path, location, source);
    }

    public static Diagnostic Warning(string code, string message, JsonPath? path = null, SourceLocation? location = null, string? source = null)
    {
        return new Diagnostic(DiagnosticSeverity.Warning, code, message, path, location, source);
    }

    public static Diagnostic Info(string code, string message, JsonPath? path = null, SourceLocation? location = null, string? source = null)
    {
        return new Diagnostic(DiagnosticSeverity.Info, code, message, path, location, source);
    }

    /// <summary>Renders <c>severity Code [path] (line:col): message</c>.</summary>
    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append(Severity.ToString().ToLowerInvariant()).Append(' ').Append(Code);
        if (Path is not null && !Path.IsRoot)
        {
            sb.Append(" [").Append(Path).Append(']');
        }

        if (Location is { } location)
        {
            sb.Append(" (").Append(location).Append(')');
        }

        sb.Append(": ").Append(Message);
        return SafeText.Escape(sb.ToString());
    }
}
