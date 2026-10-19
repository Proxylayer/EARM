using System;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Core.Json;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using EArm.Expressions.Values;

namespace EArm.Providers;

/// <summary>Planning for <see cref="GroupsProvider"/>: the immutable kind checks, the dynamic membership pause, adoption by display name and the license delta.</summary>
public sealed partial class GroupsProvider
{
    // ---- plan ----

    public override ResourceChange Plan(ResourceInstance desired, ExistingResource? existing, PlanOptions options)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(options);
        _onSoftDeletedConflict = options.OnSoftDeletedConflict;

        // A live group without a uniqueName was found by display name (ReadAsync): it is diffed as if it already held the key, and the
        // adoption (setting uniqueName) is added below as its own delta.
        bool adopting = !desired.IsIgnored && existing is not null && string.IsNullOrEmpty(JsonObjects.GetString(existing.Raw, "uniqueName"));
        var change = base.Plan(desired, WithRoleAssignableDefault(adopting ? WithUniqueName(existing!, desired.Id.NameLeaf) : existing, desired.Properties), options);
        if (change.Type == ChangeType.Ignore || desired.Properties is not { } properties)
        {
            return change;
        }

        if (adopting && change.Before is not null)
        {
            change = change with { Before = RenderBefore(existing!.Properties, properties) };
        }

        var analysis = GroupsShape.Analyze(properties);
        if (existing is null)
        {
            if (!change.HasError && GroupsShape.CreateError(analysis, desired.Describe()) is { } createError)
            {
                change = Unsupported(change, createError);
            }
        }
        else if (!change.HasError)
        {
            JsonObjects.TryGetIgnoreCase(existing.Properties, "groupTypes", out var liveTypes);
            JsonObjects.TryGetIgnoreCase(properties, "groupTypes", out var desiredTypes);
            bool typesChange = change.Deltas.Any(d => d.HasEffect && d.Path.Equals(GroupTypesPath));
            if (typesChange && GroupsShape.UnifiedPresenceDiffers(liveTypes, desiredTypes))
            {
                change = Unsupported(change, $"Immutable property groupTypes ('Unified') of {desired.Describe()} differs from the live object: a group cannot be converted between a security group and a Microsoft 365 group; delete and recreate the resource.");
            }

            JsonObjects.TryGetIgnoreCase(existing.Properties, "visibility", out var liveVisibility);
            JsonObjects.TryGetIgnoreCase(properties, "visibility", out var desiredVisibility);
            if (!change.HasError && change.Deltas.Any(d => d.HasEffect && d.Path.Equals(VisibilityPath)) && GroupsShape.HiddenMembershipInvolved(liveVisibility, desiredVisibility))
            {
                change = Unsupported(change, $"Immutable property visibility of {desired.Describe()} differs from the live object: 'HiddenMembership' can only be set at creation and never changed.");
            }

            if (!change.HasError && IsDynamicChange(change, liveTypes, desiredTypes))
            {
                // A live group without a rule reads back membershipRule: null (Graph returns nulls for selected properties), which is no rule.
                if (analysis.IsDynamic && !analysis.HasMembershipRule && !(JsonObjects.TryGetIgnoreCase(existing.Properties, "membershipRule", out var liveRule) && !JsonObjects.IsAbsent(liveRule)))
                {
                    change = Unsupported(change, $"{desired.Describe()}: a dynamic group (groupTypes contains 'DynamicMembership') requires 'membershipRule'.");
                }
                else if (GroupsShape.GroupTypesContain(liveTypes, GroupsShape.DynamicMembership))
                {
                    change = change with { Reasons = [.. change.Reasons, "membership rule processing is paused (membershipRuleProcessingState: Paused) before the dynamic membership change"] };
                }
            }
        }

        if (!change.HasError && options.Mode == DeploymentMode.Complete && DeclaresEmptyOwners(desired))
        {
            change = Unsupported(change, $"{desired.Describe()}: properties.owners is an empty array. In Complete mode the list is authoritative, so an empty one would remove every owner, and Microsoft Graph refuses to remove the last user owner of a group (group-delete-owners). List at least one owner, leave owners out to keep the live owners untouched, or deploy in Incremental mode, where an empty list declares nothing.");
        }

        if (adopting && !change.HasError)
        {
            change = WithAdoption(change, desired, existing!);
        }

