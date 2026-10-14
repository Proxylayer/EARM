using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EntraArm.Az;
using EntraArm.Core.Json;
using EntraArm.Core.Types;
using EntraArm.Engine;
using EntraArm.Engine.Providers;
using EntraArm.Engine.State;
using EntraArm.Expressions.Values;
using GraphApi = EntraArm.Az.GraphApi;

namespace EntraArm.Providers;

/// <summary>
/// <c>Microsoft.Entra/groups</c> (plan section 3.2). The resource name is the Graph <c>uniqueName</c>:
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
/// applied by the edge pass. Security groups are deleted permanently (Microsoft 365 groups are soft-deleted);
/// the delete warns.
/// </summary>
[EntraProvider("Microsoft.Entra/groups")]
public sealed class GroupsProvider : ProviderBase, IDeleteWarningSource
{
    private const string Collection = "/groups";
    private const string DeletedGroups = "/directory/deletedItems/microsoft.graph.group";

    private static readonly string[] ManagedSelect =
    [
        "id", "uniqueName", "displayName", "description", "mailEnabled", "mailNickname", "securityEnabled", "groupTypes",
        "isAssignableToRole", "visibility", "membershipRule", "membershipRuleProcessingState", "mail", "assignedLicenses",
    ];

    private static readonly JsonPath GroupTypesPath = JsonPath.Of("properties", "groupTypes");
    private static readonly JsonPath VisibilityPath = JsonPath.Of("properties", "visibility");
    private static readonly JsonPath MembershipRulePath = JsonPath.Of("properties", "membershipRule");
    private static readonly JsonPath LicensesPath = JsonPath.Of("properties", "assignedLicenses");

