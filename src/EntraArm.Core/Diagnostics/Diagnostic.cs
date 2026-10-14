using System.Text;
using EntraArm.Core.Json;

namespace EntraArm.Core.Diagnostics;

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
        return sb.ToString();
    }
}
