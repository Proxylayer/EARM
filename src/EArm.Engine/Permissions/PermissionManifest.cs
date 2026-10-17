using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EArm.Core;
using EArm.Core.Json;
using EArm.Core.Types;
using EArm.Engine.Providers;

namespace EArm.Engine.Permissions;

/// <summary>Which credentials the permissions are computed for (<c>permissions --mode</c>).</summary>
public enum PermissionMode
{
    /// <summary>A service principal, workload identity or managed identity (<c>AZURE_*</c> environment): Microsoft Graph application permissions (app roles) on the service principal.</summary>
    App,

    /// <summary>An interactive sign-in (<c>earm auth login</c>): delegated scopes plus the Entra roles the user must hold.</summary>
    Delegated,
}

/// <summary>What kind of grant a <see cref="PermissionEntry"/> is.</summary>
public enum PermissionKind
{
    GraphApplication,
    GraphDelegated,
    AzureRbac,
}

/// <summary>One required permission and the resources (and reasons) that need it.</summary>
public sealed record PermissionEntry(string Name, PermissionKind Kind, IReadOnlyList<string> Resources, IReadOnlyList<string> Reasons);

/// <summary>A group of Entra roles of which the signed-in user needs at least one, and the resources that need it.</summary>
public sealed record RoleRequirement(IReadOnlyList<string> AnyOf, IReadOnlyList<string> Resources, IReadOnlyList<string> Reasons);

/// <summary>The computed manifest of <c>earm permissions</c>.</summary>
public sealed record PermissionReport(
    string Template,
    PermissionMode Mode,
    int ResourceCount,
    IReadOnlyList<PermissionEntry> Permissions,
    IReadOnlyList<RoleRequirement> Roles,
    IReadOnlyList<string> Notes);

/// <summary>
/// The pure part of <c>earm permissions</c>: the union of what every expanded resource
/// needs (<c>IResourceProvider.RequiredPermissions</c>, the conditional rules inside the providers), what the
/// relationship properties it declares need (the edge kinds: group and administrative unit
/// membership, ownership, manager, app role assignments, directory roles, licenses), what the expression functions
/// that read the tenant need (<c>appRole()</c>, <c>permissionScope()</c>, <c>roleTemplate()</c>, <c>roleDefinitions()</c>,
/// <c>reference(id, apiVersion)</c>) and what the signed-in identity needs for Key Vault parameter references.
/// No Graph access: the instances are the offline expansion of the template.
/// </summary>
public static class PermissionManifest
{
    /// <summary>Read permission that <see cref="Preflight"/> needs on every run (<c>GET /organization?$select=id</c>).</summary>
    public const string Preflight = "Organization.Read.All";

    private const string OwnedBy = "Application.ReadWrite.OwnedBy";

