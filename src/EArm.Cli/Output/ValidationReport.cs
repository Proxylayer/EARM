using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace EArm.Cli.Output;

/// <summary>JSON shape of <c>earm deployment validate -o json</c>.</summary>
internal sealed class ValidationReport
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "Succeeded";

    [JsonPropertyName("templateFile")]
    public string? TemplateFile { get; set; }

    [JsonPropertyName("parameterFile")]
    public string? ParameterFile { get; set; }

    [JsonPropertyName("errorCount")]
    public int ErrorCount { get; set; }

    [JsonPropertyName("warningCount")]
    public int WarningCount { get; set; }

    /// <summary>The diagnostics, at most <see cref="DiagnosticCap.PerCodeLimit"/> of each code and severity; the rest are counted in <see cref="Suppressed"/>. <see cref="ErrorCount"/> and <see cref="WarningCount"/> count all of them.</summary>
    [JsonPropertyName("diagnostics")]
    public List<DiagnosticReport> Diagnostics { get; set; } = [];

    /// <summary>How many diagnostics were left out of <see cref="Diagnostics"/> because their code repeated more than the cap allows (0 when none).</summary>
    [JsonPropertyName("suppressedCount")]
    public int SuppressedCount { get; set; }

    /// <summary>One entry per code and severity that was capped; omitted when nothing was suppressed.</summary>
    [JsonPropertyName("suppressed")]
    public List<SuppressedReport>? Suppressed { get; set; }

    [JsonPropertyName("resources")]
    public List<ResourceReport> Resources { get; set; } = [];
}

internal sealed class DiagnosticReport
{
    [JsonPropertyName("severity")]
    public string Severity { get; set; } = "Error";

    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("line")]
    public int? Line { get; set; }

    [JsonPropertyName("column")]
    public int? Column { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }
}

internal sealed class SuppressedReport
{
    [JsonPropertyName("severity")]
    public string Severity { get; set; } = "Error";

    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    /// <summary>How many diagnostics of this code and severity are not listed.</summary>
    [JsonPropertyName("count")]
    public int Count { get; set; }
}

internal sealed class ResourceReport
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("apiVersion")]
    public string? ApiVersion { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>The ARM-style resource id when the name is a literal or evaluates statically; <c>null</c> when it is only known at deployment.</summary>
    [JsonPropertyName("resourceId")]
    public string? ResourceId { get; set; }

    [JsonPropertyName("nested")]
    public bool Nested { get; set; }

    [JsonPropertyName("dependsOn")]
    public List<string> DependsOn { get; set; } = [];

    [JsonPropertyName("inferredDependsOn")]
    public List<string> InferredDependsOn { get; set; } = [];

    [JsonPropertyName("path")]
    public string? Path { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ValidationReport))]
internal sealed partial class CliJsonContext : JsonSerializerContext
{
}
