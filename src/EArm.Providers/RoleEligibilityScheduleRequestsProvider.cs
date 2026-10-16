using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core;
using EArm.Core.Secrets;
using EArm.Core.Types;
using EArm.Core.Validation;
using EArm.Engine;
using EArm.Engine.Providers;
using EArm.Expressions.Values;
using EArm.Graph;

namespace EArm.Providers;

/// <summary>
/// <c>Microsoft.Entra/roleEligibilityScheduleRequests</c>: PIM eligibility for directory roles, shaped like ARM's
/// <c>Microsoft.Authorization/roleEligibilityScheduleRequests</c>. The resource name is a deterministic GUID (a label); the eligibility is found by the
/// tuple (<c>principalId</c>, <c>roleDefinitionId</c>, <c>directoryScopeId</c> or <c>appScopeId</c>) and read live on every run. Sugar:
/// <c>role</c> → <c>roleDefinitionId</c> and <c>administrativeUnit</c> → <c>directoryScopeId</c> (as for <c>roleAssignments</c>); the scope defaults to
/// <c>/</c> and an absent <c>scheduleInfo</c> to a permanent eligibility. The pure rules are <see cref="RoleEligibilityScheduleRules"/>.
/// <para>
/// Requests per operation:
/// <list type="bullet">
/// <item><b>Prepare</b>: the role / unit name lookups of <see cref="RoleAssignmentsLookup"/>, then <c>GET /directoryObjects/{principalId}</c> and, for a
/// group, <c>GET /groups/{id}?$select=id,isAssignableToRole</c>: a group that is not role-assignable cannot be made eligible (<see cref="InvalidEligibility"/>).</item>
/// <item><b>Read</b>: <c>GET /roleManagement/directory/roleEligibilitySchedules?$filter=principalId eq … and roleDefinitionId eq … and directoryScopeId eq …</c>,
/// keeping <c>memberType</c> Direct (<see cref="RoleEligibilitySchedulesLookup"/>). Schedules include future-dated eligibilities.</item>
/// <item><b>Create</b>: <c>POST …/roleEligibilityScheduleRequests</c> with action <c>adminAssign</c>, then a read-back by tuple (the request is processed
/// asynchronously: until the schedule is listed, the request's <c>targetScheduleId</c> stands in, and the engine waits for the key read,
/// <see cref="KeyReadLagsCreate"/>). Graph's <c>RoleAssignmentExists</c> is adopted after a re-read under replication lag.</item>
/// <item><b>Update</b>: <c>POST</c> with action <c>adminUpdate</c> (in place, never a recreate).</item>
/// <item><b>Delete</b>: <c>POST</c> with action <c>adminRemove</c>; an eligibility that is already gone is not an error.</item>
/// </list>
/// A request refused by a rule of the role's PIM policy (ExpirationRule, JustificationRule...) fails with <see cref="PimPolicyViolation"/> and Graph's text.
/// </para>
/// <para>
/// Comparison: the expiration type and, for AfterDateTime, endDateTime within 1 s; a start in the past is never drift; AfterDuration is applied at creation
/// and never compared (Graph may report the schedule as AfterDateTime). <c>justification</c> and <c>ticketInfo</c> are request metadata, sent on every request
/// and never compared. A changed tuple is a new eligibility: the old one is reported as retained (orphan scope: the declared principals).
/// </para>
/// </summary>
[EntraProvider(ResourceType)]
public sealed class RoleEligibilityScheduleRequestsProvider : ProviderBase, IOrphanCandidateSource
{
    public const string ResourceType = RoleEligibilityScheduleRules.ResourceType;

    /// <summary><see cref="ProviderException"/> code: the declared eligibility cannot be requested (unknown role or unit, wrong principal kind, bad shape).</summary>
    public const string InvalidEligibility = EngineDiagnosticCodes.RoleEligibilityInvalid;

