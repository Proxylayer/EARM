using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using EntraArm.Core;
using EntraArm.Core.Secrets;
using EntraArm.Engine.Execution;
using EntraArm.Engine.Providers;
using EntraArm.Expressions.Values;

namespace EntraArm.Engine.State;

/// <summary>
/// Maps <see cref="StateFile"/> to and from its on-disk document (<see cref="StateDocument"/>). Writing asserts that
/// no secret (<see cref="SecretJson"/>) or deploy-time placeholder (<see cref="UnknownJson"/>) is about to be
/// persisted; reading goes through <see cref="StateMigrator"/>.
/// </summary>
public static class StateSerializer
{
    public static string Serialize(StateFile state)
    {
        ArgumentNullException.ThrowIfNull(state);
        AssertPersistable(state);
        return JsonSerializer.Serialize(ToDocument(state), StateJsonContext.Default.StateDocument);
    }

    public static StateFile Deserialize(string json, string deploymentName)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(deploymentName);

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException ex)
        {
            throw new StateSchemaUnsupportedException(deploymentName, $"The state file of deployment '{deploymentName}' is not valid JSON: {ex.Message}", ex);
        }

        return FromDocument(StateMigrator.Read(root, deploymentName), deploymentName);
    }

    /// <summary>Throws when any entry carries a secret or a deploy-time placeholder: neither may ever reach the disk.</summary>
    public static void AssertPersistable(StateFile state)
    {
        ArgumentNullException.ThrowIfNull(state);
        foreach (var entry in state.Resources.Values)
        {
            if (SecretJson.ContainsSecret(entry.Extra) || SecretJson.ContainsSecret(entry.Relationships))
            {
                throw new InvalidOperationException($"The state entry for '{entry.Id.Id}' carries a secret value; the state file must never contain secrets.");
            }

            if (UnknownJson.ContainsUnknown(entry.Extra) || UnknownJson.ContainsUnknown(entry.Relationships))
            {
                throw new InvalidOperationException($"The state entry for '{entry.Id.Id}' carries a deploy-time placeholder; only resolved values may be persisted.");
            }
        }
    }

    public static StateDocument ToDocument(StateFile state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var document = new StateDocument
        {
            SchemaVersion = state.SchemaVersion,
            DeploymentName = state.DeploymentName,
            TenantId = state.TenantId,
            CreatedOn = state.CreatedOn,
            LastDeployedOn = state.LastDeployedOn,
            LastStatus = state.LastStatus,
            LastMode = state.LastMode,
            LastTemplateHash = state.LastTemplateHash,
        };

        foreach (var pair in state.Resources.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var entry = pair.Value;
            document.Resources[pair.Key] = new StateResourceDocument
            {
                Type = entry.Type,
                ApiVersion = entry.ApiVersion,
                Key = new Dictionary<string, string>(entry.Key, StringComparer.OrdinalIgnoreCase),
                Id = entry.GraphId,
                ParentId = entry.ParentGraphId,
                ContentHash = entry.ContentHash,
                DependsOn = [.. entry.DependsOn.Order(StringComparer.Ordinal)],
                LastWriteOn = entry.LastWriteOn,
                Extra = entry.Extra?.DeepClone().AsObject(),
                Relationships = entry.Relationships?.DeepClone().AsObject(),
            };
        }

        foreach (var operation in state.Operations)
        {
            document.Operations.Add(new StateOperationDocument
            {
                ResourceId = operation.ResourceId,
                Type = operation.Type,
                Status = operation.Status.ToString(),
                ChangeType = operation.ChangeType?.ToString(),
                Batch = operation.Batch,
                StartedOn = operation.StartedOn,
                FinishedOn = operation.FinishedOn,
                GraphId = operation.GraphId,
                Error = operation.Error,
                ErrorCode = operation.ErrorCode,
            });
        }

        return document;
    }

    public static StateFile FromDocument(StateDocument document, string deploymentName)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(deploymentName);

        var state = new StateFile
        {
            DeploymentName = string.IsNullOrWhiteSpace(document.DeploymentName) ? deploymentName : document.DeploymentName,
            SchemaVersion = document.SchemaVersion,
            TenantId = document.TenantId,
            CreatedOn = document.CreatedOn,
            LastDeployedOn = document.LastDeployedOn,
            LastStatus = document.LastStatus,
            LastMode = document.LastMode,
            LastTemplateHash = document.LastTemplateHash,
        };

        foreach (var pair in document.Resources)
        {
            var resource = pair.Value;
            if (string.IsNullOrWhiteSpace(resource.Type) || string.IsNullOrWhiteSpace(resource.Id))
            {
                throw new StateSchemaUnsupportedException(deploymentName, $"The state entry '{pair.Key}' has no type or Graph id.");
            }

            var id = ParseResourceId(pair.Key, resource.Type)
                ?? throw new StateSchemaUnsupportedException(deploymentName, $"The state entry key '{pair.Key}' is not a resource id of type '{resource.Type}'.");

            state.Resources[pair.Key] = new StateEntry
            {
                Id = id,
                Type = id.Type,
                ApiVersion = resource.ApiVersion,
                Key = new Dictionary<string, string>(resource.Key, StringComparer.OrdinalIgnoreCase),
                GraphId = resource.Id,
                ParentGraphId = resource.ParentId,
                ContentHash = resource.ContentHash,
                DependsOn = [.. resource.DependsOn],
                LastWriteOn = resource.LastWriteOn,
                Extra = resource.Extra,
                Relationships = resource.Relationships,
            };
        }

        foreach (var operation in document.Operations)
        {
            state.Operations.Add(new OperationRecord
            {
                ResourceId = operation.ResourceId,
                Type = operation.Type,
                Status = Enum.TryParse<OperationStatus>(operation.Status, ignoreCase: true, out var status) ? status : OperationStatus.Failed,
                ChangeType = Enum.TryParse<ChangeType>(operation.ChangeType, ignoreCase: true, out var changeType) ? changeType : null,
                Batch = operation.Batch,
                StartedOn = operation.StartedOn,
                FinishedOn = operation.FinishedOn,
                GraphId = operation.GraphId,
                Error = operation.Error,
                ErrorCode = operation.ErrorCode,
            });
        }

        return state;
    }

    /// <summary>
    /// Rebuilds the identifier from a state key and the entry's type: the type says how many name segments the id
    /// carries (<c>/providers/Microsoft.Entra/conditionalAccess/policies/CA-1</c> has one; a child id has two), so no
    /// catalog is needed and unknown types still round-trip.
    /// </summary>
    public static ResourceIdentifier? ParseResourceId(string resourceId, string type)
    {
        ArgumentNullException.ThrowIfNull(resourceId);
        ArgumentNullException.ThrowIfNull(type);

        string normalizedType = ResourceIdentifier.NormalizeType(type);
        string prefix = ResourceIdentifier.ProvidersPrefix + ResourceIdentifier.ProviderNamespace + "/";
        if (!resourceId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var typeSegments = normalizedType.Split('/').Skip(1).ToArray();
        var idSegments = resourceId[prefix.Length..].Split('/');
        int nameCount = idSegments.Length - typeSegments.Length;
        if (typeSegments.Length == 0 || nameCount < 1 || nameCount > typeSegments.Length)
        {
            return null;
        }

        int unpaired = typeSegments.Length - nameCount;
        var names = new List<string>(nameCount);
        int index = 0;
        for (int i = 0; i < typeSegments.Length; i++)
        {
            if (!idSegments[index].Equals(typeSegments[i], StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            index++;
            if (i >= unpaired)
            {
                names.Add(idSegments[index]);
                index++;
            }
        }

        try
        {
            return new ResourceIdentifier(normalizedType, names);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>A compact UTC timestamp for messages.</summary>
    public static string Timestamp(DateTimeOffset? value)
    {
        return value is { } v ? v.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture) : "-";
    }
}
