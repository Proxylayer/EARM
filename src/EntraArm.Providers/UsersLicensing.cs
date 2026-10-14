using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using EntraArm.Engine.Providers;
using EntraArm.Expressions.Values;

namespace EntraArm.Providers;

/// <summary>
/// The user side of <c>assignedLicenses</c> (plan section 1.4): a user's live <c>assignedLicenses</c> merges direct
/// assignments with those inherited through group-based licensing, and only the direct ones are managed. Graph tells
/// them apart in <c>licenseAssignmentStates[]</c>: an entry whose <c>assignedByGroup</c> is <c>null</c> is a direct
/// assignment. The diff and the <c>assignLicense</c> call themselves are shared with groups (<see cref="GroupsLicensing"/>).
/// </summary>
internal static class UsersLicensing
{
    public const string AssignedLicenses = "assignedLicenses";
    public const string LicenseAssignmentStates = "licenseAssignmentStates";
    public const string AssignedByGroup = "assignedByGroup";
    public const string UsageLocation = "usageLocation";

    /// <summary>
    /// The user's direct license assignments as <c>[{ skuId, skuPartNumber?, disabledPlans }]</c>: the
    /// <c>licenseAssignmentStates</c> entries without <c>assignedByGroup</c> (de-duplicated by SKU, the state's own
    /// <c>disabledPlans</c>), carrying the <c>skuPartNumber</c> the enriched <c>assignedLicenses</c> entry knows.
    /// When the object carries no <c>licenseAssignmentStates</c> at all, every <c>assignedLicenses</c> entry counts
    /// as direct (there is nothing to tell them apart by).
    /// </summary>
    public static JsonArray DirectAssignments(JsonObject? raw)
    {
        var result = new JsonArray();
        if (raw is null)
        {
            return result;
        }

        JsonObjects.TryGetIgnoreCase(raw, AssignedLicenses, out var assignedNode);
        var assigned = assignedNode as JsonArray;
        if (!JsonObjects.TryGetIgnoreCase(raw, LicenseAssignmentStates, out var statesNode) || statesNode is not JsonArray states)
        {
            if (assigned is not null)
            {
                foreach (var item in assigned)
                {
                    if (item is JsonObject license)
                    {
                        result.Add(JsonValues.Clone(license));
                    }
                }
            }

            return result;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in states)
        {
            if (item is not JsonObject state)
            {
                continue;
            }

            if (JsonObjects.TryGetIgnoreCase(state, AssignedByGroup, out var group) && !JsonObjects.IsAbsent(group))
            {
                // Inherited through group-based licensing: never touched from the user side.
                continue;
            }

            string? skuId = JsonObjects.GetString(state, "skuId");
            if (skuId is null || !seen.Add(skuId))
            {
                continue;
            }

            var entry = new JsonObject { ["skuId"] = skuId };
            if (FindBySku(assigned, skuId) is { } license && JsonObjects.GetString(license, "skuPartNumber") is { } partNumber)
            {
                entry["skuPartNumber"] = partNumber;
            }

            JsonObjects.TryGetIgnoreCase(state, "disabledPlans", out var plans);
            entry["disabledPlans"] = plans is JsonArray disabled ? JsonValues.Clone(disabled) : new JsonArray();
            result.Add(entry);
        }

        return result;
    }

    /// <summary>
    /// True when the user will have no <c>usageLocation</c> once the object write lands (a license cannot be assigned
    /// without one): a declared <c>usageLocation</c> decides on its own (declaring <c>null</c> clears the live value
    /// right before <c>assignLicense</c>, so the live value cannot rescue it); an undeclared one falls back to the
    /// live object. The template checker already requires the property to be declared alongside
    /// <c>assignedLicenses</c>; this is the evaluated-value check behind it.
    /// </summary>
    public static bool MissingUsageLocation(JsonObject? desired, JsonObject? liveRaw)
    {
        if (JsonObjects.TryGetIgnoreCase(desired, UsageLocation, out var declared))
        {
            return JsonObjects.IsAbsent(declared);
        }

        return !JsonObjects.TryGetIgnoreCase(liveRaw, UsageLocation, out var live) || JsonObjects.IsAbsent(live);
    }

    private static JsonObject? FindBySku(JsonArray? licenses, string skuId)
    {
        if (licenses is null)
        {
            return null;
        }

        foreach (var item in licenses)
        {
            if (item is JsonObject license && JsonObjects.KeyEquals(JsonObjects.GetString(license, "skuId"), skuId))
            {
                return license;
            }
        }

        return null;
    }
}
