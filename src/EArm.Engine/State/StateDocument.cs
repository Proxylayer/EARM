using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace EArm.Engine.State;

/// <summary>
/// The on-disk shape of a state file (plan section 1.7, <c>schemaVersion</c> 1). <see cref="StateSerializer"/> maps
/// it to and from <see cref="StateFile"/>; <see cref="StateJsonContext"/> serialises it without reflection.
/// </summary>
public sealed class StateDocument
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = StateFile.CurrentSchemaVersion;

    [JsonPropertyName("deploymentName")]
    public string DeploymentName { get; set; } = string.Empty;

    [JsonPropertyName("tenantId")]
    public string? TenantId { get; set; }

    [JsonPropertyName("createdOn")]
    public DateTimeOffset? CreatedOn { get; set; }

    [JsonPropertyName("lastDeployedOn")]
    public DateTimeOffset? LastDeployedOn { get; set; }

    [JsonPropertyName("lastStatus")]
    public string? LastStatus { get; set; }

    [JsonPropertyName("lastMode")]
    public string? LastMode { get; set; }

    [JsonPropertyName("lastTemplateHash")]
    public string? LastTemplateHash { get; set; }

    /// <summary>Entries keyed by ARM-style resource id, written in ordinal key order so commits diff cleanly.</summary>
    [JsonPropertyName("resources")]
    public Dictionary<string, StateResourceDocument> Resources { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("operations")]
    public List<StateOperationDocument> Operations { get; set; } = [];
}

/// <summary>One entry of <see cref="StateDocument.Resources"/>.</summary>
public sealed class StateResourceDocument
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("apiVersion")]
    public string ApiVersion { get; set; } = string.Empty;

    [JsonPropertyName("key")]
    public Dictionary<string, string> Key { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The Graph object id.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("parentId")]
    public string? ParentId { get; set; }

    [JsonPropertyName("contentHash")]
    public string? ContentHash { get; set; }

    [JsonPropertyName("dependsOn")]
    public List<string> DependsOn { get; set; } = [];

    [JsonPropertyName("lastWriteOn")]
    public DateTimeOffset? LastWriteOn { get; set; }

    [JsonPropertyName("extra")]
    public JsonObject? Extra { get; set; }

    [JsonPropertyName("relationships")]
    public JsonObject? Relationships { get; set; }
}

/// <summary>One entry of <see cref="StateDocument.Operations"/>.</summary>
public sealed class StateOperationDocument
{
    [JsonPropertyName("resourceId")]
    public string ResourceId { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("changeType")]
    public string? ChangeType { get; set; }

    [JsonPropertyName("batch")]
    public int? Batch { get; set; }

    [JsonPropertyName("startedOn")]
    public DateTimeOffset? StartedOn { get; set; }

    [JsonPropertyName("finishedOn")]
    public DateTimeOffset? FinishedOn { get; set; }

    [JsonPropertyName("graphId")]
    public string? GraphId { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("errorCode")]
    public string? ErrorCode { get; set; }
}

/// <summary>The content of a <c>&lt;name&gt;.lock</c> file.</summary>
public sealed class StateLockDocument
{
    [JsonPropertyName("pid")]
    public int ProcessId { get; set; }

    [JsonPropertyName("host")]
    public string Host { get; set; } = string.Empty;

    [JsonPropertyName("user")]
    public string User { get; set; } = string.Empty;

    [JsonPropertyName("acquiredOn")]
    public DateTimeOffset AcquiredOn { get; set; }
}

/// <summary>Source-generated serializer metadata for the state file and lock file documents.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(StateDocument))]
[JsonSerializable(typeof(StateLockDocument))]
public sealed partial class StateJsonContext : JsonSerializerContext
{
}
