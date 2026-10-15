using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Core.Json;
using EArm.Core.Secrets;
using EArm.Core.Types;
using EArm.Engine;
using EArm.Engine.Providers;
using EArm.Engine.State;
using EArm.Expressions.Values;
using GraphApi = EArm.Graph.GraphApi;

namespace EArm.Providers;

/// <summary>
/// <c>Microsoft.Entra/groups</c>. The resource name is the Graph <c>uniqueName</c>:
/// read <c>GET /groups(uniqueName='…')</c> (pinned id first), upsert <c>PATCH /groups(uniqueName='…')</c> with
/// <c>Prefer: create-if-missing</c>, update <c>PATCH /groups/{id}</c>, delete <c>DELETE /groups/{id}</c>.
/// Kinds: security (<c>groupTypes: []</c>, <c>mailEnabled: false</c>, <c>securityEnabled: true</c>), Microsoft 365
/// (<c>["Unified"]</c> ⇒ <c>mailEnabled: true</c>), dynamic (<c>DynamicMembership</c> ⇒ <c>membershipRule</c>
/// required, <c>membershipRuleProcessingState: On</c> injected). Mail-enabled security groups and distribution
/// lists are adopt-only (create is an error). <c>mailNickname</c> is derived from the name when absent.
/// Immutable: <c>uniqueName</c>, <c>mailNickname</c>, <c>mailEnabled</c>, <c>isAssignableToRole</c>, the
/// <c>Unified</c> presence in <c>groupTypes</c>, <c>visibility: HiddenMembership</c>. A dynamic toggle or rule
/// change pauses membership processing first. <c>assignedLicenses</c> (group-based licensing) is applied as an
/// action after the object write; <c>members</c>, <c>owners</c>, <c>memberOf</c>, <c>administrativeUnits</c> are
/// applied by the edge pass. Security (dynamic ones included) and Microsoft 365 groups are soft-deleted and keep their keys for
/// 30 days (Learn group-delete): a create probes the recycle bin and conflicts, or restores with <c>--on-soft-deleted-conflict restore</c>;
/// only distribution lists and mail-enabled security groups (adopt-only) are deleted permanently, and that delete warns.
/// </summary>
[EntraProvider("Microsoft.Entra/groups")]
public sealed class GroupsProvider : ProviderBase, IDeleteWarningSource
{
    private const string Collection = "/groups";
    private const string DeletedGroups = "/directory/deletedItems/microsoft.graph.group";

    private static readonly string[] ManagedSelect =
    [
        "id", "uniqueName", "displayName", "description", "mailEnabled", "mailNickname", "securityEnabled", "groupTypes",
        "isAssignableToRole", "visibility", "membershipRule", "membershipRuleProcessingState", "mail", "assignedLicenses", "createdDateTime",
    ];

    private static readonly JsonPath GroupTypesPath = JsonPath.Of("properties", "groupTypes");
    private static readonly JsonPath VisibilityPath = JsonPath.Of("properties", "visibility");
    private static readonly JsonPath MembershipRulePath = JsonPath.Of("properties", "membershipRule");
    private static readonly JsonPath LicensesPath = JsonPath.Of("properties", "assignedLicenses");

    private readonly GroupsLicensing _licensing = new();

    /// <summary>The top-level properties each template declares, selected next to <see cref="ManagedSelect"/> on its reads (G-08).</summary>
    private readonly DeclaredSelects _selects = new();

    // Deployment-wide knob captured from PlanOptions in Plan(); CreateAsync has no access to the options otherwise.
    private SoftDeletedConflict _onSoftDeletedConflict = SoftDeletedConflict.Error;

    public GroupsProvider(ResourceTypeDefinition definition, ResourceTypeCatalog catalog)
        : base(Adjust(definition), catalog)
    {
    }

