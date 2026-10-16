using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
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
public sealed class UsersProvider : ProviderBase
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

    // ---- plan ----

    public override ResourceChange Plan(ResourceInstance desired, ExistingResource? existing, PlanOptions options)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(options);
        _planOptions = options;

        if (desired.IsIgnored || desired.Properties is not { } properties)
        {
            return base.Plan(desired, existing, options);
        }

        // A literal password must never be seen in plain text after this point (what-if rows, messages).
        UsersRules.TaintSecrets(properties, Definition.Secure);

        string name = desired.Id.NameLeaf;
        if (!desired.IsSymbolic && UsersRules.ValidateUserPrincipalName(name) is { } shapeError)
        {
            return Unsupported(desired, existing, $"{desired.Describe()}: {shapeError}");
        }

        if (UsersRules.DeclaredNameConflict(properties, name, desired.Describe()) is { } conflict)
        {
            return Unsupported(desired, existing, conflict);
        }

        var reasons = new List<string>();
        if (existing is null)
        {
            UsersRules.ApplyCreatedStateDefaults(properties);
            if (!desired.IsSymbolic)
            {
                UsersRules.ApplyCreateDefaults(properties, name);
            }

            if (!UsersRules.HasPassword(properties))
            {
                reasons.Add($"no passwordProfile.password is declared: a random {UsersRules.GeneratedPasswordLength}-character password is generated on create and published as reference('{name}').passwordProfile.password for a securestring output");
            }

            var create = base.Plan(desired, null, options);
            return WithLicenseDelta(reasons.Count == 0 ? create : create with { Reasons = [.. create.Reasons, .. reasons] }, desired, null);
        }

        string? liveUpn = JsonObjects.GetString(existing.Raw, UsersRules.UserPrincipalName);
        if (liveUpn is not null && !liveUpn.Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            return Unsupported(desired, existing, $"{desired.Describe()}: the live user ({existing.GraphId}) has userPrincipalName '{liveUpn}', which cannot be changed to '{name}' (userPrincipalName is immutable); restore the original name, or delete and recreate the resource.");
        }

        var change = base.Plan(desired, existing, options);
        if (change.HasError)
        {
            return change;
        }

        if (change.Deltas.FirstOrDefault(d => d.HasEffect && d.Path.Equals(UserTypePath)) is { } userType)
        {
            reasons.Add(UsersRules.UserTypeChangeReason(userType.Before, userType.After));
        }

        return WithLicenseDelta(reasons.Count == 0 ? change : change with { Reasons = [.. change.Reasons, .. reasons] }, desired, existing);
    }

    /// <summary>
    /// Adds the <c>properties.assignedLicenses</c> array delta (action-backed, applied after the
    /// object write) against the user's <b>direct</b> assignments; a non-empty declaration without a
    /// <c>usageLocation</c> is unsupported.
    /// </summary>
    private ResourceChange WithLicenseDelta(ResourceChange change, ResourceInstance desired, ExistingResource? existing)
    {
        if (!JsonObjects.TryGetIgnoreCase(desired.EdgeProperties, UsersLicensing.AssignedLicenses, out var declared) || declared is null)
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

        if (desiredLicenses.Count > 0 && !change.HasError && UsersLicensing.MissingUsageLocation(desired.Properties, existing?.Raw))
        {
            return Unsupported(change, $"{desired.Describe()}: assignedLicenses requires usageLocation (ISO 3166-1 alpha-2) on the user; declare properties.usageLocation.");
        }

        var liveDirect = UsersLicensing.DirectAssignments(existing?.Raw);
        var diff = GroupsLicensing.Diff(desiredLicenses, liveDirect, _licensing.Cached);
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
        var properties = desired.Properties
            ?? throw new InvalidOperationException($"{desired.Describe()}: properties must be evaluated before creating.");

        string name = desired.Id.NameLeaf;
        if (UsersRules.ValidateUserPrincipalName(name) is { } shapeError)
        {
            throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: {shapeError}", desired.Id);
        }

        UsersRules.TaintSecrets(properties, Definition.Secure);
        UsersRules.ApplyCreatedStateDefaults(properties);
        UsersRules.ApplyCreateDefaults(properties, name);
        var key = ResolveKey(desired);

        // The only place the password is plain text: the request body (the Graph transport never logs bodies).
        var body = BuildCreateBody(desired);
        UsersRules.RemoveServerAssigned(body);
        if (JsonObjects.TryGetIgnoreCase(body, UsersRules.PasswordProfile, out var profileNode) && profileNode is not JsonObject)
        {
            throw new ProviderException(EngineDiagnosticCodes.UnresolvedDeployTimeValue, $"{desired.Describe()}: passwordProfile depends on values that are not available at this point of the deployment.", desired.Id);
        }

        var profile = profileNode as JsonObject ?? new JsonObject();
        body[JsonObjects.FindKey(body, UsersRules.PasswordProfile) ?? UsersRules.PasswordProfile] = profile;
        bool generated = !UsersRules.HasPassword(body);
        if (generated)
        {
            string generatedPassword = UsersRules.GeneratePassword();

            // Masked from every message from this moment on (an error text that echoes the request body must not carry it), before anything is sent.
            SecretScrubber.Register(generatedPassword, secureDeclared: true);
            profile[JsonObjects.FindKey(profile, UsersRules.Password) ?? UsersRules.Password] = generatedPassword;
        }

        // The key lookup was a 404, so the only other holder of the UPN is a soft-deleted user (30-day window).
        var warnings = new List<ProviderWarning>();
        if (await FindSoftDeletedAsync(name, desired.Api, graph, warnings, cancellationToken).ConfigureAwait(false) is { } deleted)
        {
            return await RestoreOrFailAsync(desired, key, body, deleted, graph, warnings, cancellationToken).ConfigureAwait(false);
        }

        var response = await graph.PostAsync(new GraphRequest(CollectionPath, desired.Api, hint: desired.RetryHint), body, cancellationToken).ConfigureAwait(false);
        var live = response.Body as JsonObject
            ?? await GetUserAsync(graph, EntityPath(name), desired.Api, RetryHint.ExpectReplicationLag, _selects.For(key, ManagedProperties), cancellationToken).ConfigureAwait(false)
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: the create returned no object and '{name}' could not be read back.", desired.Id);
        string id = RequireId(live, desired);

        // Publish from the tainted desired properties, never from the revealed body: a secret that flowed into
        // any other property must stay a SecretValue on the object reference() hands out.
        UsersRules.MergeWritten(live, properties);
        JsonObjects.TryGetIgnoreCase(properties, UsersRules.PasswordProfile, out var declaredProfile);
        PublishInitialPassword(live, declaredProfile as JsonObject, JsonObjects.GetString(profile, UsersRules.Password));

        // The object was created a moment ago: the license action by id may hit replication lag. A failure here leaves a user that exists (with a generated password that Graph
        // never returns again): the partial result goes with the exception so the executor records the object and the one-time secret instead of losing both.
        // A cancellation (Ctrl+C) that reaches the license step is such a failure too: the user and its password are recorded, then the run stops.
        var created = new AppliedResource(desired.Id, id, live, key, Extra(generated)) { Warnings = warnings };
        try
        {
            await ApplyLicensesAsync(desired, id, null, RetryHint.ExpectReplicationLag, graph, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is GraphException or GraphTransportException or ProviderException or OperationCanceledException)
        {
            throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: the user was created (id {id}) but assigning its licenses failed: {ex.Message}", desired.Id, ex) { Partial = created };
        }

        return created;
    }

    /// <summary>
    /// The soft-deleted user holding <paramref name="userPrincipalName"/>, or <c>null</c> (see <see cref="DeletedItemsProbe"/>:
    /// filtered advanced query, a 400 falls back to listing, a 403 skips the probe with a warning). Listing deleted users needs
    /// <c>User.Read.All</c> (Learn: directory-deleteditems-list), which <c>User.ReadWrite.All</c> covers.
    /// </summary>
    private static Task<JsonObject?> FindSoftDeletedAsync(string userPrincipalName, GraphApi api, IGraphClient graph, List<ProviderWarning> warnings, CancellationToken cancellationToken)
    {
        return DeletedItemsProbe.FindAsync(graph, DeletedUsersPath, "user", UsersRules.UserPrincipalName, userPrincipalName, DeletedItemProperties, api, advanced: true, warnings, cancellationToken);
    }
    /// <summary>
    /// Restores the soft-deleted holder of the UPN and brings it to the declared state: the declared properties are
    /// PATCHed (never <c>passwordProfile</c>: a password reset is a separate permission, so the restored user keeps
    /// its old password and <c>extra.generatedPassword</c> is <c>false</c>), then the user is re-read and its direct
    /// licenses reconciled.
    /// </summary>
    private async Task<AppliedResource> RestoreOrFailAsync(ResourceInstance desired, ResourceKey key, JsonObject body, JsonObject deleted, IGraphClient graph, List<ProviderWarning> warnings, CancellationToken cancellationToken)
    {
        string deletedId = JsonObjects.GetId(deleted) ?? "?";
        if (_planOptions.OnSoftDeletedConflict != SoftDeletedConflict.Restore)
        {
            string when = JsonObjects.GetString(deleted, "deletedDateTime") is { } stamp ? $", deleted {stamp}" : string.Empty;
            throw new ProviderException(
                ProviderException.SoftDeletedConflict,
                $"{desired.Describe()}: userPrincipalName '{desired.Id.NameLeaf}' is held by a soft-deleted user (id {deletedId}{when}), which keeps the name for 30 days. Rerun with --on-soft-deleted-conflict restore to restore and update it, or purge it first (DELETE /directory/deletedItems/{deletedId}).",
                desired.Id);
        }

        var restore = new GraphRequest(DeletedItemsPath + "/" + GraphPath.Segment(deletedId) + "/restore", desired.Api, hint: desired.RetryHint);
        var restored = await graph.PostAsync(restore, null, cancellationToken).ConfigureAwait(false);
        string id = JsonObjects.GetId(restored.Body as JsonObject) ?? deletedId;
        string path = EntityPath(id);

        var patch = (JsonObject)JsonValues.Clone(body)!;
        UsersRules.RemoveNeverPatched(patch);
        if (patch.Count > 0)
        {
            await graph.PatchAsync(new GraphRequest(path, desired.Api, hint: RetryHint.ExpectReplicationLag), patch, cancellationToken).ConfigureAwait(false);
        }

        var live = await GetUserAsync(graph, path, desired.Api, RetryHint.ExpectReplicationLag, _selects.For(key, ManagedProperties), cancellationToken).ConfigureAwait(false)
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: the user ({id}) vanished after being restored.", desired.Id);
        await _licensing.EnrichAsync(live, graph, desired.Api, cancellationToken).ConfigureAwait(false);
        PublishInitialPasswordUnavailable(live);
        var restoredResult = new AppliedResource(desired.Id, id, live, key, Extra(generated: false)) { Warnings = warnings };
        try
        {
            await ApplyLicensesAsync(desired, id, live, RetryHint.ExpectReplicationLag, graph, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is GraphException or GraphTransportException or ProviderException)
        {
            throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: the user was restored (id {id}) but assigning its licenses failed: {ex.Message}", desired.Id, ex) { Partial = restoredResult };
        }

        return restoredResult;
    }

    // ---- update ----

    public override async Task<AppliedResource> UpdateAsync(ResourceInstance desired, ExistingResource existing, ResourceChange change, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(graph);
        var properties = desired.Properties
            ?? throw new InvalidOperationException($"{desired.Describe()}: properties must be evaluated before updating.");

        // No defaults here: an existing user's undeclared properties are never written (see Plan).
        UsersRules.TaintSecrets(properties, Definition.Secure);
        var key = ResolveKey(desired);
        string path = EntityPath(existing.GraphId);

        // The license delta is action-backed; it must not reach the PATCH body.
        var objectChange = change with { Deltas = change.Deltas.Where(d => !d.Path.Equals(LicensesPath)).ToList() };
        var body = BuildPatchBody(desired, existing, objectChange);
        UsersRules.RemoveNeverPatched(body);
        if (body.Count > 0)
        {
            await graph.PatchAsync(new GraphRequest(path, desired.Api, hint: desired.RetryHint), body, cancellationToken).ConfigureAwait(false);
        }

        var live = await GetUserAsync(graph, path, desired.Api, desired.RetryHint, _selects.For(key, ManagedProperties), cancellationToken).ConfigureAwait(false)
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: the user ({existing.GraphId}) vanished after the update.", desired.Id);
        await _licensing.EnrichAsync(live, graph, desired.Api, cancellationToken).ConfigureAwait(false);

        await ApplyLicensesAsync(desired, existing.GraphId, existing.Raw, desired.RetryHint, graph, cancellationToken).ConfigureAwait(false);
        PublishInitialPasswordUnavailable(live);
        return new AppliedResource(desired.Id, existing.GraphId, live, key, Extra(generated: false));
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

    /// <summary>Reconciles the declared direct licenses against <paramref name="liveRaw"/>'s direct assignments with <c>POST /users/{id}/assignLicense</c>.</summary>
    private async Task ApplyLicensesAsync(ResourceInstance desired, string graphId, JsonObject? liveRaw, RetryHint hint, IGraphClient graph, CancellationToken cancellationToken)
    {
        if (!JsonObjects.TryGetIgnoreCase(desired.EdgeProperties, UsersLicensing.AssignedLicenses, out var declared) || declared is null)
        {
            return;
        }

        if (declared is not JsonArray desiredLicenses)
        {
            throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: properties.assignedLicenses must be an array of {{ skuId | skuPartNumber, disabledPlans }} objects.", desired.Id);
        }

        if (desiredLicenses.Count > 0 && UsersLicensing.MissingUsageLocation(desired.Properties, liveRaw))
        {
            throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: assignedLicenses requires usageLocation on the user.", desired.Id);
        }

        var liveDirect = UsersLicensing.DirectAssignments(liveRaw);
        await _licensing.ApplyAsync(graph, desired.Api, EntityPath(graphId), desiredLicenses, liveDirect, hint, desired.Describe(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Publishes the initial password on the live object as a tainted leaf (never plain text) for <c>reference()</c>:
    /// the declared profile's other members keep whatever taint they carry; the password (declared or generated) is
    /// always a fresh <see cref="SecretValue"/>.
    /// </summary>
    private static void PublishInitialPassword(JsonObject live, JsonObject? declaredProfile, string? password)
    {
        var published = declaredProfile is null ? new JsonObject() : (JsonObject)JsonValues.Clone(declaredProfile)!;
        string key = JsonObjects.FindKey(published, UsersRules.Password) ?? UsersRules.Password;
        if (string.IsNullOrEmpty(password))
        {
            published.Remove(key);
        }
        else
        {
            published[key] = SecretJson.CreateString(password);
        }

        live[JsonObjects.FindKey(live, UsersRules.PasswordProfile) ?? UsersRules.PasswordProfile] = published;
        live[LiveSecrets.InitialPasswordAvailabilityMarker] = true;
    }

    /// <summary>
    /// On every run that does not create the user (Graph never returns a password): <c>passwordProfile.password</c> is published as
    /// <c>null</c> with <see cref="LiveSecrets.InitialPasswordAvailabilityMarker"/> = <c>false</c>, so an output such as
    /// <c>[reference('alice@contoso.com').passwordProfile.password]</c> evaluates to <c>null</c> with the
    /// <see cref="EngineDiagnosticCodes.SecretNotAvailableOnRerun"/> warning instead of failing the run (the <see cref="LiveSecrets"/> contract).
    /// </summary>
    private static void PublishInitialPasswordUnavailable(JsonObject live)
    {
        string profileKey = JsonObjects.FindKey(live, UsersRules.PasswordProfile) ?? UsersRules.PasswordProfile;
        var profile = live[profileKey] as JsonObject ?? new JsonObject();
        profile[JsonObjects.FindKey(profile, UsersRules.Password) ?? UsersRules.Password] = null;
        live[profileKey] = profile;
        live[LiveSecrets.InitialPasswordAvailabilityMarker] = false;
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
