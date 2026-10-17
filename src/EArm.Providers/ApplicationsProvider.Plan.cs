using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Core.Diagnostics;
using EArm.Core.Json;
using EArm.Core.Secrets;
using EArm.Engine.Providers;
using EArm.Expressions.Values;

namespace EArm.Providers;

/// <summary>Planning for <see cref="ApplicationsProvider"/>: the application diff over the normalized live object, the declared name conflicts and the unsupported changes.</summary>
public sealed partial class ApplicationsProvider
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

        string name = desired.Id.NameLeaf;
        if (DeclaredNameConflict(properties, name, desired) is { } conflict)
        {
            return Unsupported(desired, existing, conflict);
        }

        ApplicationsRules.NormalizeDesired(properties, desired.IsSymbolic ? null : name);

        var duplicates = ApplicationsRules.DuplicateValues(properties);
        if (duplicates.Count > 0)
        {
            string list = string.Join(", ", duplicates.Select(v => "'" + v + "'"));
            return Unsupported(desired, existing, $"{desired.Describe()}: value{(duplicates.Count == 1 ? string.Empty : "s")} {list} occur{(duplicates.Count == 1 ? "s" : string.Empty)} more than once across appRoles and api.oauth2PermissionScopes; Graph requires 'value' to be unique across both.");
        }

        var reasons = new List<string>();
        if (existing is null)
        {
            if (JsonObjects.GetString(properties, "appId") is { } declaredAppId)
            {
                return Unsupported(desired, null, $"{desired.Describe()}: properties.appId ('{declaredAppId}') is declared but no application with uniqueName '{name}' or that appId exists; Graph assigns the appId on create, so remove the property, or declare the appId of an existing application to adopt it.");
            }

            if (ApplicationsRules.HasDeferredIdentifierUris(properties))
            {
                reasons.Add($"identifierUris containing '{ApplicationsRules.AppIdPlaceholder}' are written by a second PATCH once Graph has assigned the appId");
            }

            var create = base.Plan(desired, null, options);
            return reasons.Count == 0 ? create : create with { Reasons = [.. create.Reasons, .. reasons] };
        }

        if (JsonObjects.GetString(existing.Raw, "appId") is { } liveAppId)
        {
            ApplicationsRules.SubstituteAppId(properties, liveAppId);
        }

        string? liveUniqueName = JsonObjects.GetString(existing.Raw, "uniqueName");
        if (liveUniqueName is not null && !string.Equals(liveUniqueName, name, StringComparison.OrdinalIgnoreCase))
        {
            return Unsupported(desired, existing, $"{desired.Describe()}: the live application ({existing.GraphId}) has uniqueName '{liveUniqueName}', which cannot be changed to '{name}' (uniqueName is immutable).");
        }

        var normalized = NormalizedExisting(existing, properties);
        var change = base.Plan(desired, normalized, options);
        if (change.Before is not null)
        {
            // The diff ran on the view without the ignoreLiveOnly children; the what-if row still shows the whole live object.
            change = change with { Before = RenderBefore(ApplicationsRules.NormalizeLive(existing, properties), properties) };
        }

        if (change.HasError)
        {
            return change;
        }

        if (liveUniqueName is null)
        {
            // Brownfield adoption: the object was found by appId; the update sets its uniqueName.
            reasons.Add($"the existing application ({existing.GraphId}) has no uniqueName; it is adopted by setting uniqueName to '{name}'");
            var adoption = new PropertyDelta(JsonPath.Of("properties", "uniqueName"), PropertyChangeType.Create, null, JsonValue.Create(name));
            change = change with { Type = ChangeType.Modify, Mode = ChangeMode.Patch, Deltas = [.. change.Deltas, adoption] };
        }

        if (change.Type == ChangeType.Modify)
        {
            if (ApplicationsRules.DisableStep(ApplicationsRules.AppRolesOf(properties), ApplicationsRules.AppRolesOf(normalized.Properties), ApplicationsRules.AppRoleBreakingProperties) is not null)
            {
                reasons.Add("appRoles that are removed or change value/allowedMemberTypes are disabled by a first PATCH before the final array is written");
            }

            if (ApplicationsRules.DisableStep(ApplicationsRules.ScopesOf(properties), ApplicationsRules.ScopesOf(normalized.Properties), ApplicationsRules.ScopeBreakingProperties) is not null)
            {
                reasons.Add("api.oauth2PermissionScopes that are removed or change value/type are disabled by a first PATCH before the final array is written");
            }
        }

        return reasons.Count == 0 ? change : change with { Reasons = [.. change.Reasons, .. reasons] };
    }

    private ExistingResource NormalizedExisting(ExistingResource existing, JsonObject desired)
    {
        return ProviderSupport.PruneLiveOnly(Definition, desired, existing with { Properties = ApplicationsRules.NormalizeLive(existing, desired) });
    }

    /// <summary>Mirrors <see cref="ProviderBase.InjectName"/> at plan time: a declared <c>uniqueName</c> must equal the resource name.</summary>
    private static string? DeclaredNameConflict(JsonObject properties, string name, ResourceInstance desired)
    {
        if (!JsonObjects.TryGetIgnoreCase(properties, "uniqueName", out var declared) || JsonObjects.IsAbsent(declared) || SecretJson.IsSecret(declared) || UnknownJson.IsUnknown(declared))
        {
            return null;
        }

        if (JsonValues.TryGetString(declared, out var text) && string.Equals(text, name, StringComparison.Ordinal))
        {
            return null;
        }

        return $"{desired.Describe()}: properties.uniqueName ({JsonValues.Render(declared)}) conflicts with the resource name '{name}'; the name is written to uniqueName, so either omit the property or make it equal ({DiagnosticCodes.NameMappedPropertyConflict}).";
    }

    private ResourceChange Unsupported(ResourceInstance desired, ExistingResource? existing, string message)
    {
        return new ResourceChange
        {
            Id = desired.Id,
            ResourceType = TypeName,
            Type = existing is null ? ChangeType.Create : ChangeType.Modify,
            Mode = existing is null ? ChangeMode.Create : ChangeMode.Patch,
            GraphId = existing?.GraphId,
            Before = existing is null ? null : RenderBefore(existing.Properties, desired.Properties),
            After = JsonDiff.RenderObject(desired.Properties),
            IsUnknown = desired.HasUnknownProperties || desired.IsSymbolic,
            IsSymbolicId = desired.IsSymbolic,
            Reasons = [message],
            UnsupportedReason = message,
        };
    }
}
