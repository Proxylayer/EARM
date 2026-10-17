using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Core;
using EArm.Core.Json;
using EArm.Core.Secrets;
using EArm.Core.Types;
using EArm.Engine;
using EArm.Engine.Providers;
using EArm.Expressions.Values;
using GraphApi = EArm.Graph.GraphApi;

namespace EArm.Providers;

/// <summary>
/// <c>Microsoft.Entra/users</c>. The resource name is the
/// <c>userPrincipalName</c> (<c>local@domain</c>; a guest's <c>#EXT#</c> is percent-encoded on the path).
/// <para>
/// Requests per operation (every read selects <see cref="ManagedProperties"/>):
/// <list type="bullet">
/// <item><b>Read</b>: <c>GET /users/{upn}</c>; a 404 is "does not exist". When the user holds licenses, <c>GET /subscribedSkus</c> once per run resolves <c>skuPartNumber</c>.</item>
/// <item><b>Create</b>: the soft-delete probe <c>GET /directory/deletedItems/microsoft.graph.user?$filter=userPrincipalName eq '…'</c> (advanced query; a 400 falls back to listing) → on a hit either <c>ProviderException(SoftDeletedConflict)</c> or, with <c>--on-soft-deleted-conflict restore</c>, <c>POST /directory/deletedItems/{id}/restore</c> + <c>PATCH /users/{id}</c> (declared properties, never the password) + <c>GET /users/{id}</c>; otherwise <c>POST /users</c>; then <c>POST /users/{id}/assignLicense</c> when <c>assignedLicenses</c> is declared.</item>
/// <item><b>Update</b>: <c>PATCH /users/{id}</c> (merge body; never <c>passwordProfile</c>, <c>userPrincipalName</c> or <c>id</c>) → <c>GET /users/{id}</c> → <c>POST /users/{id}/assignLicense</c> for the add/remove delta of the direct assignments.</item>
/// <item><b>Delete</b>: <c>DELETE /users/{id}</c> (soft delete, 30-day recycle bin; the UPN stays reserved); a 404 is "already gone".</item>
/// </list>
/// </para>
/// <para>
/// Defaults apply only when EARM creates the user (or restores a soft-deleted holder of the UPN as part of that create):
/// <c>accountEnabled: true</c>, <c>mailNickname</c> derived from the UPN local part (see
/// <see cref="UsersRules.DeriveMailNickname"/>), <c>passwordProfile.forceChangePasswordNextSignIn: true</c>. An existing
/// user found by UPN is never given a default: an undeclared property is left as it is (a template that declares only
/// <c>memberOf</c> does not re-enable a disabled account).
/// <c>passwordProfile</c> is create-only and write-only: it is never diffed (a declared one on an existing user is
/// ignored), never sent in a PATCH and its <c>password</c> is tainted at plan time (<see cref="UsersRules.TaintSecrets"/>),
/// so what-if, logs and diagnostics only ever see <c>***</c>; the plain text exists in the
/// <c>POST /users</c> body alone. When no password is declared, a random 24-character one is generated at create
/// time. In both cases the initial password is published on the live object as a <see cref="SecretValue"/>, so
/// <c>[reference('alice@contoso.com').passwordProfile.password]</c> can feed a <c>securestring</c> output in the
/// creating run (later runs read the user from Graph, which never returns the password: there it is published as <c>null</c> with
/// <see cref="LiveSecrets.InitialPasswordAvailabilityMarker"/> = <c>false</c>, so the output is <c>null</c> with a warning). Convention for that
/// output: <c>&lt;localPart&gt;InitialPassword</c>, for example
/// <c>"aliceInitialPassword": { "type": "securestring", "value": "[reference('alice@contoso.com').passwordProfile.password]" }</c>;
/// it then lands in <c>--secure-outputs-file</c> / <c>--reveal-secure-outputs</c> like any secure output. The state
/// entry only records <c>extra.generatedPassword</c> (<c>true</c> when this run generated it), never the value.
/// </para>
/// <para>
/// <c>userPrincipalName</c> is immutable: a live user whose UPN differs from the name is an unsupported change (the user
/// is read by UPN, so a user renamed outside EARM is simply not found: renaming is a new object). A <c>userType</c> change is allowed but reported as a reason.
/// <c>assignedLicenses</c> (action-backed) is reconciled against the <b>direct</b> assignments only
/// (<see cref="UsersLicensing.DirectAssignments"/>: <c>licenseAssignmentStates[].assignedByGroup == null</c>), so
/// group-inherited licenses are never removed from the user side; a non-empty declaration requires
/// <c>usageLocation</c> (declared or live). <c>manager</c>, <c>memberOf</c>, <c>ownerOf</c>, <c>administrativeUnits</c>,
/// <c>appRoleAssignments</c> and <c>directoryRoles</c> are edge properties the engine keeps out of <c>Properties</c>;
/// the edge pass applies them. Permissions: <c>User.ReadWrite.All</c> plus the overlay's conditional entries for the
/// edge/action properties the template declares (<c>GroupMember.ReadWrite.All</c> for <c>memberOf</c>,
/// <c>AdministrativeUnit.ReadWrite.All</c> for <c>administrativeUnits</c>, <c>LicenseAssignment.ReadWrite.All</c> / <c>LicenseAssignment.Read.All</c> for
/// <c>assignedLicenses</c>, …); <c>ownerOf</c> needs the target type's permission, which that provider reports.
/// </para>
/// </summary>
[EntraProvider(ResourceType)]
public sealed partial class UsersProvider : ProviderBase
{
    public const string ResourceType = "Microsoft.Entra/users";

