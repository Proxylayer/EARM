using System;
using System.Collections.Generic;
using System.Linq;
using EntraArm.Core.Diagnostics;

namespace EntraArm.Engine.Planning;

/// <summary>The template did not pass validation (or expansion / graph construction reported errors); nothing was planned.</summary>
public sealed class ValidationFailedException : Exception
{
    public ValidationFailedException(IReadOnlyList<Diagnostic> diagnostics)
        : base(FormatMessage(diagnostics))
    {
        Diagnostics = diagnostics;
    }

    /// <summary>Every diagnostic (errors and warnings) in report order.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    public IEnumerable<Diagnostic> Errors => Diagnostics.Where(d => d.IsError);

    private static string FormatMessage(IReadOnlyList<Diagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        int errors = diagnostics.Count(d => d.IsError);
        var first = diagnostics.FirstOrDefault(d => d.IsError);
        return first is null
            ? "Validation failed."
            : $"Validation failed with {errors} error(s); first: {first}";
    }
}
