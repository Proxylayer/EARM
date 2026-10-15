using System;
using EArm.Engine.Planning;
using EArm.Engine.Providers;

namespace EArm.Engine.State;

/// <summary>
/// Replacing an object (an immutable property changed on a type that is deleted and created again) deletes the old object, so it is guarded like every state-driven
/// delete (review round 2, S2-03): the same checks run in the what-if and in the executor, and a recreate counts against <c>--max-deletes</c>.
/// </summary>
public static class RecreateRules
{
    /// <summary>
    /// The entry that describes the object about to be replaced: what the state recorded when it vouches for exactly this object, otherwise an entry built from the live
    /// object with <see cref="StateProvenance.Adopted"/> (the state does not know the object: this deployment did not create it).
    /// </summary>
    public static StateEntry OldEntryFor(ResourceInstance instance, ExistingResource existing, ResourceKey key, StateEntry? previousEntry)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(key);
        if (previousEntry is not null && string.Equals(previousEntry.GraphId, existing.GraphId, StringComparison.OrdinalIgnoreCase))
        {
            return previousEntry;
        }

        return new StateEntry
        {
            Id = instance.Id,
            Type = instance.Type,
            ApiVersion = instance.ApiVersion,
            Key = key.Values,
            GraphId = existing.GraphId,
            ParentGraphId = key.ParentGraphId,
            Provenance = StateProvenance.Adopted,
            CreatedDateTime = ProviderReads.CreatedDateTimeOf(existing.Raw),
        };
    }

    /// <summary>The refusal text when the guard blocks the delete of the old object (nothing is created or changed in that case).</summary>
    public static string RefusalMessage(ResourceInstance instance, StateEntry oldEntry, DeleteOutcome blocked)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(oldEntry);
        ArgumentNullException.ThrowIfNull(blocked);
        string why = blocked.Status == DeleteStatus.Refused && blocked.ErrorCode == EngineDiagnosticCodes.AdoptedObjectNotDeleted
            ? $"it is an object this deployment did not create ({oldEntry.GraphId}); replacing it deletes it, which needs --delete-adopted"
            : blocked.Error ?? "the old object may not be deleted";
        return $"{instance.Describe()}: an immutable property changed, so the object must be deleted and created again, but {why}. Nothing was changed.";
    }
}
