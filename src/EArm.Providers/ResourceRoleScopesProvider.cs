using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core;
using EArm.Core.Types;
using EArm.Engine;
using EArm.Engine.Execution;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using EArm.Graph;
using GraphApi = EArm.Graph.GraphApi;

namespace EArm.Providers;

/// <summary>
/// <c>Microsoft.Entra/entitlementManagement/resourceRoleScopes</c>: a role an access package grants of a catalog resource
/// (<see cref="AccessPackageResourceOrigin"/>): a group role (Member, Owner, Eligible Member, Eligible Owner), an app role of an application, or a site group
/// of a SharePoint Online site. Found by the tuple (<c>accessPackageId</c>, <c>resourceOriginId</c>, <c>role</c>); the GUID name is a label; nothing is
/// updated in place (Learn: resources/accesspackageresourcerolescope offers list, create and delete only).
/// <list type="bullet">
/// <item><b>Read</b>: <c>GET …/accessPackages/{id}?$expand=resourceRoleScopes($expand=role,scope)</c>, matched on <c>scope.originId</c> and the role's
/// display name or <c>originId</c>.</item>
/// <item><b>Create</b>: the package's catalog (<c>$expand=catalog</c>) → the resource's catalog resource → the catalog's roles of that resource, picked by
/// display name or, failing that, by the role's <c>originId</c> (an app role id, a site group number; the eligible group roles' <c>originId</c> format is
/// undocumented, so it is never built) → <c>POST …/accessPackages/{id}/resourceRoleScopes</c> (Learn: accesspackage-post-resourcerolescopes).</item>
/// <item><b>Delete</b>: <c>DELETE …/accessPackages/{id}/resourceRoleScopes/{id}</c> (Graph does not refuse it while the package has assignments).</item>
/// </list>
/// An eligible role of a group PIM for Groups has not onboarded is <see cref="NotOnboarded"/>: deferred to the next run when a schedule request of the
/// same deployment onboards the group (as <see cref="RoleManagementPoliciesProvider"/> defers a group policy), an error otherwise.
/// </summary>
[EntraProvider(ResourceType)]
public sealed class ResourceRoleScopesProvider : ProviderBase, IApplyOrderSource, IDeleteWarningSource
{
    public const string ResourceType = "Microsoft.Entra/entitlementManagement/resourceRoleScopes";

    /// <summary><see cref="ProviderException.Code"/> when the group is not a resource of the package's catalog or the role is not offered.</summary>
    public const string RoleNotAvailable = EngineDiagnosticCodes.AccessPackageRoleNotAvailable;

    /// <summary>The code when an eligible role is not offered because PIM for Groups has not onboarded the group.</summary>
    public const string NotOnboarded = EngineDiagnosticCodes.PimGroupNotOnboarded;

    /// <summary>The code when Graph refuses an eligible role because the tenant has no Entra ID Governance licence (400 <c>NoGovernanceSku</c>).</summary>
    public const string GovernanceLicenseRequired = EngineDiagnosticCodes.GovernanceLicenseRequired;

    /// <summary>Graph's error code for an eligible access package role in a tenant without an Entra ID Governance licence.</summary>
    internal const string NoGovernanceSku = "NoGovernanceSku";

    private const string PackageProperty = "accessPackageId";

    private const string OriginIdProperty = "resourceOriginId";

    private const string RoleProperty = "role";

    private const string OriginSystemProperty = "originSystem";

    private const string ScopesExpand = "resourceRoleScopes($expand=role,scope)";

    private const string PackageNameProperty = "accessPackageDisplayName";

    /// <summary>The role scopes whose group a schedule request of the same deployment onboards (found by <see cref="AppliesBefore"/>).</summary>
    private readonly ConditionalWeakTable<ResourceInstance, object> _onboardedThisRun = new();

    /// <summary>The role scopes <see cref="PrepareAsync"/> found to name an eligible role the catalog does not offer yet.</summary>
    private readonly ConditionalWeakTable<ResourceInstance, string> _notOnboarded = new();

