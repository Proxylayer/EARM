using System;
using System.Text.Json.Nodes;
using EArm.Core.Secrets;

namespace EArm.Engine.Providers;

/// <summary>
/// Generic helpers over live reads: the check that a live object carries the key it was looked up by (compared case-insensitively as
/// Graph does), and the classification of a write failure whose outcome is unknown.
/// </summary>
public static class ProviderReads
{
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
