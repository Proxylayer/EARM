using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using EArm.Engine.Providers;
using EArm.Expressions.Values;

namespace EArm.Providers;

/// <summary>
/// The "declared paths only" view of an authentication method configuration. Graph PATCH merges top-level properties but replaces a complex property
/// whole, so a declared object that omits a sub-property would wipe it (the certificate binding <c>rules</c> under
/// <c>authenticationModeConfiguration</c> being the costly case). <see cref="FillUndeclared"/> copies every live sub-property the declaration does not
/// name into the desired properties before they are diffed and sent: nested objects recursively, and the members of the keyed arrays
/// (<c>includeTargets</c>, <c>excludeTargets</c>, <c>passkeyProfiles</c>: the overlay's <c>memberKey</c>) matched by their key. Top-level properties
/// that are not declared are left alone (they are not managed), and a declared value always wins: an explicit <c>[]</c> or <c>null</c> clears.
/// </summary>
public static class AuthenticationMethodConfigurationsShape
{
    /// <summary>Fills, in place, the undeclared sub-properties of the declared properties of <paramref name="desired"/> from <paramref name="live"/>.</summary>
    /// <param name="desired">The evaluated properties (changed in place).</param>
    /// <param name="live">The live (stripped) properties.</param>
    /// <param name="memberKey">The keyed arrays (dotted path relative to <c>properties</c> → key property).</param>
    public static void FillUndeclared(JsonObject desired, JsonObject? live, IReadOnlyDictionary<string, string> memberKey)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(memberKey);
        if (live is null)
        {
            return;
        }

        foreach (var property in live)
        {
            if (JsonObjects.FindKey(desired, property.Key) is { } key)
            {
                FillValue(desired[key], property.Value, key, memberKey);
            }
        }
    }

    private static void FillValue(JsonNode? desired, JsonNode? live, string dotted, IReadOnlyDictionary<string, string> memberKey)
    {
        if (desired is JsonObject desiredObject && live is JsonObject liveObject)
        {
            FillObject(desiredObject, liveObject, dotted, memberKey);
            return;
        }

        if (desired is not JsonArray desiredArray || live is not JsonArray liveArray || !memberKey.TryGetValue(dotted, out var keyProperty))
        {
            return;
        }

        foreach (var member in desiredArray)
        {
            if (member is not JsonObject desiredMember || JsonObjects.GetString(desiredMember, keyProperty) is not { } memberId)
            {
                continue;
            }

            foreach (var candidate in liveArray)
            {
                if (candidate is JsonObject liveMember && JsonObjects.KeyEquals(JsonObjects.GetString(liveMember, keyProperty), memberId))
                {
                    FillObject(desiredMember, liveMember, dotted, memberKey);
                    break;
                }
            }
        }
    }

    private static void FillObject(JsonObject desired, JsonObject live, string dotted, IReadOnlyDictionary<string, string> memberKey)
    {
        foreach (var property in live)
        {
            if (JsonObjects.FindKey(desired, property.Key) is { } key)
            {
                FillValue(desired[key], property.Value, dotted + "." + key, memberKey);
                continue;
            }

            if (!JsonObjects.IsAbsent(property.Value))
            {
                desired[property.Key] = JsonValues.Clone(property.Value);
            }
        }
    }
}