    public ResourceRoleScopesProvider(ResourceTypeDefinition definition, ResourceTypeCatalog? catalog = null)
        : this(definition, catalog, null)
    {
    }

    /// <param name="definition">The catalog definition of the type.</param>
    /// <param name="catalog">The catalog.</param>
    /// <param name="settle">How long an eligible grant waits for the catalog to offer the role after a same-run request onboarded the group; <see cref="ReplicationWait.Default"/> when null.</param>
    public ResourceRoleScopesProvider(ResourceTypeDefinition definition, ResourceTypeCatalog? catalog, ReplicationWait? settle)
        : base(definition, catalog)
    {
        if (!ResourceIdentifier.TypeEquals(definition.Type, ResourceType))
        {
            throw new ArgumentException($"{nameof(ResourceRoleScopesProvider)} implements '{ResourceType}', not '{definition.Type}'.", nameof(definition));
        }

        Settle = settle ?? ReplicationWait.Default;
    }

    /// <summary>How long an eligible grant waits (every <see cref="ReplicationWait.Interval"/>, up to <see cref="ReplicationWait.Window"/>) for the catalog to offer the role once a same-run request onboarded the group.</summary>
    public ReplicationWait Settle { get; init; }

    /// <summary>The package's role scope list can miss a new role scope for a moment.</summary>
    public override bool KeyReadLagsCreate => true;