    /// <summary>
    /// The overlay with the group-specific adjustments: only <c>displayName</c> is required on create (the other
    /// required properties are defaulted per kind), and <c>groupTypes</c> / <c>visibility</c> leave the blanket
    /// immutable list because only the <c>Unified</c> presence and <c>HiddenMembership</c> are immutable (checked in <see cref="Plan"/>).
    /// </summary>
    private static ResourceTypeDefinition Adjust(ResourceTypeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return definition with
        {
            RequiredOnCreate = ["displayName"],
            Immutable = definition.Immutable.Where(p => p.ToString() is not ("groupTypes" or "visibility")).ToList(),
        };
    }

    // ---- permissions ----

    public override IReadOnlyList<string> RequiredPermissions(ResourceInstance? desired)
    {
        if (desired is null)
        {
            return Definition.Permissions.Application;
        }

        var properties = desired.Properties ?? desired.Declaration.Properties as JsonObject;
        var edges = desired.EdgeProperties ?? desired.Declaration.Properties as JsonObject;
        var permissions = new List<string> { "Group.ReadWrite.All" };
        if (Declares(edges, "members") || Declares(edges, "owners") || Declares(edges, "memberOf"))
        {
            permissions.Add("GroupMember.ReadWrite.All");
        }

        if (Declares(edges, "administrativeUnits"))
        {
            permissions.Add("AdministrativeUnit.ReadWrite.All");
        }

        if (JsonObjects.TryGetIgnoreCase(properties, "isAssignableToRole", out var assignable) && JsonValues.TryGetBool(assignable, out bool roleAssignable) && roleAssignable)
        {
            permissions.Add("RoleManagement.ReadWrite.Directory");
        }

        if (Declares(edges, "assignedLicenses"))
        {
            // Learn (group-assignlicense, subscribedsku-list): assignLicense needs LicenseAssignment.ReadWrite.All, resolving skuPartNumber reads
            // GET /subscribedSkus (LicenseAssignment.Read.All); Organization.Read.All is not needed.
            permissions.Add("LicenseAssignment.ReadWrite.All");
            permissions.Add("LicenseAssignment.Read.All");
        }

        return permissions;
    }

    private static bool Declares(JsonObject? properties, string name)
    {
        return JsonObjects.TryGetIgnoreCase(properties, name, out var value) && !JsonObjects.IsAbsent(value);
    }

    public override ResourceKey ResolveKey(ResourceInstance desired)
    {
        var key = base.ResolveKey(desired);
        _selects.Register(key, Definition, desired);
        return key;
    }

    // ---- read ----

    public override async Task<ExistingResource?> ReadAsync(ResourceKey key, string? stateId, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(graph);

        JsonObject? live = null;
        var select = _selects.For(key, ManagedSelect);
        if (stateId is not null)
        {
            live = await graph.GetAsync(new GraphRequest(Collection + "/" + GraphPath.Segment(stateId), key.Api).WithSelect(select), cancellationToken).ConfigureAwait(false) as JsonObject;
        }

        live ??= await GetByUniqueNameAsync(graph, key, select, cancellationToken).ConfigureAwait(false);
        if (live is null)
        {
            return null;
        }

        await _licensing.EnrichAsync(live, graph, key.Api, cancellationToken).ConfigureAwait(false);
        return ToExisting(live);
    }

    // ---- plan ----

    public override ResourceChange Plan(ResourceInstance desired, ExistingResource? existing, PlanOptions options)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(options);
        _onSoftDeletedConflict = options.OnSoftDeletedConflict;

        var change = base.Plan(desired, existing, options);
        if (change.Type == ChangeType.Ignore || desired.Properties is not { } properties)
        {
            return change;
        }

