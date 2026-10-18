using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Core.Diagnostics;
using EArm.Core.Json;
using EArm.Core.Secrets;
using EArm.Engine;
using EArm.Engine.Providers;
using EArm.Expressions.Values;

namespace EArm.Providers;

/// <summary>Planning for <see cref="PasswordCredentialsProvider"/>: rotation, the same-name credentials, the after view and the unsupported changes.</summary>
public sealed partial class PasswordCredentialsProvider
{
    // ---- plan ----

    public override ResourceChange Plan(ResourceInstance desired, ExistingResource? existing, PlanOptions options)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(options);

        if (desired.IsIgnored || desired.IsSymbolic || desired.Properties is not { } properties)
        {
            return base.Plan(desired, existing, options);
        }

        string name = desired.Id.NameLeaf;
        if (desired.Id.Parent is null)
        {
            return Unsupported(desired, existing, $"{desired.Describe()}: the name must be '<application uniqueName>/<credential displayName>' (or the credential must be nested under its application).");
        }

        if (DeclaredNameConflict(properties, name, desired) is { } conflict)
        {
            return Unsupported(desired, existing, conflict);
        }

        if (desired.HasUnknownProperties)
        {
            if (existing is null)
            {
                return base.Plan(desired, null, options);
            }

            // Whether the secret is rotated depends on values that are only known at deployment time.
            return new ResourceChange
            {
                Id = desired.Id,
                ResourceType = TypeName,
                Type = ChangeType.Modify,
                Mode = ChangeMode.Patch,
                GraphId = existing.GraphId,
                Before = JsonDiff.RenderObject(existing.Properties),
                After = JsonDiff.RenderObject(properties),
                IsUnknown = true,
                Reasons = ["some property values are only known at deployment time; whether the secret is rotated is decided when the credential is applied"],
            };
        }

        string? problem = PasswordCredentialsRules.DescribeDesired(properties, out var parsed);
        if (problem is not null || parsed is null)
        {
            return Unsupported(desired, existing, $"{desired.Describe()}: {problem ?? "the properties could not be interpreted."}");
        }

        if (PasswordCredentialsRules.NameProblem(name, parsed.RotationTag) is { } nameProblem)
        {
            return Unsupported(desired, existing, $"{desired.Describe()}: {nameProblem} ({DiagnosticCodes.InvalidResourceName})");
        }

        if (existing is null)
        {
            // The shared Create row (deltas, required-on-create check), with the name shown like the other views.
            return base.Plan(desired, null, options) with { After = AfterView(properties, name, parsed) };
        }

        var live = PasswordCredentialsRules.SummaryOf(existing.Raw)
            ?? throw new ProviderException(EngineDiagnosticCodes.ReadFailed, $"{desired.Describe()}: the live credential carries no keyId.", desired.Id);
        var assessment = PasswordCredentialsRules.AssessRotation(parsed, live, Clock.GetUtcNow());
        var triggers = assessment.Triggers;
        var warningReasons = assessment.Warnings.Select(w => ProviderWarnings.Format(w.Code, $"{desired.Describe()}: {w.Message}")).ToList();

        var before = PasswordCredentialsRules.DiffView(live);
        var after = AfterView(properties, name, parsed);
        if (triggers.Count == 0)
        {
            return new ResourceChange
            {
                Id = desired.Id,
                ResourceType = TypeName,
                Type = ChangeType.NoChange,
                Mode = ChangeMode.None,
                Before = before,
                After = after,
                GraphId = existing.GraphId,
                Reasons = warningReasons,
            };
        }

        var deltas = new List<PropertyDelta>();
        var reasons = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var trigger in triggers)
        {
            reasons.Add(trigger.Reason);
            if (!seen.Add(trigger.Property))
            {
                continue;
            }

            var kind = trigger.Before is null ? PropertyChangeType.Create : trigger.After is null ? PropertyChangeType.Delete : PropertyChangeType.Modify;
            deltas.Add(new PropertyDelta(JsonPath.Of("properties", trigger.Property), kind, trigger.Before, trigger.After));
        }

        return new ResourceChange
        {
            Id = desired.Id,
            ResourceType = TypeName,
            Type = ChangeType.Modify,
            Mode = ChangeMode.Patch,
            Deltas = deltas,
            Before = before,
            After = after,
            GraphId = existing.GraphId,
            Reasons = ["the secret is rotated (a new credential is added, then the previous one removed): " + string.Join("; ", reasons), .. warningReasons],
        };
    }

    /// <summary>
    /// The desired side of the what-if row: the declared properties with the live <c>displayName</c> (<c>name#rotationTag</c>) injected and
    /// the dates in canonical form, so it lines up with the live side (<see cref="PasswordCredentialsRules.DiffView"/>) tag for tag.
    /// </summary>
    private static JsonObject AfterView(JsonObject properties, string name, DesiredPasswordCredential parsed)
    {
        var after = (JsonObject)JsonValues.Clone(properties)!;
        string liveName = PasswordCredentialsRules.NameProblem(name, parsed.RotationTag) is null ? PasswordCredentialsRules.LiveDisplayName(name, parsed.RotationTag) : name;
        after[JsonObjects.FindKey(after, PasswordCredentialsRules.DisplayNameProperty) ?? PasswordCredentialsRules.DisplayNameProperty] = liveName;
        if (parsed.EndDateTime is { } end)
        {
            after[JsonObjects.FindKey(after, PasswordCredentialsRules.EndDateTimeProperty) ?? PasswordCredentialsRules.EndDateTimeProperty] = PasswordCredentialsRules.Format(end);
        }

        if (parsed.StartDateTime is { } start)
        {
            after[JsonObjects.FindKey(after, PasswordCredentialsRules.StartDateTimeProperty) ?? PasswordCredentialsRules.StartDateTimeProperty] = PasswordCredentialsRules.Format(start);
        }

        return JsonDiff.RenderObject(after)!;
    }

    /// <summary>Mirrors <see cref="ProviderBase.InjectName"/> at plan time: a declared <c>displayName</c> must equal the credential name.</summary>
    private static string? DeclaredNameConflict(JsonObject properties, string name, ResourceInstance desired)
    {
        if (!JsonObjects.TryGetIgnoreCase(properties, PasswordCredentialsRules.DisplayNameProperty, out var declared) || JsonObjects.IsAbsent(declared) || SecretJson.IsSecret(declared) || UnknownJson.IsUnknown(declared))
        {
            return null;
        }

        if (JsonValues.TryGetString(declared, out var text) && string.Equals(text, name, StringComparison.Ordinal))
        {
            return null;
        }

        return $"{desired.Describe()}: properties.displayName ({JsonValues.Render(declared)}) conflicts with the credential name '{name}'; the name is written to the Graph 'displayName', so either omit the property or make it equal ({DiagnosticCodes.NameMappedPropertyConflict}).";
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
