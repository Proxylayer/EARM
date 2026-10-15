using System;
using EArm.Core.Diagnostics;
using EArm.Core.Json;

namespace EArm.Core.Loading;

/// <summary>
/// Raised when a template or parameter file cannot be turned into a document at all (missing file, invalid JSON,
/// wrong root shape). Everything else is reported as diagnostics on the loaded document.
/// </summary>
public sealed class TemplateLoadException : Exception
{
    public TemplateLoadException(string code, string message, string? source = null, SourceLocation? location = null, JsonPath? path = null, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
        Source = source;
        Location = location;
        Path = path;
    }

    /// <summary>A <see cref="DiagnosticCodes"/> value.</summary>
    public string Code { get; }

    /// <summary>The file the failure relates to.</summary>
    public new string? Source { get; }

    public SourceLocation? Location { get; }

    public JsonPath? Path { get; }

    public Diagnostic ToDiagnostic()
    {
        return Diagnostic.Error(Code, Message, Path, Location, Source);
    }
}