        var analysis = GroupsShape.Analyze(properties);
        if (existing is null)
        {
            if (!change.HasError && GroupsShape.CreateError(analysis, desired.Describe()) is { } createError)
            {
                change = Unsupported(change, createError);
            }
        }
        else if (!change.HasError)
        {
            JsonObjects.TryGetIgnoreCase(existing.Properties, "groupTypes", out var liveTypes);
            JsonObjects.TryGetIgnoreCase(properties, "groupTypes", out var desiredTypes);
            bool typesChange = change.Deltas.Any(d => d.HasEffect && d.Path.Equals(GroupTypesPath));
            if (typesChange && GroupsShape.UnifiedPresenceDiffers(liveTypes, desiredTypes))
            {
                change = Unsupported(change, $"Immutable property groupTypes ('Unified') of {desired.Describe()} differs from the live object: a group cannot be converted between a security group and a Microsoft 365 group; delete and recreate the resource.");
            }

            JsonObjects.TryGetIgnoreCase(existing.Properties, "visibility", out var liveVisibility);
            JsonObjects.TryGetIgnoreCase(properties, "visibility", out var desiredVisibility);
            if (!change.HasError && change.Deltas.Any(d => d.HasEffect && d.Path.Equals(VisibilityPath)) && GroupsShape.HiddenMembershipInvolved(liveVisibility, desiredVisibility))
            {
                change = Unsupported(change, $"Immutable property visibility of {desired.Describe()} differs from the live object: 'HiddenMembership' can only be set at creation and never changed.");
            }

            if (!change.HasError && IsDynamicChange(change, liveTypes, desiredTypes))
            {
                // A live group without a rule reads back membershipRule: null (Graph returns nulls for selected properties), which is no rule.
                if (analysis.IsDynamic && !analysis.HasMembershipRule && !(JsonObjects.TryGetIgnoreCase(existing.Properties, "membershipRule", out var liveRule) && !JsonObjects.IsAbsent(liveRule)))
                {
                    change = Unsupported(change, $"{desired.Describe()}: a dynamic group (groupTypes contains 'DynamicMembership') requires 'membershipRule'.");
                }
                else if (GroupsShape.GroupTypesContain(liveTypes, GroupsShape.DynamicMembership))
                {
                    change = change with { Reasons = [.. change.Reasons, "membership rule processing is paused (membershipRuleProcessingState: Paused) before the dynamic membership change"] };
                }
            }
        }