    public const string ReadWriteAllPermission = "User.ReadWrite.All";

    public const string DeletedItemsPath = "/directory/deletedItems";

    public const string DeletedUsersPath = DeletedItemsPath + "/microsoft.graph.user";

    /// <summary>The state <c>extra</c> key: whether this run generated the initial password.</summary>
    public const string GeneratedPasswordExtra = "generatedPassword";

    /// <summary>
    /// The managed property set every read selects (users return a tiny default set). The two
    /// licensing properties are selected for the direct-assignment diff and stripped from the compared view by the
    /// overlay's <c>stripOnRead</c>.
    /// </summary>
    public static readonly string[] ManagedProperties =
    [
        "id", "userPrincipalName", "displayName", "accountEnabled", "mailNickname", "givenName", "surname", "jobTitle",
        "department", "companyName", "officeLocation", "usageLocation", "userType", "mail", "otherMails", "businessPhones",
        "mobilePhone", "employeeId", "employeeType", "preferredLanguage", "city", "state", "country", "postalCode",
        "streetAddress", "assignedLicenses", "licenseAssignmentStates", "createdDateTime",
    ];

    private static readonly string[] DeletedItemProperties = ["id", "userPrincipalName", "displayName", "deletedDateTime"];

    private static readonly JsonPath UserTypePath = JsonPath.Of("properties", UsersRules.UserType);
    private static readonly JsonPath LicensesPath = JsonPath.Of("properties", UsersLicensing.AssignedLicenses);

    private readonly GroupsLicensing _licensing = new();

    /// <summary>The top-level properties each template declares, selected next to <see cref="ManagedProperties"/> on its reads (G-08).</summary>
    private readonly DeclaredSelects _selects = new();

    private volatile PlanOptions _planOptions = PlanOptions.Default;

    public UsersProvider(ResourceTypeDefinition definition, ResourceTypeCatalog? catalog = null)
        : base(definition, catalog)
    {
        if (!ResourceIdentifier.TypeEquals(definition.Type, ResourceType))
        {
            throw new ArgumentException($"{nameof(UsersProvider)} implements '{ResourceType}', not '{definition.Type}'.", nameof(definition));
        }
    }

    public UsersProvider(ResourceTypeCatalog catalog)
        : this(RequireDefinition(catalog), catalog)
    {
    }

