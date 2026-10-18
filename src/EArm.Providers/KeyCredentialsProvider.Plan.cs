using System;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Core.Diagnostics;
using EArm.Core.Json;
using EArm.Core.Secrets;
using EArm.Engine;
using EArm.Engine.Providers;
using EArm.Expressions.Values;

namespace EArm.Providers;

/// <summary>Planning for <see cref="KeyCredentialsProvider"/>: the certificate comparison, the ambiguous key and the unsupported declarations.</summary>
public sealed partial class KeyCredentialsProvider
{
    public override ResourceChange Plan(ResourceInstance desired, ExistingResource? existing, PlanOptions options)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(options);

        if (desired.IsIgnored || desired.IsSymbolic || desired.Properties is not { } properties)
        {
            return base.Plan(desired, existing, options);
        }

        string name = desired.Id.NameLeaf;
        if (desired.Id.Parent is not { } parentId)
        {
            return Unsupported(desired, existing, $"{desired.Describe()}: the name must be '<application uniqueName>/<credential displayName>' (or the credential must be nested under its application).");
        }

        if (DeclaredNameConflict(properties, name, desired) is { } conflict)
        {
            return Unsupported(desired, existing, conflict);
        }

        if (desired.Parent?.Properties is { } parentProperties
            && JsonObjects.TryGetIgnoreCase(parentProperties, KeyCredentialsRules.KeyCredentialsProperty, out var parentKeys)
            && !JsonObjects.IsAbsent(parentKeys))
        {
            return Unsupported(desired, existing, $"{desired.Describe()}: application '{parentId.NameLeaf}' also declares properties.keyCredentials, which replaces the whole collection and would remove this credential; manage certificates either on the application or as {TypeName} resources, not both.");
        }

        if (_sameName.TryGetValue(SnapshotKey(desired.ParentGraphId, parentId.NameLeaf, name), out var sameName) && sameName.Count > 1)
        {
            return Unsupported(desired, existing, AmbiguityMessage(desired.Describe(), name, sameName.Select(c => c.KeyId)));
        }

        if (desired.HasUnknownProperties)
        {
            if (existing is null)
            {
                return base.Plan(desired, null, options);
            }

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
                Reasons = ["some property values are only known at deployment time; whether the certificate is replaced is decided when the credential is applied"],
            };
        }

        string? problem = KeyCredentialsRules.DescribeDesired(properties, out var parsed);
        if (problem is not null || parsed is null)
        {
            return Unsupported(desired, existing, $"{desired.Describe()}: {problem ?? "the properties could not be interpreted."}");
        }

        var after = JsonDiff.RenderObject(KeyCredentialsRules.AfterView(name, parsed));
        if (existing is null)
        {
            return new ResourceChange
            {
                Id = desired.Id,
                ResourceType = TypeName,
                Type = ChangeType.Create,
                Mode = ChangeMode.Create,
                Deltas = JsonDiff.DescribeCreate(KeyCredentialsRules.AfterView(name, parsed), DiffOptionsFor(existing: false)),
                After = after,
            };
        }

        var live = KeyCredentialsRules.SummaryOf(existing.Raw)
            ?? throw new ProviderException(EngineDiagnosticCodes.ReadFailed, $"{desired.Describe()}: the live credential carries no keyId.", desired.Id);
        var before = KeyCredentialsRules.DiffView(live);
        var differences = KeyCredentialsRules.Compare(parsed, live);
        if (differences.Count == 0)
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
            };
        }

        var deltas = differences
            .Select(d => new PropertyDelta(JsonPath.Of("properties", d.Property), d.Before is null ? PropertyChangeType.Create : PropertyChangeType.Modify, d.Before, d.After))
            .ToList();
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
            Reasons = ["the certificate credential is replaced (one PATCH of the application's keyCredentials; the other credentials are kept): " + string.Join(", ", differences.Select(d => d.Property)) + " changed"],
        };
    }

    /// <summary>Mirrors <see cref="ProviderBase.InjectName"/> at plan time: a declared <c>displayName</c> must equal the credential name.</summary>
    private static string? DeclaredNameConflict(JsonObject properties, string name, ResourceInstance desired)
    {
        if (!JsonObjects.TryGetIgnoreCase(properties, KeyCredentialsRules.DisplayNameProperty, out var declared) || JsonObjects.IsAbsent(declared) || SecretJson.IsSecret(declared) || UnknownJson.IsUnknown(declared))
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
