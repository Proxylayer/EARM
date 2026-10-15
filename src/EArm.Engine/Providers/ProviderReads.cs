using System;
using System.Text.Json.Nodes;
using EArm.Core.Secrets;

namespace EArm.Engine.Providers;

/// <summary>
/// Generic helpers over live reads: the <c>createdDateTime</c> fingerprint of an object, the check that a live object carries the key
/// it was looked up by (compared case-insensitively as Graph does), and the classification of a write failure whose outcome is unknown.
/// </summary>
public static class ProviderReads
{
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
