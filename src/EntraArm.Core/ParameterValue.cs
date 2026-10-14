using System.Text.Json.Nodes;

namespace EntraArm.Core;

/// <summary>Where a parameter value came from; later sources win (<c>-p k=v</c> &gt; file &gt; <c>defaultValue</c>).</summary>
public enum ParameterValueSource
{
    Default,
    File,
    Inline,
}

/// <summary>
/// A parameter value as supplied (not yet bound to a definition): either a JSON value, a Key Vault reference,
/// or the raw text of an inline <c>-p name=value</c> token that is converted to the declared type at bind time.
/// </summary>
public sealed record ParameterValue
{
    public JsonNode? Value { get; init; }

    public KeyVaultReference? Reference { get; init; }

    public required ParameterValueSource Source { get; init; }

    /// <summary>The raw text of an inline token; <c>null</c> for file values.</summary>
    public string? RawText { get; init; }

    /// <summary>The parameter file path or <c>null</c> for inline values.</summary>
    public string? SourcePath { get; init; }

    public bool IsKeyVaultReference => Reference is not null;

    public bool IsInline => Source == ParameterValueSource.Inline;
}

/// <summary>
/// A parameter after binding to its definition: the effective value (secure types wrapped as
/// <see cref="Secrets.SecretValue"/> leaves), or a pending Key Vault reference, or the unevaluated default.
/// </summary>
public sealed record BoundParameter
{
    public required string Name { get; init; }

    public required ParameterDefinition Definition { get; init; }

    /// <summary>The value; for <see cref="ParameterValueSource.Default"/> this is the raw <c>defaultValue</c> and may be an expression.</summary>
    public JsonNode? Value { get; init; }

    /// <summary>Set when the value comes from a Key Vault reference that has not been resolved yet.</summary>
    public KeyVaultReference? PendingReference { get; init; }

    public required ParameterValueSource Source { get; init; }

    public bool IsSecure => Definition.IsSecure;

    /// <summary>True when neither a value nor a default exists.</summary>
    public bool IsMissing { get; init; }

    /// <summary>
    /// True when an inline <c>-p name=value</c> text could not be converted to the declared type. The loader has
    /// already reported <c>ParameterValueTypeMismatch</c> (with the definition's location) and <see cref="Value"/>
    /// holds the raw text; later type checks skip the value instead of reporting it again.
    /// </summary>
    public bool ConversionFailed { get; init; }
}
