using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EArm.Core;
using EArm.Core.Types;
using EArm.Engine.Providers;

namespace EArm.Cli.Commands;

/// <summary>Which credentials the permissions are computed for (<c>permissions --mode</c>).</summary>
internal enum PermissionMode
{
    /// <summary><c>az login --service-principal</c>: Microsoft Graph application permissions (app roles) on the service principal.</summary>
    App,

    /// <summary><c>az login</c> as a user: delegated scopes plus the Entra roles the user must hold.</summary>
    Delegated,
}

/// <summary>What kind of grant a <see cref="PermissionEntry"/> is.</summary>
internal enum PermissionKind
{
    GraphApplication,
    GraphDelegated,
    AzureRbac,
}

/// <summary>One required permission and the resources (and reasons) that need it.</summary>
internal sealed record PermissionEntry(string Name, PermissionKind Kind, IReadOnlyList<string> Resources, IReadOnlyList<string> Reasons);

/// <summary>A group of Entra roles of which the signed-in user needs at least one, and the resources that need it.</summary>
internal sealed record RoleRequirement(IReadOnlyList<string> AnyOf, IReadOnlyList<string> Resources, IReadOnlyList<string> Reasons);

/// <summary>The computed manifest of <c>earm permissions</c>.</summary>
internal sealed record PermissionReport(
    string Template,
    PermissionMode Mode,
    int ResourceCount,
    IReadOnlyList<PermissionEntry> Permissions,
    IReadOnlyList<RoleRequirement> Roles,
    IReadOnlyList<string> Notes);

/// <summary>
/// The pure part of <c>earm permissions</c> (plan section 3.4): the union of what every expanded resource
/// needs (<c>IResourceProvider.RequiredPermissions</c>, the conditional rules inside the providers), what the
/// relationship properties it declares need (the edge kinds of plan section 1.4: group and administrative unit
/// membership, ownership, manager, app role assignments, directory roles, licenses), what the expression functions
/// that read the tenant need (<c>appRole()</c>, <c>permissionScope()</c>, <c>roleTemplate()</c>,
/// <c>reference(id, apiVersion)</c>) and what the Azure CLI identity needs for Key Vault parameter references.
/// No Graph access: the instances are the offline expansion of the template.
/// </summary>
internal static class PermissionManifest
{
    /// <summary>Read permission that <see cref="Preflight"/> needs on every run (<c>GET /organization?$select=id</c>).</summary>
    public const string Preflight = "Organization.Read.All";

    private const string OwnedBy = "Application.ReadWrite.OwnedBy";