    /// <summary>
    /// A role scope's Graph id is the composite <c>{roleId}_{scopeId}</c> of the catalog's role and scope, so it repeats in every package of the catalog
    /// that grants the same role: the claim is scoped by the access package (<c>{type}/{accessPackageId}/{graphId}</c>).
    /// </summary>
    public override string ClaimIdentity(ResourceKey key, string graphId)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(graphId);
        return key.TryGet(PackageProperty, out var packageId) && packageId.Length > 0
            ? TypeName + "/" + packageId + "/" + graphId
            : base.ClaimIdentity(key, graphId);
    }

    /// <summary>The PIM for Groups eligible roles (<c>Eligible Member</c>, <c>Eligible Owner</c>).</summary>
    internal static bool IsEligibleRole(string? role)
    {
        return role is not null && role.TrimStart().StartsWith("Eligible ", StringComparison.OrdinalIgnoreCase);
    }

    // ---- order ----

    /// <summary>The assignment policies of the same package are applied after the role scope; a same-run PIM request for the group is remembered for <see cref="Plan"/>.</summary>
    public IEnumerable<ResourceInstance> AppliesBefore(ResourceInstance instance, IReadOnlyList<ResourceInstance> declared)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(declared);
        if (AccessPackageApplyOrder.GroupOnboardedThisRun(instance, declared))
        {
            _onboardedThisRun.AddOrUpdate(instance, true);
        }

        return AccessPackageApplyOrder.PoliciesOfPackage(instance, declared);
    }

    // ---- prepare ----

    /// <summary>For an eligible role whose package and group are known and in the catalog: checks that the catalog offers the role (PIM for Groups onboarded the group).</summary>
    public override async Task PrepareAsync(ResourceInstance desired, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(graph);
        _notOnboarded.Remove(desired);
        if (desired.Properties is not { } properties || !IsEligibleRole(JsonObjects.GetString(properties, RoleProperty))
            || !AccessPackageResourceOrigin.IsGroup(OriginSystem(properties))
            || JsonObjects.GetString(properties, PackageProperty) is not { Length: > 0 } packageId
            || JsonObjects.GetString(properties, OriginIdProperty) is not { Length: > 0 } groupId)
        {
            return;
        }

        var package = await EntitlementManagementGraph.GetPackageAsync(graph, packageId, desired.Api, desired.RetryHint, "catalog", cancellationToken).ConfigureAwait(false);
        if (package is null || EntitlementManagementGraph.CatalogIdOf(package) is not { } catalogId)
        {
            return;
        }

        var resource = await EntitlementManagementGraph.FindCatalogResourceAsync(graph, catalogId, groupId, desired.Api, desired.RetryHint, cancellationToken).ConfigureAwait(false);
        if (resource is null || JsonObjects.GetId(resource) is not { } resourceId || !AccessPackageResourceOrigin.IsGroup(OriginSystem(properties, resource)))
        {
            return; // The group joins the catalog in this run (or not at all): CreateAsync reports it. An app or site has no eligible roles.
        }

        string role = JsonObjects.GetString(properties, RoleProperty)!.Trim();
        var roles = await EntitlementManagementGraph.ListResourceRolesAsync(graph, catalogId, resourceId, AccessPackageResourceOrigin.Group, desired.Api, desired.RetryHint, cancellationToken).ConfigureAwait(false);
        if (FindRole(roles, role) is not null)
        {
            return;
        }

        // Applying, after the same-run request that onboards the group (the role scope is ordered after it, and a dependency created in this run is what
        // gives it the replication hint; a what-if never has one): the catalog offers the role once PIM has processed the request.
        if (_onboardedThisRun.TryGetValue(desired, out _) && desired.RetryHint == RetryHint.ExpectReplicationLag
            && await WaitForRoleAsync(graph, catalogId, resourceId, role, AccessPackageResourceOrigin.Group, desired.Api, cancellationToken).ConfigureAwait(false) is not null)
        {
            return;
        }

        _notOnboarded.AddOrUpdate(desired, $"catalog {catalogId} offers no '{role}' role for group {groupId}: PIM for Groups has not onboarded the group");
    }

    /// <summary>Reads the catalog's roles of the resource every <see cref="ReplicationWait.Interval"/> until <paramref name="role"/> is offered (that role) or <see cref="ReplicationWait.Window"/> has passed (<c>null</c>).</summary>
    private async Task<JsonObject?> WaitForRoleAsync(IGraphClient graph, string catalogId, string resourceId, string role, string originSystem, GraphApi api, CancellationToken cancellationToken)
    {
        var waited = TimeSpan.Zero;
        while (waited + Settle.Interval <= Settle.Window)
        {
            await Settle.Delay.DelayAsync(Settle.Interval, cancellationToken).ConfigureAwait(false);
            waited += Settle.Interval;
            var roles = await EntitlementManagementGraph.ListResourceRolesAsync(graph, catalogId, resourceId, originSystem, api, RetryHint.ExpectReplicationLag, cancellationToken).ConfigureAwait(false);
            if (FindRole(roles, role) is { } offered)
            {
                return offered;
            }
        }

        return null;
    }

    private static JsonObject? FindRole(IReadOnlyList<JsonObject> roles, string role)
    {
        return AccessPackageResourceOrigin.FindRole(roles, role);
    }

    // ---- plan ----

    public override ResourceChange Plan(ResourceInstance desired, ExistingResource? existing, PlanOptions options)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(options);
        if (existing is not null || desired.IsIgnored || !_notOnboarded.TryGetValue(desired, out var notOnboarded))
        {
            return base.Plan(desired, existing, options);
        }

        if (_onboardedThisRun.TryGetValue(desired, out _) && options.PlanMode == PlanMode.WhatIf)
        {
            // A what-if: the request has not run yet. The create applies the role scope after it and waits for the catalog to offer the role.
            var planned = base.Plan(desired, existing, options);
            return planned with
            {
                Reasons = [.. planned.Reasons, $"{notOnboarded} yet; a schedule request of this deployment onboards it, and the role scope is created after that request (EARM waits up to {Settle.Window.TotalSeconds:0} s for the catalog to offer the role)"],
            };
        }

        if (_onboardedThisRun.TryGetValue(desired, out _))
        {
            // Applying, and the wait timed out: deferred to the next run (the fallback).
            string warning = ProviderWarnings.Format(
                NotOnboarded,
                $"{notOnboarded} yet, {Settle.Window.TotalSeconds:0} s after the schedule request of this deployment that onboards it. The role scope is created by the next run (deploy again once this one succeeded).");
            return new ResourceChange
            {
                Id = desired.Id,
                ResourceType = TypeName,
                Type = ChangeType.Ignore,
                Mode = ChangeMode.None,
                After = JsonDiff.RenderObject(desired.Properties),
                Reasons = [warning],
            };
        }

        string message = $"{desired.Describe()}: {notOnboarded}, and no schedule request of this deployment onboards it. A catalog offers Eligible Member and Eligible Owner only for a group managed by PIM for Groups, which onboards a group on its first eligibility or assignment request (Learn: entitlement-management-access-package-eligible, privilegedidentitymanagement-for-groups-api-overview); declare a privilegedAccess/groupEligibilityScheduleRequests (or groupAssignmentScheduleRequests) for the group in the same template, or onboard it in the portal, then deploy again.";
        return new ResourceChange
        {
            Id = desired.Id,
            ResourceType = TypeName,
            Type = ChangeType.Create,
            Mode = ChangeMode.Create,
            After = JsonDiff.RenderObject(desired.Properties),
            Reasons = [message],
            UnsupportedReason = message,
            UnsupportedCode = NotOnboarded,
        };
    }

    // ---- read ----

    public override async Task<ExistingResource?> ReadAsync(ResourceKey key, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(graph);
        if (!key.TryGet(PackageProperty, out var packageId) || !key.TryGet(OriginIdProperty, out var groupId) || !key.TryGet(RoleProperty, out var role))
        {
            return null;
        }

        var package = await EntitlementManagementGraph.GetPackageAsync(graph, packageId, key.Api, RetryHint.Default, ScopesExpand, cancellationToken).ConfigureAwait(false);
        if (package?["resourceRoleScopes"] is not JsonArray scopes)
        {
            return null;
        }

        foreach (var item in scopes)
        {
            if (item is JsonObject roleScope && Matches(roleScope, groupId, role))
            {
                // A role named by its originId (an app role id, a site group number) keeps the declared spelling, and so does a site URL: no drift.
                string? liveName = JsonObjects.GetString((JsonObject)roleScope["role"]!, "displayName");
                bool byName = string.Equals(liveName?.Trim(), role.Trim(), StringComparison.OrdinalIgnoreCase);
                var view = View(roleScope, packageId, groupId, byName ? liveName! : role);
                view[OriginIdProperty] = groupId;
                view[PackageNameProperty] = JsonObjects.GetString(package, "displayName");
                return KeyView(view);
            }
        }

        return null;
    }

    private static bool Matches(JsonObject roleScope, string originId, string role)
    {
        return roleScope["role"] is JsonObject liveRole && roleScope["scope"] is JsonObject scope
            && AccessPackageResourceOrigin.SameId(JsonObjects.GetString(scope, "originId"), originId)
            && AccessPackageResourceOrigin.IsRole(liveRole, role);
    }

    // ---- create ----

    public override async Task<AppliedResource> CreateAsync(ResourceInstance desired, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(graph);
        var properties = desired.Properties
            ?? throw new InvalidOperationException($"{desired.Describe()}: properties must be evaluated before creating.");
        string packageId = Required(desired, properties, PackageProperty);
        string groupId = Required(desired, properties, OriginIdProperty);
        string role = Required(desired, properties, RoleProperty).Trim();

        var package = await EntitlementManagementGraph.GetPackageAsync(graph, packageId, desired.Api, desired.RetryHint, "catalog", cancellationToken).ConfigureAwait(false)
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: access package {packageId} does not exist.", desired.Id);
        string catalogId = EntitlementManagementGraph.CatalogIdOf(package)
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: Graph returned access package {packageId} without its catalog.", desired.Id);
        var resource = await EntitlementManagementGraph.FindCatalogResourceAsync(graph, catalogId, groupId, desired.Api, desired.RetryHint, cancellationToken).ConfigureAwait(false);
        if (resource is null || JsonObjects.GetId(resource) is not { } resourceId)
        {
            string missing = AccessPackageResourceOrigin.Noun(OriginSystem(properties));
            throw new ProviderException(RoleNotAvailable, $"{desired.Describe()}: {missing} {groupId} is not a resource of catalog {catalogId}, the catalog of access package {packageId}; declare a Microsoft.Entra/entitlementManagement/catalogResources for it (or add it in the portal).", desired.Id);
        }

        string originSystem = OriginSystem(properties, resource);
        string noun = AccessPackageResourceOrigin.Noun(originSystem);

        // Graph keeps the origin id as it was added (a site URL's spelling): the request names it that way.
        string liveOriginId = JsonObjects.GetString(resource, "originId") ?? groupId;

        var roles = await EntitlementManagementGraph.ListResourceRolesAsync(graph, catalogId, resourceId, originSystem, desired.Api, desired.RetryHint, cancellationToken).ConfigureAwait(false);
        var match = FindRole(roles, role);
        bool eligible = IsEligibleRole(role) && AccessPackageResourceOrigin.IsGroup(originSystem);
        if (match is null && eligible && _onboardedThisRun.TryGetValue(desired, out _))
        {
            match = await WaitForRoleAsync(graph, catalogId, resourceId, role, originSystem, desired.Api, cancellationToken).ConfigureAwait(false);
        }

        if (match is null)
        {
            string offered = string.Join(", ", roles.Select(r => JsonObjects.GetString(r, "displayName") ?? "?"));
            if (eligible)
            {
                string when = _onboardedThisRun.TryGetValue(desired, out _)
                    ? $"A schedule request of this deployment onboards the group, but the role was still not offered {Settle.Window.TotalSeconds:0} s later; deploy again."
                    : "Onboard the group first (a privilegedAccess/groupEligibilityScheduleRequests or groupAssignmentScheduleRequests for it, or the portal), then deploy again.";
                throw new ProviderException(NotOnboarded, $"{desired.Describe()}: catalog {catalogId} offers no '{role}' role for group {groupId} (offered: {offered}): PIM for Groups has not onboarded the group. {when}", desired.Id);
            }

            throw new ProviderException(RoleNotAvailable, $"{desired.Describe()}: catalog {catalogId} offers no '{role}' role for {noun} {groupId} (offered: {offered}){RoleHint(originSystem)}.", desired.Id);
        }

        var body = new JsonObject
        {
            ["role"] = new JsonObject
            {
                ["displayName"] = JsonObjects.GetString(match, "displayName"),
                ["originId"] = JsonObjects.GetString(match, "originId"),
                ["originSystem"] = JsonObjects.GetString(match, "originSystem") ?? originSystem,
                ["resource"] = new JsonObject { ["id"] = resourceId, ["originId"] = liveOriginId, ["originSystem"] = originSystem },
            },
            ["scope"] = new JsonObject { ["originId"] = liveOriginId, ["originSystem"] = originSystem, ["isRootScope"] = true },
        };

        // Learn's application example names the catalog role by id; a group role's id may be the zero GUID, which Graph wants left out.
        if (!AccessPackageResourceOrigin.IsGroup(originSystem) && JsonObjects.GetId(match) is { } roleId && Guid.TryParse(roleId, out var parsed) && parsed != Guid.Empty)
        {
            ((JsonObject)body["role"]!)["id"] = roleId;
        }

        GraphResponse response;
        try
        {
            response = await graph.PostAsync(new GraphRequest(ScopesPath(packageId), desired.Api, hint: desired.RetryHint), body, cancellationToken).ConfigureAwait(false);
        }
        catch (GraphException ex) when (IsNoGovernanceSku(ex))
        {
            throw new ProviderException(
                GovernanceLicenseRequired,
                $"{desired.Describe()}: Graph refused the '{role}' role of group {groupId} ({ex.Code}: {ex.GraphMessage}). Eligible group roles in an access package need a Microsoft Entra ID Governance licence in the tenant (Learn: entitlement-management-access-package-eligible, licensing-fundamentals). Assign the licence, or grant 'Member' / 'Owner' instead of the eligible role.",
                desired.Id,
                ex);
        }

        var live = response.Body as JsonObject
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: Graph returned no object for the created resource role scope.", desired.Id);
        string id = JsonObjects.GetId(live)
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: Graph returned a resource role scope without 'id'.", desired.Id);
        live["role"] ??= body["role"]!.DeepClone();
        live["scope"] ??= body["scope"]!.DeepClone();
        string matchedName = JsonObjects.GetString(match, "displayName") ?? role;
        var view = View(live, packageId, groupId, string.Equals(matchedName.Trim(), role, StringComparison.OrdinalIgnoreCase) ? matchedName : role);
        view[OriginIdProperty] = groupId;
        return new AppliedResource(desired.Id, id, view, ResolveKey(desired));
    }

    /// <summary>Nothing is mutable (every property is part of the key).</summary>
    public override Task<AppliedResource> UpdateAsync(ResourceInstance desired, ExistingResource existing, ResourceChange change, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(graph);
        return Task.FromResult(new AppliedResource(desired.Id, existing.GraphId, existing.Raw, ResolveKey(desired)));
    }

    // ---- delete ----

    /// <summary>
    /// The people who lose the role: Graph does not refuse the delete of a role scope while the package has assignments, it removes the role from every
    /// assignee (Learn: entitlement-management-access-package-resources). Without a delete planned the assignments are not read (the key read does not
    /// list them), so this warns that they were not counted; <see cref="DescribeDeleteRiskAsync"/> counts them.
    /// </summary>
    public string? DescribeDeleteRisk(ResourceKey key, ExistingResource live)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(live);
        return DeleteRisk(live, null, "were not counted");
    }

    /// <summary>The warning of <see cref="DescribeDeleteRisk"/> with the package's delivered assignments counted now: only when a delete is planned.</summary>
    public async Task<string?> DescribeDeleteRiskAsync(ResourceKey key, ExistingResource live, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(graph);
        string? packageId = JsonObjects.GetString(live.Raw, PackageProperty) ?? (key.TryGet(PackageProperty, out var fromKey) ? fromKey : null);
        int? count = packageId is null ? null : await EntitlementManagementGraph.CountDeliveredAssignmentsAsync(graph, packageId, key.Api, cancellationToken).ConfigureAwait(false);
        return DeleteRisk(live, count, "could not be read");
    }

    private static string? DeleteRisk(ExistingResource live, int? delivered, string unknown)
    {
        string package = JsonObjects.GetString(live.Raw, PackageNameProperty) is { Length: > 0 } name
            ? $"access package '{name}' ({JsonObjects.GetString(live.Raw, PackageProperty)})"
            : $"access package {JsonObjects.GetString(live.Raw, PackageProperty)}";
        string noun = AccessPackageResourceOrigin.Noun(JsonObjects.GetString(live.Raw, OriginSystemProperty) ?? EntitlementManagementGraph.GroupOrigin);
        string grant = $"the '{JsonObjects.GetString(live.Raw, RoleProperty)}' role of {noun} {JsonObjects.GetString(live.Raw, OriginIdProperty)}";
        if (delivered is not { } count)
        {
            return $"The active assignments of {package} {unknown}: everyone assigned the package loses {grant} when this role scope is deleted (Graph does not refuse it).";
        }

        return count == 0
            ? null
            : $"{count} active assignment{(count == 1 ? string.Empty : "s")} of {package} hold{(count == 1 ? "s" : string.Empty)} {grant}: deleting this role scope removes the role from {(count == 1 ? "that person" : "those people")} (Graph does not refuse it).";
    }

    public override async Task DeleteAsync(ResourceKey key, ExistingResource live, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(graph);
        string packageId = JsonObjects.GetString(live.Raw, PackageProperty) ?? (key.TryGet(PackageProperty, out var fromKey) ? fromKey : string.Empty);
        try
        {
            await graph.DeleteAsync(new GraphRequest(ScopesPath(packageId) + "/" + GraphPath.Segment(live.GraphId), key.Api), cancellationToken).ConfigureAwait(false);
        }
        catch (GraphException ex) when (ex.IsNotFound)
        {
            // Already gone.
        }
    }

    // ---- helpers ----

    /// <summary>Graph's 400 <c>NoGovernanceSku</c> (matched on the code, any casing; the message as a fallback when the code is generic).</summary>
    internal static bool IsNoGovernanceSku(GraphException ex)
    {
        return ex.Code.Equals(NoGovernanceSku, StringComparison.OrdinalIgnoreCase)
            || (ex.Status == System.Net.HttpStatusCode.BadRequest && ex.GraphMessage.Contains(NoGovernanceSku, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Which name to use for the kind of resource, after the roles the catalog offers (docs/access-packages.md, "Roles").</summary>
    private static string RoleHint(string originSystem)
    {
        return originSystem switch
        {
            AccessPackageResourceOrigin.Application => "; name an app role by its display name or its id",
            AccessPackageResourceOrigin.SharePointOnline => "; name a site group by its full display name (for example 'Contoso Members') or its number in the site",
            _ => string.Empty,
        };
    }

    private static string ScopesPath(string packageId)
    {
        return EntitlementManagementGraph.AccessPackagesPath + "/" + GraphPath.Segment(packageId) + "/resourceRoleScopes";
    }

    /// <summary>The Graph object plus the key tuple, the diff view (only the tuple and origin system are compared).</summary>
    private static JsonObject View(JsonObject roleScope, string packageId, string groupId, string role)
    {
        var view = (JsonObject)roleScope.DeepClone();
        view[PackageProperty] = packageId;
        view[OriginIdProperty] = JsonObjects.GetString((roleScope["scope"] as JsonObject) ?? [], "originId") ?? groupId;
        view[RoleProperty] = role;
        view[OriginSystemProperty] = JsonObjects.GetString((roleScope["role"] as JsonObject) ?? [], "originSystem") ?? EntitlementManagementGraph.GroupOrigin;
        return view;
    }

    /// <summary>Only the key tuple and origin system take part in the diff.</summary>
    private ExistingResource KeyView(JsonObject live)
    {
        var existing = ToExisting(live);
        var properties = new JsonObject
        {
            [PackageProperty] = JsonObjects.GetString(live, PackageProperty),
            [OriginIdProperty] = JsonObjects.GetString(live, OriginIdProperty),
            [RoleProperty] = JsonObjects.GetString(live, RoleProperty),
            [OriginSystemProperty] = JsonObjects.GetString(live, OriginSystemProperty),
        };
        return existing with { Properties = properties };
    }

    private static string OriginSystem(JsonObject properties)
    {
        return AccessPackageResourceOrigin.Of(properties, OriginSystemProperty);
    }

    /// <summary>The declared <c>originSystem</c>, else the catalog resource's own (so a role scope of an app or site may leave it out).</summary>
    private static string OriginSystem(JsonObject properties, JsonObject catalogResource)
    {
        return JsonObjects.GetString(properties, OriginSystemProperty) is { Length: > 0 }
            ? OriginSystem(properties)
            : AccessPackageResourceOrigin.Of(catalogResource, "originSystem");
    }

    private static string Required(ResourceInstance desired, JsonObject properties, string name)
    {
        return JsonObjects.GetString(properties, name) is { Length: > 0 } value
            ? value
            : throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: {name} is required.", desired.Id);
    }
}