    private static readonly Regex AppRoleFunction = new(@"\bapprole\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex PermissionScopeFunction = new(@"\bpermissionscope\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex RoleTemplateFunction = new(@"\broletemplate\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex RoleDefinitionsFunction = new(@"\broledefinitions\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex TenantFunction = new(@"\btenant\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex ReferenceFunction = new(@"\breference\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex ApiVersionLiteral = new(@"'\d{4}-\d{2}-\d{2}(-preview(\.beta)?)?'", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex TypeLiteral = new(@"'(Microsoft\.Entra/[A-Za-z/]+)'", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>
    /// The application permissions of a relationship kind (Review 1D, checked against Learn:
    /// group-post-members, group-post-owners, application-post-owners, directoryobject-get, user-list-memberof, approleassignedto,
    /// user-assignlicense, group-assignlicense). The edge pass resolves principal kinds with <c>GET /directoryObjects/{id}</c>, which needs
    /// <c>Directory.Read.All</c> even when the write permission is <c>.All</c> (Learn: application-post-owners lists
    /// <c>Application.ReadWrite.OwnedBy</c> AND <c>Directory.Read.All</c>).
    /// </summary>
    public static IReadOnlyList<string> EdgeApplicationPermissions(RelationshipKind kind)
    {
        return kind switch
        {
            RelationshipKind.GroupMembership => ["GroupMember.ReadWrite.All", "Directory.Read.All"],
            RelationshipKind.Ownership => ["Directory.Read.All"],
            RelationshipKind.AdministrativeUnitMembership => ["AdministrativeUnit.ReadWrite.All"],
            RelationshipKind.Manager => ["User.ReadWrite.All"],
            RelationshipKind.AppRoleAssignment => ["AppRoleAssignment.ReadWrite.All", "Application.Read.All", "Directory.Read.All"],
            RelationshipKind.DirectoryRoleAssignment => ["RoleManagement.ReadWrite.Directory", "Directory.Read.All"],
            RelationshipKind.License => ["LicenseAssignment.ReadWrite.All", "LicenseAssignment.Read.All"],
            _ => [],
        };
    }

    /// <summary>The Entra roles of which the user needs one for a relationship kind.</summary>
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

    /// <summary>Builds the report from a successful <see cref="OfflineTemplateExpansion"/>.</summary>
    public static PermissionReport Build(string template, PermissionMode mode, OfflineTemplateExpansion expansion, ProviderRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(expansion);
        if (!expansion.Succeeded)
        {
            throw new ArgumentException("The template did not expand offline; its permissions are not meaningful.", nameof(expansion));
        }

        return Build(template, mode, expansion.Instances, registry, expansion.ExpressionStrings, expansion.KeyVaultReferences, expansion.IgnoredCount);
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
        bool ownersOnApplications = false;
        bool ownerOfOnUsers = false;
        bool managerDeclared = false;
        bool users = false;
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

                if (spec.Kind == RelationshipKind.GroupMembership && spec.Side == RelationshipSide.Target)
                {
                    // Learn (group-post-members): a service principal member needs GroupMember.ReadWrite.All AND Application.ReadWrite.All;
                    // the member kinds are only known at deployment.
                    graph.Add(ForMode("Application.ReadWrite.All", mode), resource, "edge: " + spec.Property + " (when a member is a service principal)");
                }

                if (spec.Kind == RelationshipKind.Ownership && spec.Side == RelationshipSide.Target && IsApplicationOrServicePrincipal(instance.Type))
                {
                    ownersOnApplications = true;
                }

                if (spec.Kind == RelationshipKind.Ownership && spec.Side == RelationshipSide.Principal)
                {
                    if (instance.Type.EndsWith("/users", StringComparison.OrdinalIgnoreCase))
                    {
                        ownerOfOnUsers = true;
                    }

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

            if (instance.Type.EndsWith("/roleAssignments", StringComparison.OrdinalIgnoreCase) || instance.Type.EndsWith("/roleEligibilityScheduleRequests", StringComparison.OrdinalIgnoreCase)
                || instance.Type.EndsWith("/roleAssignmentScheduleRequests", StringComparison.OrdinalIgnoreCase))
            {
                // A role assignment's (or eligibility's, or PIM active assignment's) principal may be a role-assignable group: GET /groups/{id}?$select=isAssignableToRole (Learn: group-get).
                graph.Add(ForMode("Group.Read.All", mode), resource, "lookup: role-assignable group check");
            }

            if (instance.Type.EndsWith("/groupEligibilityScheduleRequests", StringComparison.OrdinalIgnoreCase) || instance.Type.EndsWith("/groupAssignmentScheduleRequests", StringComparison.OrdinalIgnoreCase))
            {
                // PIM for Groups refuses dynamic and on-premises synced groups: GET /groups/{id}?$select=groupTypes,onPremisesSyncEnabled (Learn: group-get).
                graph.Add(ForMode("Group.Read.All", mode), resource, "lookup: target group kind check (dynamic, on-premises synced)");
            }

            users |= instance.Type.EndsWith("/users", StringComparison.OrdinalIgnoreCase);
            managerDeclared |= instance.Definition.Relationships.Any(r => r.Kind == RelationshipKind.Manager && Declares(instance, r.Property));
        }

        AddLookups(templateStrings, mode, graph);

        // Every Graph run reads GET /organization in the preflight.
        if (mode == PermissionMode.App)
        {
            graph.Add(Preflight, "(every deployment)", "preflight: GET /organization");
        }

        foreach (var (parameter, reference) in keyVaultReferences.OrderBy(r => r.Parameter, StringComparer.OrdinalIgnoreCase))
        {
            azure.Add($"Key Vault secret read ('Key Vault Secrets User' or the get-secret access policy) on vault '{reference.VaultName}'", $"parameter '{parameter}'", $"GET https://{reference.VaultName}.vault.azure.net/secrets/{reference.SecretName}");
        }

        var permissions = new List<PermissionEntry>();
        permissions.AddRange(graph.Build(kind, mode));
        permissions.AddRange(azure.Build(PermissionKind.AzureRbac, mode));

        var roleRequirements = roles.BuildGroups();

        notes.Add(mode == PermissionMode.App
            ? "Application permissions are Microsoft Graph app roles granted (with admin consent) to the service principal EARM signs in with (AZURE_CLIENT_ID, or the managed identity). Application.ReadWrite.OwnedBy replaces Application.ReadWrite.All for applications the service principal owns."
            : "Delegated scopes must be present on the token that earm auth login obtains (the app registration of --client-id needs them as delegated permissions); the signed-in user must additionally hold at least one role of every 'Entra roles' group (the roles are alternatives).");
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

        if (ownersOnApplications)
        {
            notes.Add("Learn (application-post-owners, serviceprincipal-post-owners): adding an owner needs Application.ReadWrite.OwnedBy AND Directory.Read.All, and \"Application.ReadWrite.OwnedBy isn't sufficient to add another owner\": when the declared owners include anyone besides the principal that owns the application, grant Application.ReadWrite.All as well.");
        }

        if (mode == PermissionMode.App && ownerOfOnUsers)
        {
            notes.Add("`ownerOf` on a user reads GET /users/{id}/ownedObjects, which Microsoft Graph does not support with application permissions (Learn: user-list-ownedobjects, Application: Not supported); the owner set of a user principal can only be reconciled with a signed-in (delegated) token. Declare `owners` on the application, service principal or group instead.");
        }

        if (mode == PermissionMode.App && managerDeclared)
        {
            notes.Add("`manager` is written with PUT /users/{id}/manager/$ref (User.ReadWrite.All) but read with GET /users/{id}/manager, for which Learn lists no application permission (user-list-manager, Application: Not supported); with an application token that read can be refused (403), so reconcile `manager` with a signed-in (delegated) token.");
        }

        if (users)
        {
            notes.Add("Restoring soft-deleted objects (--on-soft-deleted-conflict restore): users need User.DeleteRestore.All, groups Group.ReadWrite.All, administrative units AdministrativeUnit.ReadWrite.All, applications and service principals Application.ReadWrite.OwnedBy (Learn: directory-deleteditems-restore). Probing /directory/deletedItems before a create needs the type's read permission (for applications Application.Read.All, which Application.ReadWrite.OwnedBy does not carry: the probe is then skipped with a SoftDeleteProbeSkipped warning).");
        }

        notes.Add("Edge permissions are listed for every relationship property the template declares (including an empty array, which removes edges). Key Vault access is an Azure RBAC / access-policy requirement of the signed-in identity, not a Graph permission.");
        return new PermissionReport(template, mode, instances.Count, permissions, roleRequirements, notes);
    }

    /// <summary>
    /// <c>Application.ReadWrite.OwnedBy</c> exists only as an application permission; in delegated mode the scope
    /// is <c>Application.ReadWrite.All</c>. Every other name is the same scope and app role.
    /// </summary>
    public static string ForMode(string permission, PermissionMode mode)
    {
        return mode == PermissionMode.Delegated && permission.Equals(OwnedBy, StringComparison.OrdinalIgnoreCase)
            ? "Application.ReadWrite.All"
            : permission;
    }

    /// <summary>The read-level counterpart of a write permission (<c>X.ReadWrite.All</c> → <c>X.Read.All</c>), or the permission itself.</summary>
    public static string ReadVariant(string permission)
    {
        if (permission.Equals(OwnedBy, StringComparison.OrdinalIgnoreCase))
        {
            return "Application.Read.All";
        }

        return permission.Replace(".ReadWrite.", ".Read.", StringComparison.Ordinal);
    }

    private static bool IsApplicationOrServicePrincipal(string type)
    {
        return type.EndsWith("/applications", StringComparison.OrdinalIgnoreCase) || type.EndsWith("/servicePrincipals", StringComparison.OrdinalIgnoreCase);
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
        return instance.Declaration.Properties is JsonObject declared && PropertyCopyBlock.Declares(declared, property);
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
        bool roleDefinitions = false;
        bool tenant = false;
        var existing = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string text in templateStrings)
        {
            appRole |= AppRoleFunction.IsMatch(text);
            permissionScope |= PermissionScopeFunction.IsMatch(text);
            roleTemplate |= RoleTemplateFunction.IsMatch(text);
            roleDefinitions |= RoleDefinitionsFunction.IsMatch(text);
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

        if (roleDefinitions)
        {
            graph.Add("RoleManagement.Read.Directory", lookups, "lookup: roleDefinitions() (custom role names)");
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