        return WithLicenseDelta(change, desired, existing);
    }

    /// <summary>
    /// Graph reads <c>isAssignableToRole: null</c> back for a group created without it (a group a script or the portal made as a plain security group),
    /// and null means not role-assignable (Learn, group resource: "Default is false"). When the template declares <c>isAssignableToRole: false</c>, a
    /// live null (or absent) value is compared as <c>false</c>: no change, rather than a change of an immutable property.
    /// </summary>
    private static ExistingResource? WithRoleAssignableDefault(ExistingResource? existing, JsonObject? desired)
    {
        if (existing is null
            || !JsonObjects.TryGetIgnoreCase(desired, "isAssignableToRole", out var declared)
            || !JsonValues.TryGetBool(declared, out bool assignable)
            || assignable)
        {
            return existing;
        }

        string key = JsonObjects.FindKey(existing.Properties, "isAssignableToRole") ?? "isAssignableToRole";
        if (existing.Properties is { } live && !JsonObjects.IsAbsent(live[key]))
        {
            return existing;
        }

        var properties = (JsonObject?)JsonValues.Clone(existing.Properties) ?? new JsonObject();
        properties[key] = false;
        return existing with { Properties = properties };
    }

    /// <summary>The live view of a group adopted by display name, with the uniqueName it is about to receive.</summary>
    private static ExistingResource WithUniqueName(ExistingResource existing, string uniqueName)
    {
        var properties = (JsonObject?)JsonValues.Clone(existing.Properties) ?? new JsonObject();
        properties[JsonObjects.FindKey(properties, "uniqueName") ?? "uniqueName"] = uniqueName;
        return existing with { Properties = properties };
    }

    /// <summary>
    /// Adds the adoption of a group found by display name: a <c>properties.uniqueName</c> delta (the update sets it with its own PATCH first) and a
    /// <see cref="ProviderWarnings.GroupAdoptedByDisplayName"/> reason, which what-if and create both report as a warning diagnostic.
    /// </summary>
    private static ResourceChange WithAdoption(ResourceChange change, ResourceInstance desired, ExistingResource existing)
    {
        string name = desired.Id.NameLeaf;
        string displayName = JsonObjects.GetString(existing.Raw, "displayName") ?? "?";
        string reason = $"{ProviderWarnings.GroupAdoptedByDisplayName}: adopting existing group '{displayName}' (Graph id {existing.GraphId}) by display name; uniqueName will be set to '{name}'";
        var adoption = new PropertyDelta(JsonPath.Of("properties", "uniqueName"), PropertyChangeType.Create, null, JsonValue.Create(name));
        return change with
        {
            Type = ChangeType.Modify,
            Mode = ChangeMode.Patch,
            Deltas = [.. change.Deltas, adoption],
            Reasons = [.. change.Reasons, reason],
        };
    }

    /// <summary>
    /// True when <c>owners</c> evaluates to an empty array (a literal <c>[]</c>, or an expression such as a parameter that defaults to <c>[]</c>). In
    /// Complete mode the target-side list is authoritative even when empty, so it asks the edge pass to remove every owner; Learn (group-delete-owners):
    /// "Once owners are assigned to a group, the last owner (a user object) of the group cannot be removed". Refused before anything is written rather
    /// than failing half-way. In Incremental mode an empty list declares nothing (the edge pass ignores it), so it is not refused.
    /// </summary>
    private static bool DeclaresEmptyOwners(ResourceInstance desired)
    {
        return JsonObjects.TryGetIgnoreCase(desired.EdgeProperties, "owners", out var owners) && owners is JsonArray { Count: 0 };
    }

    private static ResourceChange Unsupported(ResourceChange change, string message)
    {
        return change with { UnsupportedReason = message, Reasons = [.. change.Reasons, message] };
    }

    private static bool IsDynamicChange(ResourceChange change, JsonNode? liveTypes, JsonNode? desiredTypes)
    {
        bool typesChange = change.Deltas.Any(d => d.HasEffect && d.Path.Equals(GroupTypesPath));
        bool ruleChange = change.Deltas.Any(d => d.HasEffect && d.Path.Equals(MembershipRulePath));
        return ruleChange || (typesChange && GroupsShape.DynamicPresenceDiffers(liveTypes, desiredTypes));
    }

    /// <summary>Adds the <c>properties.assignedLicenses</c> array delta (action-backed, applied after the object write).</summary>
    private ResourceChange WithLicenseDelta(ResourceChange change, ResourceInstance desired, ExistingResource? existing)
    {
        if (!JsonObjects.TryGetIgnoreCase(desired.EdgeProperties, "assignedLicenses", out var declared) || declared is null)
        {
            return change;
        }

        if (UnknownJson.IsUnknown(declared))
        {
            return change with { IsUnknown = true, Reasons = [.. change.Reasons, "assignedLicenses is only known at deployment time"] };
        }

        if (declared is not JsonArray desiredLicenses)
        {
            return change.HasError ? change : Unsupported(change, $"{desired.Describe()}: properties.assignedLicenses must be an array of {{ skuId | skuPartNumber, disabledPlans }} objects.");
        }

        JsonObjects.TryGetIgnoreCase(existing?.Properties, "assignedLicenses", out var liveNode);
        var diff = GroupsLicensing.Diff(desiredLicenses, liveNode as JsonArray, _licensing.Cached);
        if (diff.Errors.Count > 0 && !change.HasError)
        {
            change = Unsupported(change, $"{desired.Describe()}: {string.Join(" ", diff.Errors)}");
        }

        if (diff.Children.Count == 0)
        {
            return change;
        }

        var delta = new PropertyDelta(LicensesPath, PropertyChangeType.Array, null, null, diff.HasUnknown, false, diff.Children);
        var type = change.Type == ChangeType.NoChange ? ChangeType.Modify : change.Type;
        return change with
        {
            Type = type,
            Mode = type == ChangeType.Modify && change.Mode == ChangeMode.None ? ChangeMode.Patch : change.Mode,
            Deltas = [.. change.Deltas, delta],
            IsUnknown = change.IsUnknown || diff.HasUnknown,
        };
    }
}