    /// <summary><see cref="ProviderException"/> code: Graph refused the request under the role's PIM policy.</summary>
    public const string PimPolicyViolation = EngineDiagnosticCodes.PimPolicyViolation;

    /// <summary>The tolerance of the endDateTime / startDateTime comparison (Graph stores the times with its own precision).</summary>
    public static readonly TimeSpan TimeTolerance = TimeSpan.FromSeconds(1);

    public RoleEligibilityScheduleRequestsProvider(ResourceTypeDefinition definition, ResourceTypeCatalog? catalog = null)
        : base(definition, catalog)
    {
        if (!ResourceIdentifier.TypeEquals(definition.Type, ResourceType))
        {
            throw new ArgumentException($"{nameof(RoleEligibilityScheduleRequestsProvider)} implements '{ResourceType}', not '{definition.Type}'.", nameof(definition));
        }
    }

    public RoleEligibilityScheduleRequestsProvider(ResourceTypeCatalog catalog)
        : this(RequireDefinition(catalog), catalog)
    {
    }

    /// <summary>The clock "a start in the past" is judged with; tests pin it.</summary>
    public TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>A change is an adminUpdate request, never a recreate.</summary>
    protected override bool RecreateOnImmutableChange => false;

    /// <summary>PIM processes requests asynchronously: the schedule list can miss a new eligibility for a while.</summary>
    public override bool KeyReadLagsCreate => true;

    // ---- prepare (sugar + live validation) ----

    public override async Task PrepareAsync(ResourceInstance desired, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(graph);
        var properties = desired.Properties
            ?? throw new InvalidOperationException($"{desired.Describe()}: properties must be evaluated before preparing.");

        if (RoleEligibilityScheduleRules.Check(properties, IsOpaque) is { Count: > 0 } findings)
        {
            throw Invalid(desired, string.Join("; ", findings.Select(f => f.Message)));
        }

        // Role: role (template id | built-in name | custom name | custom id) → roleDefinitionId.
        if (Text(properties, "role") is { } role)
        {
            var resolved = await RoleAssignmentsLookup.ResolveRoleAsync(graph, role, desired.Api, desired.RetryHint, cancellationToken).ConfigureAwait(false);
            Set(properties, "roleDefinitionId", JsonValue.Create(resolved.Id ?? throw Invalid(desired, resolved.Error!)));
            Remove(properties, "role");
        }
        else if (Unknown(properties, "role") is { } unknownRole)
        {
            Set(properties, "roleDefinitionId", unknownRole);
            Remove(properties, "role");
        }
        else if (Text(properties, "roleDefinitionId") is { } roleDefinitionId)
        {
            Set(properties, "roleDefinitionId", JsonValue.Create(RoleAssignmentsRules.NormalizeId(roleDefinitionId)));
        }

        // Scope: administrativeUnit (id | name) → directoryScopeId; neither a directory nor an app scope → "/".
        if (Text(properties, "administrativeUnit") is { } unit)
        {
            var resolved = await RoleAssignmentsLookup.ResolveAdministrativeUnitScopeAsync(graph, unit, desired.Api, desired.RetryHint, cancellationToken).ConfigureAwait(false);
            Set(properties, "directoryScopeId", JsonValue.Create(resolved.Id ?? throw Invalid(desired, resolved.Error!)));
            Remove(properties, "administrativeUnit");
        }
        else if (Unknown(properties, "administrativeUnit") is { } unknownUnit)
        {
            Set(properties, "directoryScopeId", unknownUnit);
            Remove(properties, "administrativeUnit");
        }
        else if (Text(properties, "directoryScopeId") is { } scope)
        {
            Set(properties, "directoryScopeId", JsonValue.Create(RoleAssignmentsRules.NormalizeScope(scope)));
        }
        else if (!Declares(properties, "directoryScopeId") && !Declares(properties, "appScopeId"))
        {
            Set(properties, "directoryScopeId", JsonValue.Create(RoleAssignmentsRules.RootScope));
        }

        // Principal: must exist and be a user, a role-assignable group or a service principal.
        if (Text(properties, "principalId") is { } principalId)
        {
            string? collection = await RoleAssignmentsLookup.PrincipalCollectionAsync(graph, principalId, desired.Api, desired.RetryHint, cancellationToken).ConfigureAwait(false);
            bool? roleAssignable = collection == "groups"
                ? await RoleAssignmentsLookup.IsRoleAssignableGroupAsync(graph, principalId, desired.Api, desired.RetryHint, cancellationToken).ConfigureAwait(false)
                : null;
            if (collection == "groups" && roleAssignable != true)
            {
                throw Invalid(desired, $"group {principalId} is not role-assignable; PIM can only make a group eligible for a directory role when it was created with isAssignableToRole: true (the property is immutable, so recreate the group).");
            }

            if (RoleAssignmentsLookup.CheckPrincipalKind(collection, roleAssignable, principalId) is { } reason)
            {
                throw Invalid(desired, reason);
            }

            Set(properties, "principalId", JsonValue.Create(RoleAssignmentsRules.NormalizeId(principalId)));
        }

        Normalize(properties);
    }

