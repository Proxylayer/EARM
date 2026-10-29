using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Core;
using EArm.Core.Json;
using EArm.Core.Types;
using EArm.Core.Validation;
using EArm.Engine.Providers;

namespace EArm.Engine.Permissions;

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
    /// <summary>
    /// The application permission the preflight's <c>GET /organization</c> needs on every app-only run. A signed-in user reads it with <c>User.Read</c>
    /// (Learn: organization-get), so delegated runs request this admin-consent scope only for <c>tenant()</c>.
    /// </summary>
    public const string Preflight = "Organization.Read.All";

    internal const string OwnedBy = "Application.ReadWrite.OwnedBy";

    private const string AppRoleAssignmentWrite = "AppRoleAssignment.ReadWrite.All";

    /// <summary>The reason of a languageVersion 2.0 <c>"existing": true</c> resource's read permissions (a lookup: a write grant of the family folds it away).</summary>
    private const string ExistingReason = "lookup: existing resource (\"existing\": true, read only)";

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
            // Learn grouplifecyclepolicy-addgroup / group-list-grouplifecyclepolicies: Directory.ReadWrite.All is the only (and least privileged) write permission.
            RelationshipKind.GroupLifecyclePolicy => ["Directory.ReadWrite.All"],
            // Learn application-post-tokenlifetimepolicies, serviceprincipal-post-claimsmappingpolicies (and the list / delete pages): Application.ReadWrite.All
            // (Application.ReadWrite.OwnedBy for owned objects, app-only) AND Policy.Read.All; Policy.ReadWrite.ApplicationConfiguration is not needed.
            RelationshipKind.TokenLifetimePolicy => ["Application.ReadWrite.All", "Policy.Read.All"],
            RelationshipKind.ClaimsMappingPolicy => ["Application.ReadWrite.All", "Policy.Read.All"],
            // Learn appmanagementpolicy-post-appliesto / -delete-appliesto: Policy.ReadWrite.ApplicationConfiguration AND Application.Read.All.
            RelationshipKind.AppManagementPolicy => ["Policy.ReadWrite.ApplicationConfiguration", "Application.Read.All"],
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
            // Learn (entra groups-lifecycle): Groups Administrator or User Administrator manage the expiration policy.
            RelationshipKind.GroupLifecyclePolicy => ["Groups Administrator", "User Administrator"],
            // Learn is silent on the roles of the token lifetime / claims mapping assignment pages; the application roles that own app configuration.
            RelationshipKind.TokenLifetimePolicy => ["Cloud Application Administrator", "Application Administrator"],
            RelationshipKind.ClaimsMappingPolicy => ["Cloud Application Administrator", "Application Administrator"],
            // Learn appmanagementpolicy-post-appliesto: Cloud Application Administrator or Application Administrator.
            RelationshipKind.AppManagementPolicy => ["Cloud Application Administrator", "Application Administrator"],
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

        var graph = new PermissionAccumulator();
        var roles = new PermissionAccumulator();
        var azure = new PermissionAccumulator();
        var notes = new List<string>();
        var kind = mode == PermissionMode.App ? PermissionKind.GraphApplication : PermissionKind.GraphDelegated;

        int symbolic = 0;
        int unprovided = 0;
        int existingCount = 0;
        bool ownersOnApplications = false;
        bool ownerOfOnUsers = false;
        bool managerDeclared = false;
        bool users = false;
        bool azureRoleManagementPolicies = false;
        var azureGrantTypes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var instance in instances.OrderBy(i => i.PlannedId.Id, StringComparer.Ordinal))
        {
            string resource = instance.PlannedId.Id;
            if (instance.IsSymbolic)
            {
                symbolic++;
            }

            if (instance.Declaration.IsExisting)
            {
                // languageVersion 2.0 "existing": true is only read (by its key, then published for reference()): the read variant of the type's
                // permissions, no edge, no write lookup and no Entra role. A "lookup" reason, so a write grant of the same family folds it away.
                existingCount++;
                var baseline = registry.TryGet(instance.Type, out var reader) ? reader.RequiredPermissions(null) : instance.Definition.Permissions.Application;
                foreach (string permission in baseline.Select(ReadVariant).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    graph.Add(ForMode(permission, mode), resource, ExistingReason);
                }

                continue;
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

            var targetGroup = PermissionRoles.TargetGroupKind(instance, instances);
            if (mode == PermissionMode.App && PermissionRoles.IsCatalogResource(instance.Type) && targetGroup is PermissionRoles.GroupKind.RoleAssignable or PermissionRoles.GroupKind.Unknown)
            {
                // Learn (entitlementmanagement-post-resourcerequests, groups-concept): adding a role-assignable group needs RoleManagement.ReadWrite.Directory.
                graph.Add("RoleManagement.ReadWrite.Directory", resource, targetGroup == PermissionRoles.GroupKind.RoleAssignable
                    ? "provider: the group is role-assignable"
                    : "provider: when the group is role-assignable");
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
                PermissionRoles.AddRoles(instance, resource, roles, targetGroup);
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

            if (ResourceIdentifier.TypeEquals(instance.Type, AzureRoleManagementPolicyRules.ResourceType))
            {
                // Written through Azure Resource Manager, not Graph: an Azure RBAC grant of the signed-in identity at the policy's scope.
                azureRoleManagementPolicies = true;
                azure.Add(
                    $"Azure role management policy write ('Microsoft.Authorization/roleManagementPolicies/write': Owner, User Access Administrator or Role Based Access Control Administrator) at scope '{AzurePolicyScope(instance)}'",
                    resource,
                    "provider: PATCH {scope}/providers/Microsoft.Authorization/roleManagementPolicies/{policy} (whatif reads it: '.../read', for example Reader)");
            }

            if (ResourceIdentifier.TypeEquals(instance.Type, AzureRoleEligibilityScheduleRules.ResourceType))
            {
                // Requested through Azure Resource Manager, not Graph: an Azure RBAC grant of the signed-in identity at the eligibility's scope.
                azureGrantTypes.Add(AzureRoleEligibilityScheduleRules.ResourceType);
                azure.Add(
                    $"Azure role eligibility request ('Microsoft.Authorization/roleEligibilityScheduleRequests/write': Owner or User Access Administrator) at scope '{AzurePolicyScope(instance)}'",
                    resource,
                    "provider: PUT {scope}/providers/Microsoft.Authorization/roleEligibilityScheduleRequests/{guid} (AdminAssign; deployment delete AdminRemove; whatif reads roleEligibilitySchedules: '.../read', for example Reader)");
            }

            if (ResourceIdentifier.TypeEquals(instance.Type, AzureRoleAssignmentRules.ResourceType))
            {
                // Assigned through Azure Resource Manager, not Graph: an Azure RBAC grant of the signed-in identity at the assignment's scope.
                azureGrantTypes.Add(AzureRoleAssignmentRules.ResourceType);
                azure.Add(
                    $"Azure role assignment write ('Microsoft.Authorization/roleAssignments/write', and '.../delete' for deployment delete: Owner, User Access Administrator or Role Based Access Control Administrator) at scope '{AzurePolicyScope(instance)}'",
                    resource,
                    "provider: PUT {scope}/providers/Microsoft.Authorization/roleAssignments/{guid} (deployment delete: DELETE; whatif reads roleAssignments: '.../read', for example Reader)");
            }

            users |= instance.Type.EndsWith("/users", StringComparison.OrdinalIgnoreCase);
            managerDeclared |= instance.Definition.Relationships.Any(r => r.Kind == RelationshipKind.Manager && Declares(instance, r.Property));
        }

        ExpressionLookupPermissions.AddLookups(templateStrings, mode, graph);

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
        permissions.AddRange(graph.Build(kind));
        permissions.AddRange(azure.Build(PermissionKind.AzureRbac));

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

        if (existingCount > 0)
        {
            notes.Add($"{existingCount} resource(s) are declared \"existing\": true: they are only read, so they need the read variant of their type's permissions and no Entra role.");
        }

        if (roleRequirements.Any(r => r.IsTogether))
        {
            notes.Add("An Entra roles group marked 'together' needs one role of each of its parts at the same time (for example a catalog owner role AND a role that may change the group); a part in parentheses lists alternatives.");
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

        notes.Add(mode == PermissionMode.App
            ? "Optional, not listed above: User.ReadBasic.All (or a broader user or directory read such as User.Read.All or Directory.Read.All) lets every online run read the tenant size right after the preflight (GET /users/$count) and pace Graph requests with the tenant's directory budget; without it the run goes on with the smallest tenant's budget (3,500 resource units per 10 s), which only makes a large deployment slower."
            : "Optional, not listed above: User.ReadBasic.All (or a broader user or directory read such as User.Read.All or Directory.Read.All) lets every online run read the tenant size right after the preflight (GET /users/$count) and pace Graph requests with the tenant's directory budget. A run does not request it: the size is read only when the token already carries such a scope; otherwise the run goes on with the smallest tenant's budget (3,500 resource units per 10 s), which only makes a large deployment slower.");
        if (azureRoleManagementPolicies)
        {
            notes.Add("Azure role management policies (Microsoft.Entra/azureRoleManagementPolicies) are read and written through Azure Resource Manager with its own token: the signed-in identity needs the Azure RBAC grant listed at each policy's scope (no Graph permission), and a delegated sign-in the one-time Resource Manager consent (earm auth login -f <template>).");
        }

        if (azureGrantTypes.Count > 0)
        {
            notes.Add($"Azure role grants ({string.Join(", ", azureGrantTypes)}) are read and written through Azure Resource Manager with its own token: the signed-in identity needs the Azure RBAC grant listed at each declared scope (no Graph permission), and a delegated sign-in the one-time Resource Manager consent (earm auth login -f <template>).");
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

    /// <summary>
    /// The read-level counterpart of a write permission (<c>X.ReadWrite.All</c> → <c>X.Read.All</c>), or the permission itself. Two write permissions
    /// have no such counterpart and read with <c>Application.Read.All</c>: <c>Application.ReadWrite.OwnedBy</c>, and
    /// <c>AppRoleAssignment.ReadWrite.All</c> (there is no <c>AppRoleAssignment.Read.All</c>; Learn: serviceprincipal-list-approleassignedto lists
    /// <c>Application.Read.All</c> as the least privileged permission, delegated and application).
    /// </summary>
    public static string ReadVariant(string permission)
    {
        if (permission.Equals(OwnedBy, StringComparison.OrdinalIgnoreCase) || permission.Equals(AppRoleAssignmentWrite, StringComparison.OrdinalIgnoreCase))
        {
            return "Application.Read.All";
        }

        if (permission.Equals("Policy.ReadWrite.ApplicationConfiguration", StringComparison.OrdinalIgnoreCase) || permission.Equals("Policy.ReadWrite.CrossTenantAccess", StringComparison.OrdinalIgnoreCase))
        {
            // Learn (tokenlifetimepolicy-list, claimsmappingpolicy-list, appmanagementpolicy-list, tenantappmanagementpolicy-get, crosstenantaccesspolicy-get and
            // the partner and default pages): the least privileged read permission of these policies is Policy.Read.All.
            return "Policy.Read.All";
        }

        return permission.Replace(".ReadWrite.", ".Read.", StringComparison.Ordinal);
    }

    /// <summary>The literal scope of an Azure Resource Manager declaration (a policy, an eligibility, a role assignment), or a placeholder while it is only known at deployment.</summary>
    private static string AzurePolicyScope(ResourceInstance instance)
    {
        var properties = instance.Properties ?? instance.Declaration.Properties as JsonObject;
        return properties is not null && JsonObjects.GetString(properties, "scope") is { } scope && !ExpressionSyntax.IsExpression(scope)
            ? AzureRoleManagementPolicyRules.NormalizeScope(scope) ?? scope
            : "(the declared scope, known at deployment)";
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
}
