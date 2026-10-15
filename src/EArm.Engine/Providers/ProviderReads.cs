using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Core.Secrets;

namespace EArm.Engine.Providers;

/// <summary>The result of a pinned read that was checked against the desired key.</summary>
/// <param name="Existing">The live object to use (<c>null</c> when none).</param>
/// <param name="Mismatch">When not <c>null</c>: why the object found under the pinned id was discarded (it is not the object the key names).</param>
/// <param name="Superseded">
/// The object under the pinned id does not have the desired key but is exactly what this deployment recorded for the resource
/// (a composite-key type whose immutable key properties changed): it is kept as the object to replace, never to update.
/// </param>
/// <param name="Renamed">
/// The pinned object does not carry the desired key and no object does: the managed object was renamed (or its key changed) outside the template, or the state was edited.
/// The caller must NOT plan a create (that would duplicate the object and orphan the original, review round 2 finding S2-04) but fail the resource (<c>StateEntryMismatch</c>).
/// </param>
public sealed record VerifiedRead(ExistingResource? Existing, string? Mismatch, bool Superseded = false, bool Renamed = false);

/// <summary>
/// The generic check that a state-pinned Graph id still names the object the resource's key says it should (review finding
/// S-01b). A state file is committed and can be edited: without this check a tampered or stale pinned id would make a
/// run PATCH or delete an arbitrary object (the signed-in user, a foreign role assignment...). After a read by pinned id the
/// returned object's key properties (the keys of <see cref="ResourceKey.Values"/>: <c>uniqueName</c>, <c>appId</c>,
/// <c>userPrincipalName</c>, <c>displayName</c>, the composite members...) must match the desired ones, compared
/// case-insensitively as Graph does; a mismatch discards the pinned result and falls back to a lookup by key.
/// </summary>
public static class ProviderReads
{
    /// <summary>
    /// Reads through <paramref name="read"/> (called with the pinned id, then, after a mismatch, with <c>null</c>) and applies the key check
    /// to a pinned result.
    /// </summary>
    /// <param name="keyCanBeRenamed">
    /// <c>true</c> for a name-keyed type (<c>nameMapsTo</c>: a UPN, a displayName, a uniqueName), whose key can be changed out of band: when the pinned object no longer has the key and no
    /// object has it, the result is <see cref="VerifiedRead.Renamed"/> instead of "create". A composite-keyed type has no rename: a key that differs is a new object.
    /// </param>
    public static async Task<VerifiedRead> ReadVerifiedAsync(ResourceKey key, string? stateId, Func<string?, Task<ExistingResource?>> read, Func<ExistingResource, bool>? acceptSuperseded = null, DateTimeOffset? recordedCreated = null, bool keyCanBeRenamed = false)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(read);

        var existing = await read(stateId).ConfigureAwait(false);
        string? reason = null;
        if (stateId is null || existing is null || (KeyMatches(key, existing.Raw, out reason) && FingerprintMatches(recordedCreated, existing.Raw, out reason)))
        {
            return new VerifiedRead(existing, null);
        }

        // The pinned id names something else: never plan an update against it, find the real object by key.
        if (acceptSuperseded is not null && acceptSuperseded(existing))
        {
            var current = await read(null).ConfigureAwait(false);
            return current is null
                ? new VerifiedRead(existing, null, Superseded: true)
                : new VerifiedRead(current, $"the state pins Graph id {stateId}, but that object {reason}; the object the key names was used instead");
        }

        var byKey = await read(null).ConfigureAwait(false);
        if (byKey is null && keyCanBeRenamed)
        {
            return new VerifiedRead(null, $"the state pins Graph id {stateId}, but that object {reason}, and no object has the key {key.Describe()}: object {stateId} was renamed outside the template (or its key was changed, or the state was edited). Restore its name, or run 'earm state forget' for this resource and let the next deployment adopt or create it", Renamed: true);
        }

        return new VerifiedRead(byKey, $"the state pins Graph id {stateId}, but that object {reason}; the pinned id was ignored");
    }

    /// <summary>The <c>createdDateTime</c> of a live object (the immutable fingerprint recorded in the state), or <c>null</c> when it has none.</summary>
    public static DateTimeOffset? CreatedDateTimeOf(JsonObject? raw)
    {
        if (raw is null || !JsonObjects.TryGetIgnoreCase(raw, "createdDateTime", out var node) || node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<DateTimeOffset>(out var direct))
        {
            return direct;
        }

        return value.TryGetValue<string>(out string? text)
            && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>
    /// True when no fingerprint was recorded, the live object does not report one, or both agree. A live object created at another time than the
    /// entry recorded is another object (an entry re-pointed by editing the state), whatever its key says.
    /// </summary>
    public static bool FingerprintMatches(DateTimeOffset? recorded, JsonObject live, out string? reason)
    {
        reason = null;
        if (recorded is not { } expected || CreatedDateTimeOf(live) is not { } actual || actual == expected)
        {
            return true;
        }

        reason = $"was created {actual:O} instead of {expected:O} (it is not the object the state recorded)";
        return false;
    }

    /// <summary>
    /// True when every key value recorded in the state entry is still carried by the live object (so the object is the one this
    /// deployment recorded, even though the template now asks for a different key). At least one value must verify.
    /// </summary>
    public static bool RecordedKeyMatches(IReadOnlyDictionary<string, string> recorded, JsonObject live)
    {
        ArgumentNullException.ThrowIfNull(recorded);
        ArgumentNullException.ThrowIfNull(live);
        int verified = 0;
        foreach (var (property, expected) in recorded)
        {
            if (!JsonObjects.TryGetIgnoreCase(live, property, out var node) || node is null || SecretJson.IsSecret(node))
            {
                return false;
            }

            string? actual = node is JsonValue value && value.TryGetValue<string>(out string? text) ? text : node.ToJsonString();
            if (!string.Equals(actual?.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            verified++;
        }

        return verified > 0;
    }

    /// <summary>
    /// True when the live object carries every key value that can be verified and at least one could be: a key property the
    /// live object does not expose is skipped, but an object that exposes none of them cannot be shown to be the right one.
    /// </summary>
    public static bool KeyMatches(ResourceKey key, JsonObject live, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(live);
        reason = null;
        int verified = 0;
        foreach (var (property, expected) in key.Values)
        {
            if (!JsonObjects.TryGetIgnoreCase(live, property, out var node) || node is null || SecretJson.IsSecret(node))
            {
                continue;
            }

            string? actual = node is JsonValue value && value.TryGetValue<string>(out string? text) ? text : node.ToJsonString();
            if (!string.Equals(actual?.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                reason = $"has {property} '{actual}' instead of '{expected}'";
                return false;
            }

            verified++;
        }

        if (verified == 0 && key.Values.Count > 0)
        {
            reason = $"does not expose the key ({string.Join(", ", key.Values.Keys)}) it would have to match";
            return false;
        }

        return true;
    }

    /// <summary>True for a Graph exception or transport failure whose outcome for a write is unknown (timeout, connection loss, 5xx).</summary>
    public static bool IsAmbiguousWriteFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            EArm.Graph.GraphTransportException transport => transport.Ambiguous || transport.Failure is EArm.Graph.GraphTransportFailure.Network or EArm.Graph.GraphTransportFailure.Timeout or EArm.Graph.GraphTransportFailure.InvalidResponse or EArm.Graph.GraphTransportFailure.ResponseTooLarge,
            EArm.Graph.GraphException graph => graph.IsAmbiguous || (int)graph.Status >= 500,
            TimeoutException => true,
            _ => false,
        };
    }
}