    // ---- plan ----

    public override ResourceChange Plan(ResourceInstance desired, ExistingResource? existing, PlanOptions options)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(options);

        if (desired.IsIgnored || desired.Properties is not { } properties)
        {
            return base.Plan(desired, existing, options);
        }

        if (!desired.IsSymbolic && !RoleAssignmentsRules.IsGuid(desired.Id.NameLeaf))
        {
            return Unsupported(desired, existing, $"{desired.Describe()}: the name '{desired.Id.NameLeaf}' is not a GUID; name role eligibilities with a deterministic GUID, for example [guid(variables('groupName'), 'Global Reader', 'eligible')].");
        }

        if (RoleEligibilityScheduleRules.Check(properties, IsOpaque) is { Count: > 0 } findings)
        {
            return Unsupported(desired, existing, $"{desired.Describe()}: {string.Join("; ", findings.Select(f => f.Message))}.");
        }

        Normalize(properties);
        if (existing is null)
        {
            // Not yet prepared (a symbolic name is planned before PrepareAsync runs): sugar stands in for the canonical property for this plan only,
            // so a later PrepareAsync still sees the declaration as written.
            var standIns = new List<string>();
            StandIn(properties, "role", "roleDefinitionId", standIns);
            StandIn(properties, "administrativeUnit", "directoryScopeId", standIns);
            try
            {
                return base.Plan(desired, null, options);
            }
            finally
            {
                standIns.ForEach(name => Remove(properties, name));
            }
        }

