using System;
using System.Collections.Generic;
using System.Linq;
using EArm.Core;
using EArm.Core.Types;
using EArm.Engine.KeyVault;
using EArm.Engine.Providers;

namespace EArm.Engine.Permissions;

/// <summary>What a run does with the tenant, which decides the delegated scopes it requests.</summary>
public enum RunVerb
{
    /// <summary><c>earm validate</c>: offline, no token.</summary>
    Validate,

    /// <summary><c>deployment what-if</c> (and the read-only part of every run): the read variants only.</summary>
    WhatIf,

    /// <summary><c>deployment create</c>: the write permissions of everything the template declares.</summary>
    Create,

    /// <summary><c>deployment delete</c>: write for the declared types, reads for edges and lookups.</summary>
    Delete,
}

/// <summary>One delegated scope of a run, the resources that need it and why.</summary>
public sealed record RunScope(string Name, IReadOnlyList<string> Resources, IReadOnlyList<string> Reasons);

/// <summary>The delegated Microsoft Graph scopes and the Key Vault need of one run.</summary>
/// <param name="Verb">The verb the set was computed for.</param>
/// <param name="Scopes">The scopes with their resources and reasons, sorted by name (case-insensitively).</param>
/// <param name="NeedsKeyVault">True when the run resolves Key Vault parameter references (a separate <c>https://vault.azure.net</c> token).</param>
public sealed record RunScopeSet(RunVerb Verb, IReadOnlyList<RunScope> Scopes, bool NeedsKeyVault)
{
    /// <summary>The short scope names (for example <c>Policy.ReadWrite.ConditionalAccess</c>), sorted; the Graph resource prefix is the token provider's job.</summary>
    public IReadOnlyList<string> GraphScopes { get; } = Scopes.Select(s => s.Name).ToList();
}

/// <summary>
/// The delegated scopes a run requests for a signed-in user (DES-0002): computed per verb, offline (no token, before any engine
/// service exists), from the template and its parameters alone. The source is the delegated <see cref="PermissionManifest"/>
/// (provider rules, edge kinds, lookup functions), then:
/// <list type="bullet">
/// <item><see cref="RunVerb.WhatIf"/> folds every write permission to its read variant (<see cref="PermissionManifest.ReadVariant"/>);</item>
/// <item><see cref="RunVerb.Create"/> keeps the manifest set;</item>
/// <item><see cref="RunVerb.Delete"/> keeps write only for permissions a declared type's provider asks for and folds edge and lookup permissions to reads
/// (deleting an object removes its edges);</item>
/// <item><see cref="RunVerb.Validate"/> is empty;</item>
/// <item>every other verb adds <see cref="SignIn"/>, which also covers the preflight's <c>GET /organization</c> for a signed-in user (Learn: organization-get);
/// <see cref="PermissionManifest.Preflight"/> (<c>Organization.Read.All</c>, admin consent) is requested only when the template calls <c>tenant()</c>.</item>
/// </list>
/// A read scope that a write scope of the same family covers (<c>X.Read.Y</c> next to <c>X.ReadWrite.Y</c>) is folded into the write scope.
/// <para>
/// The schema overlays carry no per-type <c>delegated</c> lists (they were removed): such a list is an unconditional union, while the
/// providers' <c>RequiredPermissions</c> are conditional on what an instance declares (for example
/// <c>RoleManagement.ReadWrite.Directory</c> only for a role-assignable group), and <see cref="PermissionManifest.ForMode"/> already maps the
/// one application-only permission (<c>Application.ReadWrite.OwnedBy</c>) to its delegated scope. App-only sign-ins keep <c>.default</c>.
/// </para>
/// </summary>
public static class RunScopes
{
    /// <summary>
    /// The basic profile scope. Sign-in consents it anyway (<c>earm auth login</c> requests it), the edge pass falls back to
    /// <c>GET /me?$select=id</c> for the signed-in principal, and the preflight's <c>GET /organization</c> reads <c>id</c>, <c>displayName</c> and
    /// <c>verifiedDomains</c> with it; requesting it explicitly keeps the run's set equal to what login consented.
    /// </summary>
    public const string SignIn = "User.Read";

