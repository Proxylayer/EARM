using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace EArm.Engine.Providers;

public abstract partial class ProviderBase
{
    // ---- plan ----

    public virtual ResourceChange Plan(ResourceInstance desired, ExistingResource? existing, PlanOptions options)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(options);

        if (desired.IsIgnored)
        {
            return ResourceChange.ConditionFalse(desired.Id, TypeName);
        }

        var properties = desired.Properties
            ?? throw new InvalidOperationException($"{desired.Describe()}: properties must be evaluated before planning.");

        var reasons = new List<string>();
        if (desired.IsSymbolic)
        {
            reasons.Add($"the name is only known once '{SymbolSource(desired)}' exists");
        }

        if (existing is null)
        {
            var createOptions = DiffOptionsFor(existing: false);
            var createDeltas = JsonDiff.DescribeCreate(properties, createOptions);
            if (desired.HasUnknownProperties)
            {
                reasons.Add("some property values are only known at deployment time");
            }

            var missing = MissingRequiredOnCreate(properties);
            if (missing.Count > 0)
            {
                string message = $"{desired.Describe()} cannot be created: required propert{(missing.Count == 1 ? "y" : "ies")} {string.Join(", ", missing)} missing from 'properties'.";
                reasons.Add(message);
                return new ResourceChange
                {
                    Id = desired.Id,
                    ResourceType = TypeName,
                    Type = ChangeType.Create,
                    Mode = ChangeMode.Create,
                    Deltas = createDeltas,
                    After = JsonDiff.RenderObject(properties),
                    IsUnknown = desired.HasUnknownProperties || desired.IsSymbolic,
                    IsSymbolicId = desired.IsSymbolic,
                    Reasons = reasons,
                    UnsupportedReason = message,
                };
            }

            return new ResourceChange
            {
                Id = desired.Id,
                ResourceType = TypeName,
                Type = ChangeType.Create,
                Mode = ChangeMode.Create,
                Deltas = createDeltas,
                After = JsonDiff.RenderObject(properties),
                IsUnknown = desired.HasUnknownProperties || desired.IsSymbolic,
                IsSymbolicId = desired.IsSymbolic,
                Reasons = reasons,
            };
        }

        var diffOptions = DiffOptionsFor(existing: true);
        var deltas = JsonDiff.Diff(properties, existing.Properties, diffOptions);
        var effective = deltas.Where(d => d.HasEffect).ToList();
        var before = RenderBefore(existing.Properties, properties);
        var after = JsonDiff.RenderObject(properties);

        if (effective.Count == 0)
        {
            return new ResourceChange
            {
                Id = desired.Id,
                ResourceType = TypeName,
                Type = ChangeType.NoChange,
                Mode = ChangeMode.None,
                Deltas = deltas,
                Before = before,
                After = after,
                GraphId = existing.GraphId,
                IsUnknown = desired.HasUnknownProperties,
                Reasons = reasons,
            };
        }

        // immutable: ["*"] (ImmutableAll) strips the star from the overlay's list, so every effective delta is an
        // immutable change for such a type; otherwise only the listed paths are.
        var immutable = Definition.ImmutableAll
            ? effective
            : effective.Where(d => JsonDiff.IsImmutable(d, ImmutableProperties, diffOptions.BasePath)).ToList();
        if (immutable.Count > 0)
        {
            string names = string.Join(", ", immutable.Select(d => d.Path.ToString()));
            if (RecreateOnImmutableChange)
            {
                reasons.Add(Definition.ImmutableAll
                    ? $"immutable property {names} changed (every property of {Definition.TypeLeaf} is immutable); the object is deleted and created again"
                    : $"immutable property {names} changed; the object is deleted and created again");
                return new ResourceChange
                {
                    Id = desired.Id,
                    ResourceType = TypeName,
                    Type = ChangeType.Modify,
                    Mode = ChangeMode.Recreate,
                    Deltas = deltas,
                    Before = before,
                    After = after,
                    GraphId = existing.GraphId,
                    IsUnknown = desired.HasUnknownProperties,
                    Reasons = reasons,
                };
            }

            string message = $"Immutable property {names} of {desired.Describe()} differs from the live object and cannot be changed in place; restore the original value, or delete and recreate the resource.";
            reasons.Add(message);
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
                IsUnknown = desired.HasUnknownProperties,
                Reasons = reasons,
                UnsupportedReason = message,
            };
        }

        if (desired.HasUnknownProperties)
        {
            reasons.Add("some property values are only known at deployment time");
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
            IsUnknown = desired.HasUnknownProperties,
            Reasons = reasons,
        };
    }

    /// <summary>The diff options for this type (member keys, nullability, create-only exclusions on update).</summary>
    protected JsonDiffOptions DiffOptionsFor(bool existing)
    {
        return JsonDiffOptions.ForDefinition(Definition, existing);
    }

    /// <summary>
    /// The rendering copy of the live properties for <see cref="ResourceChange.Before"/>: unknowns rendered, and every
    /// live value whose desired counterpart is secret, or whose path is in the type's overlay <c>secure</c> list,
    /// masked as <c>***</c> (a rerun's live value IS the secret, so rendering it verbatim would print the plaintext).
    /// Providers building their own <see cref="ResourceChange"/> use this instead of <see cref="JsonDiff.RenderObject"/>
    /// for the live side.
    /// </summary>
    protected JsonObject? RenderBefore(JsonObject? live, JsonObject? desired)
    {
        var rendered = JsonDiff.RenderLiveObject(live, desired);
        JsonDiff.MaskSecurePaths(rendered, Definition.Secure);
        return rendered;
    }

    /// <summary>
    /// The overlay's <c>requiredOnCreate</c> paths (dotted) that are absent from the evaluated properties and have no
    /// overlay default. A deploy-time-unknown value counts as present.
    /// </summary>
    protected IReadOnlyList<string> MissingRequiredOnCreate(JsonObject properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        var missing = new List<string>();
        foreach (var path in Definition.RequiredOnCreate)
        {
            if (Definition.Defaults is { } defaults && defaults.ContainsKey(path))
            {
                continue;
            }

            if (!JsonObjects.TryGetDotted(properties, path, out var node) || JsonObjects.IsAbsent(node))
            {
                missing.Add(path);
            }
        }

        return missing;
    }

    private static string SymbolSource(ResourceInstance desired)
    {
        foreach (var occurrence in desired.NameReferences)
        {
            if (occurrence.Target is not null)
            {
                return occurrence.Target.Id;
            }
        }

        return desired.PlannedId.NameLeaf;
    }
}