    /// <summary>
    /// The deployment-wide options captured by the last <see cref="Plan"/> call (every call of one run carries the
    /// same values). Settable for tests that call <see cref="CreateAsync"/> directly.
    /// </summary>
    public PlanOptions PlanOptions
    {
        get => _planOptions;
        set => _planOptions = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>An immutable change (userPrincipalName) is a hard error for users.</summary>
    protected override bool RecreateOnImmutableChange => false;

    // ---- permissions ----

    /// <summary>
    /// <c>User.ReadWrite.All</c> plus, for every edge/action property the template declares, the application
    /// permissions of the overlay's <c>permissions.conditional</c> entry. The planner asks before
    /// the properties are evaluated, so the raw declaration is consulted when they are not split yet.
    /// </summary>
    public override IReadOnlyList<string> RequiredPermissions(ResourceInstance? desired)
    {
        var permissions = new List<string>(Definition.Permissions.Application);
        if (permissions.Count == 0)
        {
            permissions.Add(ReadWriteAllPermission);
        }

        if (desired is null)
        {
            return permissions;
        }

        var properties = desired.Properties ?? desired.Declaration.Properties as JsonObject;
        var edges = desired.EdgeProperties ?? desired.Declaration.Properties as JsonObject;
        if (Definition.Permissions.Raw?["conditional"] is not JsonObject conditional)
        {
            return permissions;
        }

        foreach (var entry in conditional)
        {
            if (!Declares(edges, entry.Key) && !Declares(properties, entry.Key))
            {
                continue;
            }

            if (entry.Value is JsonObject spec && spec["application"] is JsonArray required)
            {
                foreach (var item in required)
                {
                    if (JsonValues.TryGetString(item, out var permission) && !permissions.Contains(permission, StringComparer.OrdinalIgnoreCase))
                    {
                        permissions.Add(permission);
                    }
                }
            }
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
        _selects.Register(key, Definition, desired, [UsersRules.PasswordProfile]);
        return key;
    }

    // ---- read ----

    public override async Task<ExistingResource?> ReadAsync(ResourceKey key, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(graph);

        var select = _selects.For(key, ManagedProperties);
        string upn = ProviderSupport.SingleKey(key, UsersRules.UserPrincipalName);
        var live = await GetUserAsync(graph, EntityPath(upn), key.Api, RetryHint.Default, select, cancellationToken).ConfigureAwait(false);

        if (live is null)
        {
            return null;
        }

        await _licensing.EnrichAsync(live, graph, key.Api, cancellationToken).ConfigureAwait(false);
        var existing = ToExisting(live);
        PublishInitialPasswordUnavailable(existing.Raw);
        return existing;
    }

    // ---- delete ----

    public override async Task DeleteAsync(ResourceKey key, ExistingResource live, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(graph);
        var api = key.Api;
        try
        {
            await graph.DeleteAsync(new GraphRequest(EntityPath(live.GraphId), api), cancellationToken).ConfigureAwait(false);
        }
        catch (GraphException ex) when (ex.IsNotFound)
        {
            // Already gone (deleted outside EARM, or purged): nothing to do.
        }
    }

    // ---- helpers ----

    private static ResourceTypeDefinition RequireDefinition(ResourceTypeCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.Find(ResourceType)
            ?? throw new InvalidOperationException($"The catalog does not define '{ResourceType}'.");
    }

    /// <summary><c>/users/{id}</c> or <c>/users/{upn}</c>; a guest's <c>#EXT#</c> becomes <c>%23EXT%23</c>.</summary>
    private string EntityPath(string idOrUpn)
    {
        return CollectionPath + "/" + GraphPath.Segment(idOrUpn);
    }

    private static async Task<JsonObject?> GetUserAsync(IGraphClient graph, string path, GraphApi api, RetryHint hint, string[] select, CancellationToken cancellationToken)
    {
        var request = new GraphRequest(path, api, hint: hint).WithSelect(select);
        return await graph.GetAsync(request, cancellationToken).ConfigureAwait(false) as JsonObject;
    }

    private static JsonObject Extra(bool generated)
    {
        return new JsonObject { [GeneratedPasswordExtra] = generated };
    }

    private static ResourceChange Unsupported(ResourceChange change, string message)
    {
        return change with { UnsupportedReason = message, Reasons = [.. change.Reasons, message] };
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

    private string RequireId(JsonObject live, ResourceInstance desired)
    {
        return JsonObjects.GetId(live)
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{TypeName}: Graph returned an object without 'id' for '{desired.Name}'.", desired.Id);
    }
}
