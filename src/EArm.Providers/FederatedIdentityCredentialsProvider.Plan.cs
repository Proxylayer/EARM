using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using EArm.Core.Diagnostics;
using EArm.Core.Secrets;
using EArm.Engine.Providers;
using EArm.Expressions.Values;
using GraphApi = EArm.Graph.GraphApi;

namespace EArm.Providers;

/// <summary>Planning for <see cref="FederatedIdentityCredentialsProvider"/>: the credential diff, the declared and sibling name conflicts and the unsupported changes.</summary>
public sealed partial class FederatedIdentityCredentialsProvider
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
        if (desired.Id.Parent is not { } parentId)
        {
            return Unsupported(desired, existing, $"{desired.Describe()}: the name must be '<application uniqueName>/<credential name>' (or the credential must be nested under its application).");
        }

        string parentName = parentId.NameLeaf;
        if (DeclaredNameConflict(properties, name, desired) is { } conflict)
        {
            return Unsupported(desired, existing, conflict);
        }

        if (FederatedIdentityCredentialsRules.NameProblem(name, Definition.NamePattern) is { } nameProblem)
        {
            return Unsupported(desired, existing, $"{desired.Describe()}: {nameProblem}");
        }

        JsonObjects.TryGetIgnoreCase(properties, "issuer", out var issuer);
        if (FederatedIdentityCredentialsRules.IssuerProblem(issuer) is { } issuerProblem)
        {
            return Unsupported(desired, existing, $"{desired.Describe()}: {issuerProblem}");
        }

        JsonObjects.TryGetIgnoreCase(properties, "subject", out var subject);
        if (FederatedIdentityCredentialsRules.SubjectProblem(subject) is { } subjectProblem)
        {
            return Unsupported(desired, existing, $"{desired.Describe()}: {subjectProblem}");
        }

        if (desired.Api != GraphApi.Beta && JsonObjects.TryGetIgnoreCase(properties, FederatedIdentityCredentialsRules.PreviewOnlyProperty, out var preview) && !JsonObjects.IsAbsent(preview))
        {
            return Unsupported(desired, existing, $"{desired.Describe()}: properties.{FederatedIdentityCredentialsRules.PreviewOnlyProperty} is only available on the beta endpoint; use apiVersion '2026-10-01-preview.beta' or remove the property.");
        }

        var pair = FederatedIdentityCredentialsRules.PairOf(properties);
        string snapshotKey = SnapshotKey(desired.ParentGraphId, parentName);
        var siblings = _siblings.TryGetValue(snapshotKey, out var snapshot) ? snapshot : [];
        var declared = _declared.GetValue(options, _ => new DeclaredCredentials()).Register(snapshotKey, name, pair);

        if (pair is { } mine)
        {
            if (FederatedIdentityCredentialsRules.FindPairHolder(siblings, mine, name) is { } holder)
            {
                return Unsupported(desired, existing, $"{desired.Describe()}: issuer '{mine.Issuer}' and subject '{mine.Subject}' are already used by federated identity credential '{holder.Name}' of application '{parentName}'; Graph requires the pair to be unique per application. Remove or change that credential first (a renamed credential keeping issuer and subject needs the old one deleted before the new one is created).");
            }

            // A credential that already holds its pair live is never the duplicate: the declared sibling that is not
            // live yet is (it sees this one in its sibling list), whatever the order the two were planned in.
            bool holdsPairLive = existing is not null && FederatedIdentityCredentialsRules.PairOf(existing.Raw) is { } livePair && FederatedIdentityCredentialsRules.SamePair(livePair, mine);
            if (!holdsPairLive && declared.DuplicateOf is { } other)
            {
                return Unsupported(desired, existing, $"{desired.Describe()}: issuer '{mine.Issuer}' and subject '{mine.Subject}' are also declared by federated identity credential '{other}' of application '{parentName}' in this template; Graph requires the pair to be unique per application.");
            }
        }

        if (existing is null)
        {
            int others = FederatedIdentityCredentialsRules.CountOthers(siblings, declared.OtherNames, name);
            if (others >= FederatedIdentityCredentialsRules.MaxPerApplication)
            {
                return Unsupported(desired, null, $"{desired.Describe()}: application '{parentName}' already has {others} federated identity credentials (existing plus those declared in this template); Graph allows at most {FederatedIdentityCredentialsRules.MaxPerApplication} per application.");
            }
        }

        return base.Plan(desired, existing, options);
    }

    /// <summary>Mirrors <see cref="ProviderBase.InjectName"/> at plan time: a declared <c>name</c> must equal the credential name.</summary>
    private static string? DeclaredNameConflict(JsonObject properties, string name, ResourceInstance desired)
    {
        if (!JsonObjects.TryGetIgnoreCase(properties, "name", out var declared) || JsonObjects.IsAbsent(declared) || SecretJson.IsSecret(declared) || UnknownJson.IsUnknown(declared))
        {
            return null;
        }

        if (JsonValues.TryGetString(declared, out var text) && string.Equals(text, name, StringComparison.Ordinal))
        {
            return null;
        }

        return $"{desired.Describe()}: properties.name ({JsonValues.Render(declared)}) conflicts with the credential name '{name}'; the name is written to the Graph 'name', so either omit the property or make it equal ({DiagnosticCodes.NameMappedPropertyConflict}).";
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

    /// <summary>
    /// The credentials declared in one planning pass, per parent: the <c>issuer + subject</c> pair of every name seen
    /// so far, so siblings planned in parallel (and not live yet) are checked against each other.
    /// </summary>
    private sealed class DeclaredCredentials
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, Dictionary<string, (string Issuer, string Subject)?>> _byParent = new(StringComparer.Ordinal);

        /// <summary>Registers (or re-registers) a name and returns the other declared names of the parent plus the first one sharing the pair.</summary>
        public (string? DuplicateOf, IReadOnlyList<string> OtherNames) Register(string parentKey, string name, (string Issuer, string Subject)? pair)
        {
            lock (_gate)
            {
                if (!_byParent.TryGetValue(parentKey, out var names))
                {
                    names = new Dictionary<string, (string Issuer, string Subject)?>(StringComparer.OrdinalIgnoreCase);
                    _byParent[parentKey] = names;
                }

                names[name] = pair;
                string? duplicateOf = null;
                var others = new List<string>();
                foreach (var other in names)
                {
                    if (FederatedIdentityCredentialsRules.NamesEqual(other.Key, name))
                    {
                        continue;
                    }

                    others.Add(other.Key);
                    if (duplicateOf is null && pair is { } mine && other.Value is { } theirs && FederatedIdentityCredentialsRules.SamePair(mine, theirs))
                    {
                        duplicateOf = other.Key;
                    }
                }

                return (duplicateOf, others);
            }
        }
    }
}