        return WithLicenseDelta(change, desired, existing);
    }

    private static ResourceChange Unsupported(ResourceChange change, string message)
    {
        return change with { UnsupportedReason = message, Reasons = [.. change.Reasons, message] };
    }

    private static bool IsDynamicChange(ResourceChange change, JsonNode? liveTypes, JsonNode? desiredTypes)
    {
        bool typesChange = change.Deltas.Any(d => d.HasEffect && d.Path.Equals(GroupTypesPath));
        bool ruleChange = change.Deltas.Any(d => d.HasEffect && d.Path.Equals(MembershipRulePath));
        return ruleChange || (typesChange && GroupsShape.DynamicPresenceDiffers(liveTypes, desiredTypes));
    }

    /// <summary>Adds the <c>properties.assignedLicenses</c> array delta (action-backed, applied after the object write).</summary>
    private ResourceChange WithLicenseDelta(ResourceChange change, ResourceInstance desired, ExistingResource? existing)
    {
        if (!JsonObjects.TryGetIgnoreCase(desired.EdgeProperties, "assignedLicenses", out var declared) || declared is null)
        {
            return change;
        }

        if (UnknownJson.IsUnknown(declared))
        {
            return change with { IsUnknown = true, Reasons = [.. change.Reasons, "assignedLicenses is only known at deployment time"] };
        }

        if (declared is not JsonArray desiredLicenses)
        {
            return change.HasError ? change : Unsupported(change, $"{desired.Describe()}: properties.assignedLicenses must be an array of {{ skuId | skuPartNumber, disabledPlans }} objects.");
        }

        JsonObjects.TryGetIgnoreCase(existing?.Properties, "assignedLicenses", out var liveNode);
        var diff = GroupsLicensing.Diff(desiredLicenses, liveNode as JsonArray, _licensing.Cached);
        if (diff.Errors.Count > 0 && !change.HasError)
        {
            change = Unsupported(change, $"{desired.Describe()}: {string.Join(" ", diff.Errors)}");
        }

        if (diff.Children.Count == 0)
        {
            return change;
        }

        var delta = new PropertyDelta(LicensesPath, PropertyChangeType.Array, null, null, diff.HasUnknown, false, diff.Children);
        var type = change.Type == ChangeType.NoChange ? ChangeType.Modify : change.Type;
        return change with
        {
            Type = type,
            Mode = type == ChangeType.Modify && change.Mode == ChangeMode.None ? ChangeMode.Patch : change.Mode,
            Deltas = [.. change.Deltas, delta],
            IsUnknown = change.IsUnknown || diff.HasUnknown,
        };
    }

    // ---- create ----

    public override async Task<AppliedResource> CreateAsync(ResourceInstance desired, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(graph);

        var key = ResolveKey(desired);
        var properties = desired.Properties ?? throw new InvalidOperationException($"{desired.Describe()}: properties must be evaluated before create.");
        var analysis = GroupsShape.Analyze(properties);
        if (GroupsShape.CreateError(analysis, desired.Describe()) is { } createError)
        {
            throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, createError, desired.Id);
        }

        var body = BuildCreateBody(desired);
        string uniqueName = ProviderSupport.SingleKey(key, "uniqueName");
        GroupsShape.ApplyCreateDefaults(body, uniqueName, analysis);
        RemoveUniqueName(body);
        var warnings = new List<ProviderWarning>();
        if (analysis.IsUnified && !AddOwnersAtCreate(body, desired))
        {
            warnings.Add(new ProviderWarning(ProviderWarnings.GroupHasNoOwnerAtCreate, $"{desired.Describe()} is a Microsoft 365 group created without owners: with an application token Graph creates it anonymously (no owner, and the SharePoint site may not be provisioned until an owner exists). Declare properties.owners."));
        }
        else if (!analysis.IsUnified)
        {
            AddOwnersAtCreate(body, desired);
        }

        // Security and Microsoft 365 groups both sit in the recycle bin for 30 days and keep their keys there (Learn group-delete).
        var deleted = analysis.IsUnified
            ? await FindSoftDeletedAsync(uniqueName, JsonObjects.GetString(body, "mailNickname"), desired.Api, graph, warnings, cancellationToken).ConfigureAwait(false)
            : await DeletedItemsProbe.FindAsync(graph, DeletedGroups, "group", "uniqueName", uniqueName, ["id", "displayName", "uniqueName", "mailNickname", "deletedDateTime"], desired.Api, advanced: false, warnings, cancellationToken).ConfigureAwait(false);
        if (deleted is not null)
        {
            return await RestoreOrFailAsync(desired, key, body, deleted, analysis, graph, warnings, cancellationToken).ConfigureAwait(false);
        }

        string path = AlternateKeyPath(key);
        var response = await graph.PatchAsync(new GraphRequest(path, desired.Api, hint: desired.RetryHint).WithPrefer("create-if-missing"), body, cancellationToken).ConfigureAwait(false);
        var live = response.Body as JsonObject
            ?? await graph.GetAsync(new GraphRequest(path, desired.Api, hint: RetryHint.ExpectReplicationLag).WithSelect(_selects.For(key, ManagedSelect)), cancellationToken).ConfigureAwait(false) as JsonObject
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{TypeName}: the upsert of '{desired.Name}' returned no object.", desired.Id);
        string id = RequireId(live, desired);

        // A failure here leaves a group that exists: the partial result goes with the exception so the executor records the object instead of losing it.
        var created = new AppliedResource(desired.Id, id, live, key, Extra(analysis)) { Warnings = warnings };
        await ApplyLicensesOrPartialAsync(desired, id, null, created, "created", graph, cancellationToken).ConfigureAwait(false);
        return created;
    }

    /// <summary>
    /// Applies the declared licenses after the object write; a failure is rethrown carrying <paramref name="written"/> (<see cref="ProviderException.Partial"/>),
    /// because the group exists whatever happened to its licenses (review round 3).
    /// </summary>
    private async Task ApplyLicensesOrPartialAsync(ResourceInstance desired, string graphId, JsonObject? live, AppliedResource written, string verb, IGraphClient graph, CancellationToken cancellationToken)
    {
        try
        {
            await ApplyLicensesAsync(desired, graphId, live, graph, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is GraphException or GraphTransportException or ProviderException { Partial: null })
        {
            throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: the group was {verb} (id {graphId}) but assigning its licenses failed: {ex.Message}", desired.Id, ex) { Partial = written };
        }
    }

    /// <summary>
    /// Learn (group-upsert / group-post-groups): a group created with an application token and without owners is created
    /// anonymously. When the template declares <c>owners</c> they are bound in the create body (<c>owners@odata.bind</c>, at most 20,
    /// object ids only); the edge pass stays authoritative afterwards. True when at least one owner was bound.
    /// </summary>
    private static bool AddOwnersAtCreate(JsonObject body, ResourceInstance desired)
    {
        if (!JsonObjects.TryGetIgnoreCase(desired.EdgeProperties, "owners", out var declared) || declared is not JsonArray owners)
        {
            return false;
        }

        string prefix = desired.Api.BaseUrl() + "/directoryObjects/";
        var bound = new JsonArray();
        foreach (var owner in owners)
        {
            if (owner is not null && !SecretJson.IsSecret(owner) && !UnknownJson.IsUnknown(owner) && JsonValues.TryGetString(owner, out var id) && Guid.TryParse(id, out _) && bound.Count < 20)
            {
                bound.Add(prefix + id);
            }
        }

        if (bound.Count == 0)
        {
            return false;
        }

        body["owners@odata.bind"] = bound;
        return true;
    }

    /// <summary>The upsert addresses the group by its <c>uniqueName</c> (the URL key); Graph documents it as read-only, so it is never sent in the body.</summary>
    private static void RemoveUniqueName(JsonObject body)
    {
        if (JsonObjects.FindKey(body, "uniqueName") is { } key)
        {
            body.Remove(key);
        }
    }

    /// <summary>
    /// <c>GET /groups(uniqueName='…')</c>. <c>uniqueName</c> is documented as an alternate key for the upsert only, so a tenant that
    /// rejects the key form (400) is asked with <c>$filter=uniqueName eq '…'</c> (advanced query). A 404 stays "does not exist".
    /// </summary>
    private static async Task<JsonObject?> GetByUniqueNameAsync(IGraphClient graph, ResourceKey key, string[] select, CancellationToken cancellationToken)
    {
        string uniqueName = ProviderSupport.SingleKey(key, "uniqueName");
        try
        {
            return await graph.GetAsync(new GraphRequest(AlternateKeyPath(key), key.Api).WithSelect(select), cancellationToken).ConfigureAwait(false) as JsonObject;
        }
        catch (GraphException ex) when (ex.Status == HttpStatusCode.BadRequest)
        {
            var request = new GraphRequest(Collection, key.Api)
                .WithFilter("uniqueName eq '" + ProviderSupport.EscapeLiteral(uniqueName) + "'")
                .WithCount()
                .WithConsistencyLevelEventual()
                .WithSelect(select);
            await foreach (var item in graph.GetAllPagesAsync(request, cancellationToken).ConfigureAwait(false))
            {
                if (item is JsonObject match && string.Equals(JsonObjects.GetString(match, "uniqueName"), uniqueName, StringComparison.OrdinalIgnoreCase))
                {
                    return match;
                }
            }

            return null;
        }
    }

    private async Task<JsonObject?> FindSoftDeletedAsync(string uniqueName, string? mailNickname, GraphApi api, IGraphClient graph, List<ProviderWarning> warnings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(mailNickname))
        {
            return null;
        }

        JsonNode? page;
        try
        {
            var request = new GraphRequest(DeletedGroups, api)
                .WithFilter($"mailNickname eq '{ProviderSupport.EscapeLiteral(mailNickname)}'")
                .WithSelect("id", "displayName", "uniqueName", "mailNickname", "deletedDateTime");
            page = await graph.GetAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (GraphException ex)
        {
            // The probe is best effort: a tenant (or an identity without Group.Read.All) that refuses the query must not block creates.
            warnings.Add(ProviderSupport.ProbeSkipped("group", mailNickname, ex));
            return null;
        }

        if ((page as JsonObject)?["value"] is not JsonArray items)
        {
            return null;
        }

        foreach (var item in items)
        {
            if (item is JsonObject deleted && (JsonObjects.KeyEquals(JsonObjects.GetString(deleted, "uniqueName"), uniqueName) || JsonObjects.KeyEquals(JsonObjects.GetString(deleted, "mailNickname"), mailNickname)))
            {
                return deleted;
            }
        }

        return null;
    }

    private async Task<AppliedResource> RestoreOrFailAsync(ResourceInstance desired, ResourceKey key, JsonObject body, JsonObject deleted, GroupsShape.Analysis analysis, IGraphClient graph, List<ProviderWarning> warnings, CancellationToken cancellationToken)
    {
        string deletedId = JsonObjects.GetId(deleted) ?? "?";
        string held = analysis.IsUnified ? $"mailNickname '{JsonObjects.GetString(deleted, "mailNickname")}'" : $"uniqueName '{JsonObjects.GetString(deleted, "uniqueName")}'";
        string description = $"a soft-deleted {(analysis.IsUnified ? "Microsoft 365" : "security")} group '{JsonObjects.GetString(deleted, "displayName")}' ({deletedId}, deleted {JsonObjects.GetString(deleted, "deletedDateTime") ?? "recently"}) still holds {held}";
        if (_onSoftDeletedConflict != SoftDeletedConflict.Restore)
        {
            throw new ProviderException(ProviderException.SoftDeletedConflict, $"{desired.Describe()} cannot be created: {description}. Restore it with --on-soft-deleted-conflict restore, or purge it from the recycle bin (DELETE /directory/deletedItems/{deletedId}).", desired.Id);
        }

        var restored = await graph.PostAsync(new GraphRequest("/directory/deletedItems/" + GraphPath.Segment(deletedId) + "/restore", desired.Api, hint: desired.RetryHint), null, cancellationToken).ConfigureAwait(false);
        string id = JsonObjects.GetId(restored.Body as JsonObject) ?? deletedId;
        string path = Collection + "/" + GraphPath.Segment(id);
        var patch = (JsonObject)body.DeepClone();
        patch.Remove(JsonObjects.FindKey(patch, "mailNickname") ?? "mailNickname");
        patch.Remove("owners@odata.bind");
        await graph.PatchAsync(new GraphRequest(path, desired.Api, hint: RetryHint.ExpectReplicationLag), patch, cancellationToken).ConfigureAwait(false);
        var live = await graph.GetAsync(new GraphRequest(path, desired.Api, hint: RetryHint.ExpectReplicationLag).WithSelect(_selects.For(key, ManagedSelect)), cancellationToken).ConfigureAwait(false) as JsonObject
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{TypeName}: '{desired.Name}' vanished after being restored.", desired.Id);
        var restoredResult = new AppliedResource(desired.Id, id, live, key, Extra(GroupsShape.Analyze(live))) { Warnings = warnings };
        await ApplyLicensesOrPartialAsync(desired, id, live, restoredResult, "restored", graph, cancellationToken).ConfigureAwait(false);
        return restoredResult;
    }

    // ---- update ----

    public override async Task<AppliedResource> UpdateAsync(ResourceInstance desired, ExistingResource existing, ResourceChange change, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(graph);

        var key = ResolveKey(desired);
        var properties = desired.Properties ?? throw new InvalidOperationException($"{desired.Describe()}: properties must be evaluated before update.");
        var analysis = GroupsShape.Analyze(properties);
        string path = Collection + "/" + GraphPath.Segment(existing.GraphId);

        // The license delta is action-backed; it must not reach the PATCH body.
        var objectChange = change with { Deltas = change.Deltas.Where(d => !d.Path.Equals(LicensesPath)).ToList() };
        var body = BuildPatchBody(desired, existing, objectChange);

        JsonObjects.TryGetIgnoreCase(existing.Properties, "groupTypes", out var liveTypes);
        JsonObjects.TryGetIgnoreCase(properties, "groupTypes", out var desiredTypes);
        if (IsDynamicChange(objectChange, liveTypes, desiredTypes))
        {
            if (GroupsShape.GroupTypesContain(liveTypes, GroupsShape.DynamicMembership))
            {
                // Pause processing before the rule or the dynamic flag changes.
                await graph.PatchAsync(new GraphRequest(path, desired.Api, hint: desired.RetryHint), new JsonObject { ["membershipRuleProcessingState"] = GroupsShape.ProcessingPaused }, cancellationToken).ConfigureAwait(false);
            }

            if (analysis.IsDynamic)
            {
                JsonObjects.SetDefault(body, "membershipRuleProcessingState", JsonValue.Create(GroupsShape.ProcessingOn));
            }
        }

        if (body.Count > 0)
        {
            await graph.PatchAsync(new GraphRequest(path, desired.Api, hint: desired.RetryHint), body, cancellationToken).ConfigureAwait(false);
        }

        var live = await graph.GetAsync(new GraphRequest(path, desired.Api).WithSelect(_selects.For(key, ManagedSelect)), cancellationToken).ConfigureAwait(false) as JsonObject
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{TypeName}: '{desired.Name}' vanished after the update.", desired.Id);

        await ApplyLicensesAsync(desired, existing.GraphId, existing.Raw, graph, cancellationToken).ConfigureAwait(false);
        return new AppliedResource(desired.Id, existing.GraphId, live, key, Extra(analysis));
    }

    // ---- delete ----

    public override Task DeleteAsync(StateEntry entry, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(graph);
        var api = entry.ToResourceKey(Catalog).Api;
        return graph.DeleteAsync(new GraphRequest(Collection + "/" + GraphPath.Segment(entry.GraphId), api), cancellationToken);
    }

    /// <summary>
    /// Only the kinds Learn (group-delete) does not list as restorable are deleted permanently: distribution lists, and mail-enabled security groups
    /// (adopt-only, Exchange-backed). Security and Microsoft 365 groups wait 30 days in the recycle bin, so they carry no warning.
    /// </summary>
    public string? DescribeDeleteRisk(StateEntry entry, ExistingResource? existing)
    {
        ArgumentNullException.ThrowIfNull(entry);
        bool permanent;
        if (existing is not null)
        {
            permanent = GroupsShape.Analyze(existing.Raw).IsPermanentlyDeleted;
        }
        else if (JsonObjects.GetString(entry.Extra, "kind") is { } kind)
        {
            permanent = kind.Equals("distribution", StringComparison.OrdinalIgnoreCase) || kind.Equals("mailEnabledSecurity", StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            // Nothing says which kind it was: it was created by EARM (security or Microsoft 365), both recoverable for 30 days.
            permanent = false;
        }

        return permanent
            ? "this group is a distribution list or a mail-enabled security group and is deleted permanently (no 30-day recycle bin); its memberships, owners and role assignments are lost."
            : null;
    }
    // ---- helpers ----

    private async Task ApplyLicensesAsync(ResourceInstance desired, string graphId, JsonObject? live, IGraphClient graph, CancellationToken cancellationToken)
    {
        if (!JsonObjects.TryGetIgnoreCase(desired.EdgeProperties, "assignedLicenses", out var declared) || declared is null)
        {
            return;
        }

        if (declared is not JsonArray desiredLicenses)
        {
            throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: properties.assignedLicenses must be an array of {{ skuId | skuPartNumber, disabledPlans }} objects.", desired.Id);
        }

        JsonObjects.TryGetIgnoreCase(live, "assignedLicenses", out var liveNode);
        await _licensing.ApplyAsync(graph, desired.Api, Collection + "/" + GraphPath.Segment(graphId), desiredLicenses, liveNode as JsonArray, desired.RetryHint, desired.Describe(), cancellationToken).ConfigureAwait(false);
    }

    private static JsonObject Extra(GroupsShape.Analysis analysis)
    {
        return new JsonObject { ["kind"] = analysis.Kind, ["dynamic"] = analysis.IsDynamic };
    }

    private static string AlternateKeyPath(ResourceKey key)
    {
        return GraphPath.AlternateKey(Collection, "uniqueName", ProviderSupport.SingleKey(key, "uniqueName"));
    }

    private string RequireId(JsonObject live, ResourceInstance desired)
    {
        return JsonObjects.GetId(live)
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{TypeName}: Graph returned an object without 'id' for '{desired.Name}'.", desired.Id);
    }
}