    private readonly GroupsLicensing _licensing = new();

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
            permissions.Add("Organization.Read.All");
        }

        return permissions;
    }

    private static bool Declares(JsonObject? properties, string name)
    {
        return JsonObjects.TryGetIgnoreCase(properties, name, out var value) && !JsonObjects.IsAbsent(value);
    }

    // ---- read ----

    public override async Task<ExistingResource?> ReadAsync(ResourceKey key, string? stateId, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(graph);

        JsonObject? live = null;
        if (stateId is not null)
        {
            live = await graph.GetAsync(new GraphRequest(Collection + "/" + GraphPath.Segment(stateId), key.Api).WithSelect(ManagedSelect), cancellationToken).ConfigureAwait(false) as JsonObject;
        }

        live ??= await graph.GetAsync(new GraphRequest(AlternateKeyPath(key), key.Api).WithSelect(ManagedSelect), cancellationToken).ConfigureAwait(false) as JsonObject;
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
                if (analysis.IsDynamic && !analysis.HasMembershipRule && !JsonObjects.TryGetIgnoreCase(existing.Properties, "membershipRule", out _))
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

    /// <summary>Adds the <c>properties.assignedLicenses</c> array delta (plan section 1.4: action-backed, applied after the object write).</summary>
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
        GroupsShape.ApplyCreateDefaults(body, key.Single, analysis);

        if (analysis.IsUnified)
        {
            // Plan section 3.1: a soft-deleted Microsoft 365 group keeps its keys for 30 days.
            var deleted = await FindSoftDeletedAsync(key.Single, JsonObjects.GetString(body, "mailNickname"), desired.Api, graph, cancellationToken).ConfigureAwait(false);
            if (deleted is not null)
            {
                return await RestoreOrFailAsync(desired, key, body, deleted, graph, cancellationToken).ConfigureAwait(false);
            }
        }

        string path = AlternateKeyPath(key);
        var response = await graph.PatchAsync(new GraphRequest(path, desired.Api, hint: desired.RetryHint).WithPrefer("create-if-missing"), body, cancellationToken).ConfigureAwait(false);
        var live = response.Body as JsonObject
            ?? await graph.GetAsync(new GraphRequest(path, desired.Api, hint: RetryHint.ExpectReplicationLag).WithSelect(ManagedSelect), cancellationToken).ConfigureAwait(false) as JsonObject
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{TypeName}: the upsert of '{desired.Name}' returned no object.", desired.Id);
        string id = RequireId(live, desired);

        await ApplyLicensesAsync(desired, id, null, graph, cancellationToken).ConfigureAwait(false);
        return new AppliedResource(desired.Id, id, live, key, Extra(analysis));
    }

    private async Task<JsonObject?> FindSoftDeletedAsync(string uniqueName, string? mailNickname, GraphApi api, IGraphClient graph, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(mailNickname))
        {
            return null;
        }

        JsonNode? page;
        try
        {
            var request = new GraphRequest(DeletedGroups, api)
                .WithFilter($"mailNickname eq '{mailNickname.Replace("'", "''", StringComparison.Ordinal)}'")
                .WithSelect("id", "displayName", "uniqueName", "mailNickname", "deletedDateTime");
            page = await graph.GetAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (GraphException)
        {
            // The probe is best effort: a tenant that refuses the query must not block creates.
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

    private async Task<AppliedResource> RestoreOrFailAsync(ResourceInstance desired, ResourceKey key, JsonObject body, JsonObject deleted, IGraphClient graph, CancellationToken cancellationToken)
    {
        string deletedId = JsonObjects.GetId(deleted) ?? "?";
        string description = $"a soft-deleted Microsoft 365 group '{JsonObjects.GetString(deleted, "displayName")}' ({deletedId}, deleted {JsonObjects.GetString(deleted, "deletedDateTime") ?? "recently"}) still holds mailNickname '{JsonObjects.GetString(deleted, "mailNickname")}'";
        if (_onSoftDeletedConflict != SoftDeletedConflict.Restore)
        {
            throw new ProviderException(ProviderException.SoftDeletedConflict, $"{desired.Describe()} cannot be created: {description}. Restore it with --on-soft-deleted-conflict restore, or purge it from the recycle bin (DELETE /directory/deletedItems/{deletedId}).", desired.Id);
        }

        var restored = await graph.PostAsync(new GraphRequest("/directory/deletedItems/" + GraphPath.Segment(deletedId) + "/restore", desired.Api, hint: desired.RetryHint), null, cancellationToken).ConfigureAwait(false);
        string id = JsonObjects.GetId(restored.Body as JsonObject) ?? deletedId;
        string path = Collection + "/" + GraphPath.Segment(id);
        var patch = (JsonObject)body.DeepClone();
        patch.Remove(JsonObjects.FindKey(patch, "mailNickname") ?? "mailNickname");
        await graph.PatchAsync(new GraphRequest(path, desired.Api, hint: RetryHint.ExpectReplicationLag), patch, cancellationToken).ConfigureAwait(false);
        var live = await graph.GetAsync(new GraphRequest(path, desired.Api, hint: RetryHint.ExpectReplicationLag).WithSelect(ManagedSelect), cancellationToken).ConfigureAwait(false) as JsonObject
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{TypeName}: '{desired.Name}' vanished after being restored.", desired.Id);
        await ApplyLicensesAsync(desired, id, live, graph, cancellationToken).ConfigureAwait(false);
        return new AppliedResource(desired.Id, id, live, key, Extra(GroupsShape.Analyze(live)));
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
                // Plan section 3.2: pause processing before the rule or the dynamic flag changes.
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

        var live = await graph.GetAsync(new GraphRequest(path, desired.Api).WithSelect(ManagedSelect), cancellationToken).ConfigureAwait(false) as JsonObject
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

    /// <summary>Plan section 3.2: security groups (and every non-Microsoft 365 kind) are deleted permanently.</summary>
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
            permanent = !kind.Equals("microsoft365", StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            permanent = true;
        }

        return permanent
            ? "this group is not a Microsoft 365 group and is deleted permanently (no 30-day recycle bin); its memberships, owners and role assignments are lost."
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
        return GraphPath.AlternateKey(Collection, "uniqueName", key.Single);
    }

    private string RequireId(JsonObject live, ResourceInstance desired)
    {
        return JsonObjects.GetId(live)
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{TypeName}: Graph returned an object without 'id' for '{desired.Name}'.", desired.Id);
    }
}