    /// <summary>Validates and expands <paramref name="document"/> offline, then computes the run's scopes.</summary>
    /// <exception cref="InvalidOperationException">The template has validation or expansion errors (run <see cref="OfflineTemplateExpansion"/> to get them).</exception>
    public static RunScopeSet Compute(TemplateDocument document, ProviderRegistry registry, RunVerb verb, ResourceTypeCatalog? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(registry);
        if (verb == RunVerb.Validate)
        {
            return new RunScopeSet(verb, [], false);
        }

        var expansion = OfflineTemplateExpansion.Run(document, catalog ?? ResourceTypeCatalog.Default);
        if (!expansion.Succeeded)
        {
            throw new InvalidOperationException("The template has errors; its delegated scopes cannot be computed: " + expansion.Errors[0].Message);
        }

        return Compute(expansion, registry, verb);
    }

    /// <summary>Computes the run's scopes from a successful offline expansion.</summary>
    public static RunScopeSet Compute(OfflineTemplateExpansion expansion, ProviderRegistry registry, RunVerb verb)
    {
        ArgumentNullException.ThrowIfNull(expansion);
        ArgumentNullException.ThrowIfNull(registry);
        if (verb == RunVerb.Validate)
        {
            return new RunScopeSet(verb, [], false);
        }

        var report = PermissionManifest.Build(expansion.Document.SourcePath ?? string.Empty, PermissionMode.Delegated, expansion, registry);
        var scopes = new ScopeAccumulator();
        foreach (var entry in report.Permissions.Where(p => p.Kind == PermissionKind.GraphDelegated))
        {
            string name = verb switch
            {
                RunVerb.WhatIf => PermissionManifest.ReadVariant(entry.Name),
                RunVerb.Delete when !entry.Reasons.Contains("provider", StringComparer.Ordinal) => PermissionManifest.ReadVariant(entry.Name),
                _ => entry.Name,
            };

            var reasons = name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase)
                ? entry.Reasons
                : [.. entry.Reasons.Select(r => r + " (read of " + entry.Name + ")")];
            scopes.Add(name, entry.Resources, reasons);
        }

        scopes.Add(SignIn, ["(every deployment)"], ["sign-in: the signed-in user's profile (GET /me) and the preflight (GET /organization)"]);
        return new RunScopeSet(verb, scopes.Build(), KeyVaultSecretResolver.HasPendingReferences(expansion.Document));
    }

    private sealed class ScopeAccumulator
    {
        private readonly SortedDictionary<string, (SortedSet<string> Resources, SortedSet<string> Reasons)> _items = new(StringComparer.OrdinalIgnoreCase);

        public void Add(string name, IEnumerable<string> resources, IEnumerable<string> reasons)
        {
            if (!_items.TryGetValue(name, out var entry))
            {
                entry = (new SortedSet<string>(StringComparer.Ordinal), new SortedSet<string>(StringComparer.Ordinal));
                _items[name] = entry;
            }

            entry.Resources.UnionWith(resources);
            entry.Reasons.UnionWith(reasons);
        }

        public IReadOnlyList<RunScope> Build()
        {
            // X.ReadWrite.Y covers X.Read.Y: the read entry's resources and reasons move to the write scope.
            foreach (string read in _items.Keys.Where(k => k.Contains(".Read.", StringComparison.Ordinal)).ToList())
            {
                string write = read.Replace(".Read.", ".ReadWrite.", StringComparison.Ordinal);
                if (_items.TryGetValue(write, out var covering))
                {
                    var covered = _items[read];
                    covering.Resources.UnionWith(covered.Resources);
                    covering.Reasons.UnionWith(covered.Reasons);
                    _items.Remove(read);
                }
            }

            return _items.Select(p => new RunScope(p.Key, [.. p.Value.Resources], [.. p.Value.Reasons])).ToList();
        }
    }
}
