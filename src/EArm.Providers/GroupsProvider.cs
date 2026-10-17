using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Core.Json;
using EArm.Core.Secrets;
using EArm.Core.Types;
using EArm.Engine;
using EArm.Engine.Providers;
using EArm.Expressions.Values;

namespace EArm.Providers;

/// <summary>
/// <c>Microsoft.Entra/groups</c>. The resource name is the Graph <c>uniqueName</c>:
/// read <c>GET /groups(uniqueName='…')</c>, upsert <c>PATCH /groups(uniqueName='…')</c> with
/// <c>Prefer: create-if-missing</c>, update <c>PATCH /groups/{id}</c>, delete <c>DELETE /groups/{id}</c>.
/// Kinds: security (<c>groupTypes: []</c>, <c>mailEnabled: false</c>, <c>securityEnabled: true</c>), Microsoft 365
/// (<c>["Unified"]</c> ⇒ <c>mailEnabled: true</c>), dynamic (<c>DynamicMembership</c> ⇒ <c>membershipRule</c>
/// required, <c>membershipRuleProcessingState: On</c> injected). Mail-enabled security groups and distribution
/// lists are adopt-only (create is an error). <c>mailNickname</c> is derived from the name when absent.
/// Immutable: <c>uniqueName</c>, <c>mailEnabled</c>, <c>isAssignableToRole</c> (the overlay), the <c>Unified</c> presence in
/// <c>groupTypes</c> and <c>visibility: HiddenMembership</c> (checked in <see cref="Plan"/>); <c>mailNickname</c>, <c>Public</c> / <c>Private</c>
/// visibility and <c>DynamicMembership</c> are updated in place (Learn group-update). A dynamic toggle or rule
/// change pauses membership processing first. <c>assignedLicenses</c> (group-based licensing) is applied as an
/// action after the object write; <c>members</c>, <c>owners</c>, <c>memberOf</c>, <c>administrativeUnits</c> are
/// applied by the edge pass. Security (dynamic ones included) and Microsoft 365 groups are soft-deleted and keep their keys for
/// 30 days (Learn group-delete): a create probes the recycle bin and conflicts, or restores with <c>--on-soft-deleted-conflict restore</c>;
/// only distribution lists and mail-enabled security groups (adopt-only) are deleted permanently, and that delete warns.
/// A group created outside EARM has no uniqueName: when none holds the key, the one group without a uniqueName whose displayName equals the
/// declared one is adopted (<c>GroupAdoptedByDisplayName</c>; the update sets its uniqueName first, a refusal is <c>UniqueNameNotSettable</c>),
/// several are <c>AmbiguousMatch</c>; <c>deployment delete</c> never deletes such a group (deletes go by uniqueName).
/// </summary>
[EntraProvider("Microsoft.Entra/groups")]
public sealed partial class GroupsProvider : ProviderBase, IDeleteWarningSource, IDeleteRefusalSource
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
    /// required properties are defaulted per kind). <c>groupTypes</c> / <c>visibility</c> are not in the overlay's immutable list (only the
    /// <c>Unified</c> presence and <c>HiddenMembership</c> are immutable, checked in <see cref="Plan"/>); they are filtered here too so an
    /// overlay that lists them cannot block a <c>DynamicMembership</c> toggle or a Public / Private change.
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

    /// <summary>
    /// The uniqueName key, with the declared <c>displayName</c> (when it is a known string) as a read hint (<see cref="DisplayNameHint"/>): a group
    /// created outside EARM has no uniqueName, and <see cref="ReadAsync"/> adopts it by display name. The hint is not part of the identity
    /// (never persisted, never compared), so a delete, which reads by the recorded key alone, never adopts by display name.
    /// </summary>
    public override ResourceKey ResolveKey(ResourceInstance desired)
    {
        var key = base.ResolveKey(desired);
        _selects.Register(key, Definition, desired);
        if (desired.Properties is { } properties
            && JsonObjects.TryGetIgnoreCase(properties, "displayName", out var declared)
            && declared is not null
            && !SecretJson.IsSecret(declared)
            && !UnknownJson.IsUnknown(declared)
            && JsonValues.TryGetString(declared, out var displayName)
            && !string.IsNullOrWhiteSpace(displayName))
        {
            key = key with { Hints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [DisplayNameHint] = displayName } };
        }

        return key;
    }

    /// <summary>The key hint (<see cref="ResourceKey.Hints"/>) carrying the declared <c>displayName</c> for the adoption fallback of <see cref="ReadAsync"/>.</summary>
    public const string DisplayNameHint = "displayName";

    // ---- read ----

    /// <summary>
    /// By uniqueName; when no group holds it and the key carries <see cref="DisplayNameHint"/>, by display name: exactly one group with that
    /// displayName (case-insensitive) and NO uniqueName is adopted (<see cref="Plan"/> then sets its uniqueName), several are
    /// <see cref="ProviderException.AmbiguousMatch"/>, none is "does not exist". A same-name group that holds another uniqueName belongs to another
    /// key and is never adopted; one that already holds this uniqueName (the key read lagged) is the object itself.
    /// </summary>
    public override async Task<ExistingResource?> ReadAsync(ResourceKey key, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(graph);

        var select = _selects.For(key, ManagedSelect);
        var live = await GetByUniqueNameAsync(graph, key, select, cancellationToken).ConfigureAwait(false);
        if (live is null && key.Hints.TryGetValue(DisplayNameHint, out var displayName))
        {
            live = await FindAdoptableByDisplayNameAsync(graph, key, displayName, select, cancellationToken).ConfigureAwait(false);
        }

        if (live is null)
        {
            return null;
        }

        await _licensing.EnrichAsync(live, graph, key.Api, cancellationToken).ConfigureAwait(false);
        return ToExisting(live);
    }

    // ---- delete ----

    public override Task DeleteAsync(ResourceKey key, ExistingResource live, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(graph);
        var api = key.Api;
        return graph.DeleteAsync(new GraphRequest(Collection + "/" + GraphPath.Segment(live.GraphId), api), cancellationToken);
    }

    /// <summary>
    /// Only the kinds Learn (group-delete) does not list as restorable are deleted permanently: distribution lists, and mail-enabled security groups
    /// (adopt-only, Exchange-backed). Security and Microsoft 365 groups wait 30 days in the recycle bin, so they carry no warning.
    /// </summary>
    public string? DescribeDeleteRisk(ResourceKey key, ExistingResource live)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(live);
        bool permanent = GroupsShape.Analyze(live.Raw).IsPermanentlyDeleted;
        return permanent
            ? "this group is a distribution list or a mail-enabled security group and is deleted permanently (no 30-day recycle bin); its memberships, owners and role assignments are lost."
            : null;
    }
    /// <summary>
    /// Deletes go by uniqueName only: a group without one was found by its display name (created outside EARM and not adopted yet), so
    /// <c>deployment delete</c> keeps it instead of deleting a group no deployment has taken over.
    /// </summary>
    public string? DescribeDeleteRefusal(ResourceKey key, ExistingResource live)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(live);
        return string.IsNullOrEmpty(JsonObjects.GetString(live.Raw, "uniqueName"))
            ? $"the group {live.GraphId} has no uniqueName: it matches only by display name (created outside EARM and not adopted by a deployment yet), and deletes go by uniqueName. Deploy the template to adopt it first, or delete it yourself."
            : null;
    }

    // ---- helpers ----

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
