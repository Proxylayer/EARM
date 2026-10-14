using System;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EntraArm.Engine.State;

/// <summary>
/// Gate on <c>schemaVersion</c> before a state file is deserialised: version 1 is read as is; a missing or
/// non-integer version means the file is not an EntraARM state file; a higher version was written by a newer
/// executor and is refused rather than silently misread (<see cref="StateSchemaUnsupportedException"/>). Future
/// versions add an upgrade step per version here.
/// </summary>
public static class StateMigrator
{
    public static StateDocument Read(JsonNode? root, string deploymentName)
    {
        ArgumentNullException.ThrowIfNull(deploymentName);
        if (root is not JsonObject obj)
        {
            throw new StateSchemaUnsupportedException(deploymentName, $"The state file of deployment '{deploymentName}' is not a JSON object.");
        }

        if (obj["schemaVersion"] is not JsonValue versionValue || !versionValue.TryGetValue<int>(out int version))
        {
            throw new StateSchemaUnsupportedException(deploymentName, $"The state file of deployment '{deploymentName}' has no integer 'schemaVersion'; it is not an EntraARM state file.");
        }

        if (version > StateFile.CurrentSchemaVersion)
        {
            throw new StateSchemaUnsupportedException(deploymentName, $"The state file of deployment '{deploymentName}' has schemaVersion {version}, but this version of entraarm understands up to {StateFile.CurrentSchemaVersion}. Upgrade entraarm.");
        }

        if (version < 1)
        {
            throw new StateSchemaUnsupportedException(deploymentName, $"The state file of deployment '{deploymentName}' has schemaVersion {version}; expected 1 or higher.");
        }

        // Version 1: the current shape. Later versions chain their upgrades before this point.
        try
        {
            return JsonSerializer.Deserialize(obj, StateJsonContext.Default.StateDocument)
                ?? throw new StateSchemaUnsupportedException(deploymentName, $"The state file of deployment '{deploymentName}' is empty.");
        }
        catch (JsonException ex)
        {
            throw new StateSchemaUnsupportedException(deploymentName, $"The state file of deployment '{deploymentName}' could not be read: {ex.Message}", ex);
        }
    }
}