    private static readonly Regex AppRoleFunction = new(@"\bapprole\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex PermissionScopeFunction = new(@"\bpermissionscope\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex RoleTemplateFunction = new(@"\broletemplate\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex TenantFunction = new(@"\btenant\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex ReferenceFunction = new(@"\breference\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex ApiVersionLiteral = new(@"'\d{4}-\d{2}-\d{2}(-preview(\.beta)?)?'", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex TypeLiteral = new(@"'(Microsoft\.Entra/[A-Za-z/]+)'", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>The application permissions of a relationship kind (plan section 1.4 "Graph apply" and 3.4).</summary>
    public static IReadOnlyList<string> EdgeApplicationPermissions(RelationshipKind kind)
    {
        return kind switch
        {
            RelationshipKind.GroupMembership => ["GroupMember.ReadWrite.All"],
            RelationshipKind.Ownership => [],
            RelationshipKind.AdministrativeUnitMembership => ["AdministrativeUnit.ReadWrite.All"],
            RelationshipKind.Manager => ["User.ReadWrite.All"],
            RelationshipKind.AppRoleAssignment => ["AppRoleAssignment.ReadWrite.All", "Application.Read.All"],
            RelationshipKind.DirectoryRoleAssignment => ["RoleManagement.ReadWrite.Directory"],
            RelationshipKind.License => ["Organization.Read.All"],
            _ => [],
        };
    }

    /// <summary>The Entra roles of which the user needs one for a relationship kind (plan section 3.4).</summary>
    public static IReadOnlyList<string> EdgeRoles(RelationshipKind kind)
    {
        return kind switch
        {
            RelationshipKind.GroupMembership => ["Groups Administrator"],
            RelationshipKind.AdministrativeUnitMembership => ["Privileged Role Administrator"],
            RelationshipKind.Manager => ["User Administrator"],
            RelationshipKind.AppRoleAssignment => ["Cloud Application Administrator", "Application Administrator"],
            RelationshipKind.DirectoryRoleAssignment => ["Privileged Role Administrator"],
            RelationshipKind.License => ["License Administrator"],
            _ => [],
        };
    }

    /// <summary>
    /// Builds the report. <paramref name="instances"/> are the offline expansion (copy iterations expanded, instances
    /// whose condition is false already excluded by the caller); <paramref name="templateStrings"/> are every string of
    /// the template that is an expression, for the lookup functions; <paramref name="keyVaultReferences"/> are the
    /// parameters bound to Key Vault secrets.
    /// </summary>
    public static PermissionReport Build(
        string template,
        PermissionMode mode,
        IReadOnlyList<ResourceInstance> instances,
        ProviderRegistry registry,
        IEnumerable<string> templateStrings,
        IEnumerable<(string Parameter, KeyVaultReference Reference)> keyVaultReferences,
        int ignoredCount)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(templateStrings);
        ArgumentNullException.ThrowIfNull(keyVaultReferences);

        var graph = new Accumulator();
        var roles = new Accumulator();
        var azure = new Accumulator();
        var notes = new List<string>();
        var kind = mode == PermissionMode.App ? PermissionKind.GraphApplication : PermissionKind.GraphDelegated;

        int symbolic = 0;
        int unprovided = 0;
        foreach (var instance in instances.OrderBy(i => i.PlannedId.Id, StringComparer.Ordinal))
        {
            string resource = instance.PlannedId.Id;
            if (instance.IsSymbolic)
            {
                symbolic++;
            }

            // The type's baseline: the provider's own rule (conditional on what the instance declares), else the overlay.
            IReadOnlyList<string> required;
            if (registry.TryGet(instance.Type, out var provider))
            {
                required = SafeRequiredPermissions(provider, instance);
            }
            else
            {
                unprovided++;
                required = instance.Definition.Permissions.Application;
            }

            foreach (string permission in required)
            {
                graph.Add(ForMode(permission, mode), resource, "provider");
            }

            // Relationship properties the instance declares (edge pass; action-backed licenses).
            foreach (var spec in instance.Definition.Relationships)
            {
                if (!Declares(instance, spec.Property))
                {
                    continue;
                }

                foreach (string permission in EdgeApplicationPermissions(spec.Kind))
                {
                    graph.Add(ForMode(permission, mode), resource, "edge: " + spec.Property);
                }

                if (spec.Kind == RelationshipKind.Ownership && spec.Side == RelationshipSide.Principal)
                {
                    // ownerOf: the target's kind is only known at deployment (applications, service principals, groups).
                    graph.Add(ForMode("Application.ReadWrite.All", mode), resource, "edge: ownerOf (when a target is an application or service principal)");
                    graph.Add(ForMode("Group.ReadWrite.All", mode), resource, "edge: ownerOf (when a target is a group)");
                }

                if (mode == PermissionMode.Delegated && EdgeRoles(spec.Kind) is { Count: > 0 } edgeRoles)
                {
                    roles.AddGroupSet(edgeRoles, resource, "edge: " + spec.Property);
                }
            }

            if (mode == PermissionMode.Delegated)
            {
                AddRoles(instance, resource, roles);
            }
        }

        AddLookups(templateStrings, mode, graph);

        // Every Graph run reads GET /organization in the preflight.
        if (mode == PermissionMode.App)
        {
            graph.Add(Preflight, "(every deployment)", "preflight: GET /organization");
        }

        foreach (var (parameter, reference) in keyVaultReferences.OrderBy(r => r.Parameter, StringComparer.OrdinalIgnoreCase))
        {
            azure.Add($"Key Vault secret read ('Key Vault Secrets User' or the get-secret access policy) on vault '{reference.VaultName}'", $"parameter '{parameter}'", $"az keyvault secret show --vault-name {reference.VaultName} --name {reference.SecretName}");
        }

        var permissions = new List<PermissionEntry>();
        permissions.AddRange(graph.Build(kind, mode));
        permissions.AddRange(azure.Build(PermissionKind.AzureRbac, mode));

        var roleRequirements = roles.BuildGroups();

        notes.Add(mode == PermissionMode.App
            ? "Application permissions are Microsoft Graph app roles granted (with admin consent) to the service principal the Azure CLI is logged in with (az login --service-principal). Application.ReadWrite.OwnedBy replaces Application.ReadWrite.All for applications the service principal owns."
            : "Delegated scopes must be present on the token the Azure CLI obtains; the signed-in user must additionally hold at least one role of every 'Entra roles' group (the roles are alternatives).");
        if (symbolic > 0)
        {
            notes.Add($"{symbolic} resource(s) have a name that depends on another resource (for example a service principal named after reference(...).appId); their permissions are computed from the declaration and the references it contains.");
        }

        if (unprovided > 0)
        {
            notes.Add($"{unprovided} resource(s) are of a type without a registered provider; their permissions come from the type's schema overlay.");
        }

        if (ignoredCount > 0)
        {
            notes.Add($"{ignoredCount} resource(s) with a condition that evaluates to false offline are not included.");
        }

        notes.Add("Edge permissions are listed for every relationship property the template declares (including an empty array, which removes edges). Key Vault access is an Azure RBAC / access-policy requirement of the Azure CLI identity, not a Graph permission.");
        return new PermissionReport(template, mode, instances.Count, permissions, roleRequirements, notes);
    }

    /// <summary>
    /// <c>Application.ReadWrite.OwnedBy</c> exists only as an application permission; in delegated mode the scope
    /// is <c>Application.ReadWrite.All</c>. Every other name is the same scope and app role.
    /// </summary>
    internal static string ForMode(string permission, PermissionMode mode)
    {
        return mode == PermissionMode.Delegated && permission.Equals(OwnedBy, StringComparison.OrdinalIgnoreCase)
            ? "Application.ReadWrite.All"
            : permission;
    }

    /// <summary>The read-level counterpart of a write permission (<c>X.ReadWrite.All</c> → <c>X.Read.All</c>), or the permission itself.</summary>
    internal static string ReadVariant(string permission)
    {
        if (permission.Equals(OwnedBy, StringComparison.OrdinalIgnoreCase))
        {
            return "Application.Read.All";
        }

        return permission.Replace(".ReadWrite.", ".Read.", StringComparison.Ordinal);
    }

    private static IReadOnlyList<string> SafeRequiredPermissions(IResourceProvider provider, ResourceInstance instance)
    {
        try
        {
            return provider.RequiredPermissions(instance);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NullReferenceException or KeyNotFoundException or InvalidCastException)
        {
            // A symbolic or partially evaluated instance the provider cannot inspect: its declaration-free fallback.
            return provider.RequiredPermissions(null);
        }
    }

    /// <summary>True when the instance's declaration carries <paramref name="property"/> (an empty array and an explicit null still act: they remove edges).</summary>
    private static bool Declares(ResourceInstance instance, string property)
    {
        return instance.Declaration.Properties is JsonObject declared && JsonObjects.FindKey(declared, property) is not null;
    }

    private static void AddRoles(ResourceInstance instance, string resource, Accumulator roles)
    {
        var overlay = instance.Definition.Permissions;
        if (overlay.Roles.Count > 0)
        {
            roles.AddGroupSet(overlay.Roles, resource, "provider");
        }

        if (overlay.Raw?["conditional"] is not JsonObject conditional || instance.Declaration.Properties is not JsonObject declared)
        {
            return;
        }

        foreach (var entry in conditional)
        {
            string? key = JsonObjects.FindKey(declared, entry.Key);
            if (key is null || entry.Value is not JsonObject spec || spec["roles"] is not JsonArray roleArray)
            {
                continue;
            }

            // isAssignableToRole: false (a literal) does not make the group role-assignable.
            if (declared[key] is JsonValue flag && flag.TryGetValue<bool>(out bool value) && !value)
            {
                continue;
            }

            var names = roleArray.Select(n => n?.GetValue<string>()).OfType<string>().ToArray();
            if (names.Length > 0)
            {
                roles.AddGroupSet(names, resource, "condition: " + entry.Key);
            }
        }
    }

    /// <summary>Permissions of the expression functions that read the tenant (they run while the resources are applied).</summary>
    private static void AddLookups(IEnumerable<string> templateStrings, PermissionMode mode, Accumulator graph)
    {
        bool appRole = false;
        bool permissionScope = false;
        bool roleTemplate = false;
        bool tenant = false;
        var existing = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string text in templateStrings)
        {
            appRole |= AppRoleFunction.IsMatch(text);
            permissionScope |= PermissionScopeFunction.IsMatch(text);
            roleTemplate |= RoleTemplateFunction.IsMatch(text);
            tenant |= TenantFunction.IsMatch(text);

            if (ReferenceFunction.IsMatch(text) && ApiVersionLiteral.IsMatch(text))
            {
                foreach (Match match in TypeLiteral.Matches(text))
                {
                    existing.Add(match.Groups[1].Value);
                }
            }
        }

        string lookups = "(expression lookups)";
        if (appRole)
        {
            graph.Add("Application.Read.All", lookups, "lookup: appRole()");
        }

        if (permissionScope)
        {
            graph.Add("Application.Read.All", lookups, "lookup: permissionScope()");
        }

        if (roleTemplate)
        {
            graph.Add("RoleManagement.Read.Directory", lookups, "lookup: roleTemplate() (custom role names)");
        }

        if (tenant)
        {
            graph.Add("Organization.Read.All", lookups, "lookup: tenant()");
        }

        var catalog = ResourceTypeCatalog.Default;
        foreach (string type in existing)
        {
            if (catalog.Find(type) is not { } definition)
            {
                continue;
            }

            foreach (string permission in definition.Permissions.Application.Select(ReadVariant).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                graph.Add(ForMode(permission, mode), $"reference({definition.Type}, apiVersion)", "lookup: existing resource");
            }
        }

    }

    /// <summary>Name → contributing resources and reasons, with the "a ReadWrite grant covers its Read variant" folding applied on build.</summary>
    private sealed class Accumulator
    {
        private readonly SortedDictionary<string, (SortedSet<string> Resources, SortedSet<string> Reasons)> _items = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (string[] AnyOf, SortedSet<string> Resources, SortedSet<string> Reasons)> _groups = new(StringComparer.OrdinalIgnoreCase);

        public void Add(string name, string resource, string reason)
        {
            if (!_items.TryGetValue(name, out var entry))
            {
                entry = (new SortedSet<string>(StringComparer.Ordinal), new SortedSet<string>(StringComparer.Ordinal));
                _items[name] = entry;
            }

            entry.Resources.Add(resource);
            entry.Reasons.Add(reason);
        }


        public void AddGroupSet(IEnumerable<string> anyOf, string resource, string reason)
        {
            string[] sorted = anyOf.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(r => r, StringComparer.OrdinalIgnoreCase).ToArray();
            string key = string.Join('|', sorted);
            if (!_groups.TryGetValue(key, out var entry))
            {
                entry = (sorted, new SortedSet<string>(StringComparer.Ordinal), new SortedSet<string>(StringComparer.Ordinal));
                _groups[key] = entry;
            }

            entry.Resources.Add(resource);
            entry.Reasons.Add(reason);
        }

        public IEnumerable<PermissionEntry> Build(PermissionKind kind, PermissionMode mode)
        {
            _ = mode;

            // Application.ReadWrite.All implies Application.ReadWrite.OwnedBy: when both are needed only the broader grant is listed
            // (the resources that would manage with OwnedBy alone stay visible under it).
            if (_items.TryGetValue(OwnedBy, out var owned) && _items.TryGetValue("Application.ReadWrite.All", out var all))
            {
                all.Resources.UnionWith(owned.Resources);
                all.Reasons.Add("Application.ReadWrite.OwnedBy would suffice for the applications this principal owns (" + owned.Resources.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " resource(s))");
                _items.Remove(OwnedBy);
            }

            foreach (var pair in _items)
            {
                // A pure lookup requirement (reason starts with "lookup" / "preflight") that a write permission of the same
                // family already covers adds nothing: Application.ReadWrite.All implies Application.Read.All.
                if (pair.Key.Contains(".Read.", StringComparison.Ordinal) && pair.Value.Reasons.All(r => r.StartsWith("lookup", StringComparison.Ordinal) || r.StartsWith("preflight", StringComparison.Ordinal))
                    && _items.ContainsKey(pair.Key.Replace(".Read.", ".ReadWrite.", StringComparison.Ordinal)))
                {
                    continue;
                }

                yield return new PermissionEntry(pair.Key, kind, [.. pair.Value.Resources], [.. pair.Value.Reasons]);
            }
        }

        public IReadOnlyList<RoleRequirement> BuildGroups()
        {
            return _groups.Values
                .OrderBy(g => string.Join('|', g.AnyOf), StringComparer.OrdinalIgnoreCase)
                .Select(g => new RoleRequirement(g.AnyOf, [.. g.Resources], [.. g.Reasons]))
                .ToList();
        }
    }
}
