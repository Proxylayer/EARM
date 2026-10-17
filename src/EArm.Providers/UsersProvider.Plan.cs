using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Engine.Providers;
using EArm.Expressions.Values;

namespace EArm.Providers;

/// <summary>Planning for <see cref="UsersProvider"/>: the user diff and the license delta.</summary>
public sealed partial class UsersProvider
{
    // ---- plan ----

    public override ResourceChange Plan(ResourceInstance desired, ExistingResource? existing, PlanOptions options)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(options);
        _planOptions = options;

        if (desired.IsIgnored || desired.Properties is not { } properties)
        {
            return base.Plan(desired, existing, options);
        }

        // A literal password must never be seen in plain text after this point (what-if rows, messages).
        UsersRules.TaintSecrets(properties, Definition.Secure);

        string name = desired.Id.NameLeaf;
        if (!desired.IsSymbolic && UsersRules.ValidateUserPrincipalName(name) is { } shapeError)
        {
            return Unsupported(desired, existing, $"{desired.Describe()}: {shapeError}");
        }

        if (UsersRules.DeclaredNameConflict(properties, name, desired.Describe()) is { } conflict)
        {
            return Unsupported(desired, existing, conflict);
        }

        var reasons = new List<string>();
        if (existing is null)
        {
            UsersRules.ApplyCreatedStateDefaults(properties);
            if (!desired.IsSymbolic)
            {
                UsersRules.ApplyCreateDefaults(properties, name);
            }

            if (!UsersRules.HasPassword(properties))
            {
                reasons.Add($"no passwordProfile.password is declared: a random {UsersRules.GeneratedPasswordLength}-character password is generated on create and published as reference('{name}').passwordProfile.password for a securestring output");
            }

            var create = base.Plan(desired, null, options);
            return WithLicenseDelta(reasons.Count == 0 ? create : create with { Reasons = [.. create.Reasons, .. reasons] }, desired, null);
        }

        string? liveUpn = JsonObjects.GetString(existing.Raw, UsersRules.UserPrincipalName);
        if (liveUpn is not null && !liveUpn.Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            return Unsupported(desired, existing, $"{desired.Describe()}: the live user ({existing.GraphId}) has userPrincipalName '{liveUpn}', which cannot be changed to '{name}' (userPrincipalName is immutable); restore the original name, or delete and recreate the resource.");
        }

        var change = base.Plan(desired, existing, options);
        if (change.HasError)
        {
            return change;
        }

        if (change.Deltas.FirstOrDefault(d => d.HasEffect && d.Path.Equals(UserTypePath)) is { } userType)
        {
            reasons.Add(UsersRules.UserTypeChangeReason(userType.Before, userType.After));
        }

        return WithLicenseDelta(reasons.Count == 0 ? change : change with { Reasons = [.. change.Reasons, .. reasons] }, desired, existing);
    }

    /// <summary>
    /// Adds the <c>properties.assignedLicenses</c> array delta (action-backed, applied after the
    /// object write) against the user's <b>direct</b> assignments; a non-empty declaration without a
    /// <c>usageLocation</c> is unsupported.
    /// </summary>
    private ResourceChange WithLicenseDelta(ResourceChange change, ResourceInstance desired, ExistingResource? existing)
    {
        if (!JsonObjects.TryGetIgnoreCase(desired.EdgeProperties, UsersLicensing.AssignedLicenses, out var declared) || declared is null)
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

        if (desiredLicenses.Count > 0 && !change.HasError && UsersLicensing.MissingUsageLocation(desired.Properties, existing?.Raw))
        {
            return Unsupported(change, $"{desired.Describe()}: assignedLicenses requires usageLocation (ISO 3166-1 alpha-2) on the user; declare properties.usageLocation.");
        }

        var liveDirect = UsersLicensing.DirectAssignments(existing?.Raw);
        var diff = GroupsLicensing.Diff(desiredLicenses, liveDirect, _licensing.Cached);
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