        var aligned = new ExistingResource(existing.GraphId, RoleEligibilityScheduleComparison.Align(properties, existing.Properties, Clock.GetUtcNow(), TimeTolerance, IsOpaque), existing.Raw);
        var change = base.Plan(desired, aligned, options);
        return change is { Type: ChangeType.Modify, HasError: false }
            ? change with { Reasons = [.. change.Reasons, "the eligibility is changed in place with a PIM adminUpdate request"] }
            : change;
    }

    // ---- read ----

    public override async Task<ExistingResource?> ReadAsync(ResourceKey key, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(graph);
        var found = await RoleEligibilitySchedulesLookup.FindAsync(graph, RequireTuple(key, null), key.Api, RetryHint.Default, cancellationToken).ConfigureAwait(false);
        return found is null ? null : Existing(found);
    }

    // ---- create ----

    public override async Task<AppliedResource> CreateAsync(ResourceInstance desired, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(graph);
        var properties = desired.Properties
            ?? throw new InvalidOperationException($"{desired.Describe()}: properties must be evaluated before creating.");

        var key = ResolveKey(desired);
        var tuple = RequireTuple(key, desired.Id);
        JsonObject live;
        try
        {
            var response = await SendAsync(desired, graph, RoleEligibilitySchedulesLookup.AdminAssign, tuple, properties, cancellationToken).ConfigureAwait(false);
            live = await RoleEligibilitySchedulesLookup.FindAsync(graph, tuple, desired.Api, RetryHint.ExpectReplicationLag, cancellationToken).ConfigureAwait(false)
                ?? StandInFor(response, desired);
        }
        catch (GraphException ex) when (ex.IsAlreadyExists || string.Equals(ex.Code, "RoleAssignmentExists", StringComparison.OrdinalIgnoreCase))
        {
            // RoleAssignmentExists: made by another run, or not yet listed when this create was planned. Adopt it; when it cannot be read back yet,
            // the engine waits out replication on the same answer.
            var found = await RoleEligibilitySchedulesLookup.FindAsync(graph, tuple, desired.Api, RetryHint.ExpectReplicationLag, cancellationToken).ConfigureAwait(false);
            if (found is null)
            {
                // Not listed yet: the executor adopts on "already exists" after waiting out replication (ReplicationWait).
                throw;
            }

            live = found;
        }

        return new AppliedResource(desired.Id, RequireId(live, desired), live, key, Extra(tuple));
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

        var key = ResolveKey(desired);
        var tuple = RequireTuple(key, desired.Id);
        var response = await SendAsync(desired, graph, RoleEligibilitySchedulesLookup.AdminUpdate, tuple, properties, cancellationToken).ConfigureAwait(false);
        var live = await RoleEligibilitySchedulesLookup.FindAsync(graph, tuple, desired.Api, RetryHint.ExpectReplicationLag, cancellationToken).ConfigureAwait(false);
        string graphId = (live is null ? null : JsonObjects.GetId(live)) ?? existing.GraphId;
        return new AppliedResource(desired.Id, graphId, live ?? response ?? (JsonObject)JsonValues.Clone(existing.Raw)!, key, Extra(tuple));
    }

    // ---- delete ----

    public override async Task DeleteAsync(ResourceKey key, ExistingResource live, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(graph);
        var tuple = RequireTuple(key, null);
        var body = RoleEligibilitySchedulesLookup.RequestBody(RoleEligibilitySchedulesLookup.AdminRemove, tuple, null, JsonValue.Create(RoleEligibilityScheduleRules.DefaultJustification), null);
        try
        {
            await graph.PostAsync(new GraphRequest(RoleEligibilitySchedulesLookup.RequestsPath, key.Api), body, cancellationToken).ConfigureAwait(false);
        }
        catch (GraphException ex) when (RoleEligibilitySchedulesLookup.IsAlreadyGone(ex))
        {
            // Already removed (or expired) outside EARM.
        }
        catch (GraphException ex) when (RoleEligibilitySchedulesLookup.IsPolicyViolation(ex))
        {
            throw new ProviderException(PimPolicyViolation, $"{TypeName} {key.Describe()}: the adminRemove request was refused by the role's PIM policy: {ex.GraphMessage}");
        }
    }

    // ---- orphan report ----

    /// <summary>The orphan report lists the Direct eligibilities of the principals the declared resources name (reported as retained).</summary>
    public OrphanScope OrphanScope => OrphanScope.DeclaredPrincipals;

    public async Task<IReadOnlyList<OrphanCandidate>> ListOrphanCandidatesAsync(OrphanQuery query, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(graph);
        var candidates = new List<OrphanCandidate>();
        foreach (var principalId in OrphanListing.DistinctKeyValues(query.DeclaredKeys, "principalId"))
        {
            foreach (var schedule in await RoleEligibilitySchedulesLookup.ReadPrincipalSchedulesAsync(graph, principalId, query.Api, RetryHint.Default, cancellationToken).ConfigureAwait(false))
            {
                if (JsonObjects.GetId(schedule) is { } id)
                {
                    string scopeProperty = JsonObjects.GetString(schedule, "directoryScopeId") is not null ? "directoryScopeId" : "appScopeId";
                    candidates.Add(new OrphanCandidate(id, id, null, "eligible: " + OrphanListing.Describe(schedule, "principalId", "roleDefinitionId", scopeProperty)));
                }
            }
        }

        return candidates;
    }

    // ---- helpers ----

    /// <summary>Sends one schedule request; a PIM policy refusal becomes <see cref="PimPolicyViolation"/>, a failed request status an apply failure.</summary>
    private async Task<JsonObject?> SendAsync(ResourceInstance desired, IGraphClient graph, string action, EligibilityTuple tuple, JsonObject properties, CancellationToken cancellationToken)
    {
        var body = RoleEligibilitySchedulesLookup.RequestBody(action, tuple, Get(properties, "scheduleInfo"), Get(properties, "justification") ?? JsonValue.Create(RoleEligibilityScheduleRules.DefaultJustification), Get(properties, "ticketInfo"));
        GraphResponse response;
        try
        {
            response = await graph.PostAsync(new GraphRequest(RoleEligibilitySchedulesLookup.RequestsPath, desired.Api, hint: desired.RetryHint), (JsonObject)SecretJson.Reveal(body)!, cancellationToken).ConfigureAwait(false);
        }
        catch (GraphException ex) when (RoleEligibilitySchedulesLookup.IsPolicyViolation(ex))
        {
            throw new ProviderException(PimPolicyViolation, $"{desired.Describe()}: the {action} request was refused by the role's PIM policy: {ex.GraphMessage}. Change scheduleInfo (for example a policy that forbids permanent eligibility needs an AfterDateTime or AfterDuration expiration), justification or ticketInfo to satisfy the policy, or change the policy.", desired.Id);
        }

        var request = response.Body as JsonObject;
        if (RoleEligibilitySchedulesLookup.HasFailedStatus(request, out var status))
        {
            throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: the {action} request ended with status {status}.", desired.Id);
        }

        return request;
    }

    /// <summary>The live view: the tuple (scope canonicalised) and the schedule, without Graph's bookkeeping.</summary>
    private ExistingResource Existing(JsonObject live)
    {
        string id = JsonObjects.GetId(live) ?? throw new ProviderException(EngineDiagnosticCodes.ReadFailed, $"The live {TypeName} schedule has no 'id'.");
        return new ExistingResource(id, RoleEligibilityScheduleComparison.LiveView(live), (JsonObject)JsonValues.Clone(live)!);
    }

    /// <summary>The request response standing in for a schedule that is not listed yet (its <c>targetScheduleId</c> is the schedule's id).</summary>
    private static JsonObject StandInFor(JsonObject? response, ResourceInstance desired)
    {
        if (response is null || (JsonObjects.GetString(response, "targetScheduleId") ?? JsonObjects.GetId(response)) is not { } scheduleId)
        {
            throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: the adminAssign request returned no request object and the eligibility could not be read back.", desired.Id);
        }

        var standIn = (JsonObject)JsonValues.Clone(response)!;
        standIn["id"] = scheduleId;
        return standIn;
    }

    private EligibilityTuple RequireTuple(ResourceKey key, ResourceIdentifier? id)
    {
        if (!key.TryGet("principalId", out var principalId) || !key.TryGet("roleDefinitionId", out var roleDefinitionId))
        {
            throw new ProviderException(EngineDiagnosticCodes.ResourceKeyUnresolved, $"{TypeName}: the key {key.Describe()} does not carry principalId and roleDefinitionId.", id);
        }

        return key.TryGet("appScopeId", out var appScope) && !key.TryGet("directoryScopeId", out _)
            ? new EligibilityTuple(principalId, roleDefinitionId, appScope, IsAppScope: true)
            : new EligibilityTuple(principalId, roleDefinitionId, key.TryGet("directoryScopeId", out var scope) ? scope : RoleAssignmentsRules.RootScope, IsAppScope: false);
    }

    private static JsonObject Extra(EligibilityTuple tuple)
    {
        return new JsonObject
        {
            ["principalId"] = RoleAssignmentsRules.NormalizeId(tuple.PrincipalId),
            ["roleDefinitionId"] = RoleAssignmentsRules.NormalizeId(tuple.RoleDefinitionId),
            [tuple.ScopeProperty] = RoleEligibilitySchedulesLookup.CanonicalScope(tuple),
        };
    }

    /// <summary>The engine-side normalisation (shared rules) plus the defaults: a permanent schedule and the neutral justification.</summary>
    private static void Normalize(JsonObject properties)
    {
        RoleEligibilityScheduleRules.Normalize(properties, IsOpaque);
        if (!Declares(properties, "scheduleInfo"))
        {
            Set(properties, "scheduleInfo", new JsonObject { ["expiration"] = new JsonObject { ["type"] = RoleEligibilityScheduleRules.NoExpiration } });
        }

        if (!Declares(properties, "justification"))
        {
            Set(properties, "justification", JsonValue.Create(RoleEligibilityScheduleRules.DefaultJustification));
        }
    }

    /// <summary>A deploy-time-unknown or secret value cannot be judged at plan time.</summary>
    private static bool IsOpaque(JsonNode? node)
    {
        return UnknownJson.IsUnknown(node) || SecretJson.IsSecret(node, out _);
    }

    private static bool Declares(JsonObject properties, string name)
    {
        return JsonObjects.TryGetIgnoreCase(properties, name, out var value) && !JsonObjects.IsAbsent(value);
    }

    private static JsonNode? Get(JsonObject properties, string name)
    {
        return JsonObjects.TryGetIgnoreCase(properties, name, out var value) && !JsonObjects.IsAbsent(value) ? value : null;
    }

    /// <summary>The trimmed text of a declared, known string property, or <c>null</c>.</summary>
    private static string? Text(JsonObject properties, string name)
    {
        return Get(properties, name) is { } node && !IsOpaque(node) && JsonValues.TryGetString(node, out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;
    }

    /// <summary>A copy of a deploy-time-unknown property value (what-if: a role or unit deployed in this template), or <c>null</c>.</summary>
    private static JsonNode? Unknown(JsonObject properties, string name)
    {
        return Get(properties, name) is { } node && UnknownJson.IsUnknown(node) ? JsonValues.Clone(node) : null;
    }

    private static void Set(JsonObject properties, string name, JsonNode? value)
    {
        string key = JsonObjects.FindKey(properties, name) ?? name;
        properties[key] = value;
    }

    private static void Remove(JsonObject properties, string name)
    {
        if (JsonObjects.FindKey(properties, name) is { } key)
        {
            properties.Remove(key);
        }
    }

    /// <summary>When only the sugar property is declared, a deploy-time placeholder stands in for the canonical one.</summary>
    private static void StandIn(JsonObject properties, string sugar, string canonical, List<string> added)
    {
        if (!Declares(properties, canonical) && Get(properties, sugar) is { } value)
        {
            properties[canonical] = UnknownJson.IsUnknown(value, out var unknown) ? UnknownJson.Create(unknown) : UnknownJson.Create(sugar, UnknownOrigins.Reference);
            added.Add(canonical);
        }
    }

    private ProviderException Invalid(ResourceInstance desired, string message)
    {
        return new ProviderException(InvalidEligibility, $"{desired.Describe()}: {message}", desired.Id);
    }

    private string RequireId(JsonObject live, ResourceInstance desired)
    {
        return JsonObjects.GetId(live)
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{TypeName}: Graph returned a schedule without 'id' for '{desired.Name}'.", desired.Id);
    }

    private static ResourceTypeDefinition RequireDefinition(ResourceTypeCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.Find(ResourceType)
            ?? throw new InvalidOperationException($"The catalog does not define '{ResourceType}'.");
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

}
