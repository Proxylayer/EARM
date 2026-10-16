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
using GraphApi = EArm.Graph.GraphApi;

namespace EArm.Providers;

/// <summary>
/// The lifecycle every PIM schedule request type shares (<see cref="RoleEligibilityScheduleRequestsProvider"/>,
/// <see cref="GroupEligibilityScheduleRequestsProvider"/>). The resource name is a deterministic GUID (a label); the schedule is found by the type's
/// identity tuple and read live on every run.
/// <list type="bullet">
/// <item><b>Plan</b>: the type's rules (an unsupported row on a finding), then the live schedule aligned with <see cref="PimScheduleComparison"/>; a
/// change is a <c>Patch</c> (adminUpdate), never a recreate. Before Prepare (a symbolic name), sugar stands in for its canonical property.</item>
/// <item><b>Create</b>: <c>POST {requests}</c> with action <c>adminAssign</c>, then a read-back by tuple (until the schedule is listed, the request's
/// <c>targetScheduleId</c> stands in; the engine waits for the key read, <see cref="KeyReadLagsCreate"/>). <c>RoleAssignmentExists</c> is adopted
/// after a re-read under replication lag.</item>
/// <item><b>Update</b>: <c>POST</c> with action <c>adminUpdate</c>. <b>Delete</b>: <c>POST</c> with action <c>adminRemove</c>; a schedule that is
/// already gone is not an error.</item>
/// </list>
/// A request refused by a rule of the PIM policy fails with <see cref="PimPolicyViolation"/> and Graph's text plus the type's advice. Defaults applied
/// before the diff: an absent <c>scheduleInfo</c> is a permanent eligibility, an absent <c>justification</c> is <see cref="PimScheduleRules.DefaultJustification"/>.
/// </summary>
/// <typeparam name="TTuple">The type's identity tuple.</typeparam>
public abstract class PimScheduleRequestsProvider<TTuple> : ProviderBase, IOrphanCandidateSource
    where TTuple : class
{
    /// <summary><see cref="ProviderException"/> code: Graph refused the request under the PIM policy.</summary>
    public const string PimPolicyViolation = EngineDiagnosticCodes.PimPolicyViolation;

    /// <summary>The tolerance of the endDateTime / startDateTime comparison (Graph stores the times with its own precision).</summary>
    public static readonly TimeSpan TimeTolerance = TimeSpan.FromSeconds(1);

    protected PimScheduleRequestsProvider(ResourceTypeDefinition definition, ResourceTypeCatalog? catalog, string resourceType)
        : base(definition, catalog)
    {
        if (!ResourceIdentifier.TypeEquals(definition.Type, resourceType))
        {
            throw new ArgumentException($"{GetType().Name} implements '{resourceType}', not '{definition.Type}'.", nameof(definition));
        }
    }

    /// <summary>The clock "a start in the past" is judged with; tests pin it.</summary>
    public TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>A change is an adminUpdate request, never a recreate.</summary>
    protected override bool RecreateOnImmutableChange => false;

    /// <summary>PIM processes requests asynchronously: the schedule list can miss a new eligibility for a while.</summary>
    public override bool KeyReadLagsCreate => true;

    // ---- the type's specifics ----

    /// <summary>The Graph collection every write goes through.</summary>
    protected abstract string RequestsPath { get; }

    /// <summary>The <see cref="ProviderException"/> code of a declaration Prepare refuses.</summary>
    protected abstract string InvalidCode { get; }

    /// <summary>Whose policy refused a request, for messages ("the role's PIM policy").</summary>
    protected abstract string PolicyOwner { get; }

    /// <summary>What to do about a policy refusal, appended to the <see cref="PimPolicyViolation"/> message.</summary>
    protected abstract string PolicyAdvice { get; }

    /// <summary>An example guid() name for the "not a GUID" message.</summary>
    protected abstract string NameExample { get; }

    /// <summary>The (sugar, canonical) property pairs a plan before Prepare stands in for.</summary>
    protected abstract IReadOnlyList<(string Sugar, string Canonical)> SugarProperties { get; }

    /// <summary>The type's rules (<see cref="RoleEligibilityScheduleRules.Check"/>...), opaque values skipped.</summary>
    protected abstract IReadOnlyList<PimRequestFinding> Check(JsonObject properties);

    /// <summary>The type's normalisation in place (Graph casing, requestType removed).</summary>
    protected abstract void NormalizeRules(JsonObject properties);

    /// <summary>The identity tuple a key carries.</summary>
    protected abstract TTuple RequireTuple(ResourceKey key, ResourceIdentifier? id);

    /// <summary>The Direct, active schedule of the tuple, or <c>null</c>.</summary>
    protected abstract Task<JsonObject?> FindAsync(IGraphClient graph, TTuple tuple, GraphApi api, RetryHint hint, CancellationToken cancellationToken);

    /// <summary>The request body: action, tuple, schedule and metadata.</summary>
    protected abstract JsonObject RequestBody(string action, TTuple tuple, JsonNode? scheduleInfo, JsonNode? justification, JsonNode? ticketInfo);

    /// <summary>The tuple as the applied resource's extras.</summary>
    protected abstract JsonObject Extra(TTuple tuple);

    /// <summary>The comparable view of a live schedule (tuple + <see cref="PimScheduleComparison.ScheduleView"/>).</summary>
    protected abstract JsonObject LiveView(JsonObject live);

    /// <summary>The schedule an adminRemove request carries; none by default.</summary>
    protected virtual JsonNode? RemoveSchedule(ExistingResource live)
    {
        return null;
    }

    // ---- orphan report ----

    public abstract OrphanScope OrphanScope { get; }

    public abstract Task<IReadOnlyList<OrphanCandidate>> ListOrphanCandidatesAsync(OrphanQuery query, IGraphClient graph, CancellationToken cancellationToken);

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
            return Unsupported(desired, existing, $"{desired.Describe()}: the name '{desired.Id.NameLeaf}' is not a GUID; name eligibilities with a deterministic GUID, for example {NameExample}.");
        }

        if (Check(properties) is { Count: > 0 } findings)
        {
            return Unsupported(desired, existing, $"{desired.Describe()}: {string.Join("; ", findings.Select(f => f.Message))}.");
        }

        Normalize(properties);
        if (existing is null)
        {
            // Not yet prepared (a symbolic name is planned before PrepareAsync runs): sugar stands in for the canonical property for this plan only,
            // so a later PrepareAsync still sees the declaration as written.
            var standIns = new List<string>();
            foreach (var (sugar, canonical) in SugarProperties)
            {
                StandIn(properties, sugar, canonical, standIns);
            }

            try
            {
                return base.Plan(desired, null, options);
            }
            finally
            {
                standIns.ForEach(name => Remove(properties, name));
            }
        }

        var aligned = new ExistingResource(existing.GraphId, PimScheduleComparison.Align(properties, existing.Properties, Clock.GetUtcNow(), TimeTolerance, IsOpaque), existing.Raw);
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
        var found = await FindAsync(graph, RequireTuple(key, null), key.Api, RetryHint.Default, cancellationToken).ConfigureAwait(false);
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
            var response = await SendAsync(desired, graph, PimScheduleRequests.AdminAssign, tuple, properties, cancellationToken).ConfigureAwait(false);
            live = await FindAsync(graph, tuple, desired.Api, RetryHint.ExpectReplicationLag, cancellationToken).ConfigureAwait(false)
                ?? StandInFor(response, desired);
        }
        catch (GraphException ex) when (PimScheduleRequests.IsAlreadyExists(ex))
        {
            // RoleAssignmentExists: made by another run, or not yet listed when this create was planned. Adopt it; when it cannot be read back yet,
            // the engine waits out replication on the same answer.
            var found = await FindAsync(graph, tuple, desired.Api, RetryHint.ExpectReplicationLag, cancellationToken).ConfigureAwait(false);
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
        var response = await SendAsync(desired, graph, PimScheduleRequests.AdminUpdate, tuple, properties, cancellationToken).ConfigureAwait(false);
        var live = await FindAsync(graph, tuple, desired.Api, RetryHint.ExpectReplicationLag, cancellationToken).ConfigureAwait(false);
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
        var body = RequestBody(PimScheduleRequests.AdminRemove, tuple, RemoveSchedule(live), JsonValue.Create(PimScheduleRules.DefaultJustification), null);
        try
        {
            await graph.PostAsync(new GraphRequest(RequestsPath, key.Api), body, cancellationToken).ConfigureAwait(false);
        }
        catch (GraphException ex) when (PimScheduleRequests.IsAlreadyGone(ex))
        {
            // Already removed (or expired) outside EARM.
        }
        catch (GraphException ex) when (PimScheduleRequests.IsPolicyViolation(ex))
        {
            throw new ProviderException(PimPolicyViolation, $"{TypeName} {key.Describe()}: the adminRemove request was refused by {PolicyOwner}: {ex.GraphMessage}");
        }
    }

    // ---- helpers ----

    /// <summary>Sends one schedule request; a PIM policy refusal becomes <see cref="PimPolicyViolation"/>, a failed request status an apply failure.</summary>
    private async Task<JsonObject?> SendAsync(ResourceInstance desired, IGraphClient graph, string action, TTuple tuple, JsonObject properties, CancellationToken cancellationToken)
    {
        var body = RequestBody(action, tuple, Get(properties, "scheduleInfo"), Get(properties, "justification") ?? JsonValue.Create(PimScheduleRules.DefaultJustification), Get(properties, "ticketInfo"));
        GraphResponse response;
        try
        {
            response = await graph.PostAsync(new GraphRequest(RequestsPath, desired.Api, hint: desired.RetryHint), (JsonObject)SecretJson.Reveal(body)!, cancellationToken).ConfigureAwait(false);
        }
        catch (GraphException ex) when (PimScheduleRequests.IsPolicyViolation(ex))
        {
            throw new ProviderException(PimPolicyViolation, $"{desired.Describe()}: the {action} request was refused by {PolicyOwner}: {ex.GraphMessage}. {PolicyAdvice}", desired.Id);
        }

        var request = response.Body as JsonObject;
        if (PimScheduleRequests.HasFailedStatus(request, out var status))
        {
            throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: the {action} request ended with status {status}.", desired.Id);
        }

        return request;
    }

    /// <summary>The live view: the tuple and the schedule, without Graph's bookkeeping.</summary>
    private ExistingResource Existing(JsonObject live)
    {
        string id = JsonObjects.GetId(live) ?? throw new ProviderException(EngineDiagnosticCodes.ReadFailed, $"The live {TypeName} schedule has no 'id'.");
        return new ExistingResource(id, LiveView(live), (JsonObject)JsonValues.Clone(live)!);
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

    /// <summary>The type's normalisation plus the defaults: a permanent schedule and the neutral justification.</summary>
    protected void Normalize(JsonObject properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        NormalizeRules(properties);
        if (!Declares(properties, "scheduleInfo"))
        {
            Set(properties, "scheduleInfo", new JsonObject { ["expiration"] = new JsonObject { ["type"] = PimScheduleRules.NoExpiration } });
        }

        if (!Declares(properties, "justification"))
        {
            Set(properties, "justification", JsonValue.Create(PimScheduleRules.DefaultJustification));
        }
    }

    /// <summary>A <see cref="ProviderException"/> under <see cref="InvalidCode"/>.</summary>
    protected ProviderException Invalid(ResourceInstance desired, string message)
    {
        ArgumentNullException.ThrowIfNull(desired);
        return new ProviderException(InvalidCode, $"{desired.Describe()}: {message}", desired.Id);
    }

    /// <summary>The evaluated properties of an instance being prepared.</summary>
    protected static JsonObject PreparedProperties(ResourceInstance desired)
    {
        ArgumentNullException.ThrowIfNull(desired);
        return desired.Properties ?? throw new InvalidOperationException($"{desired.Describe()}: properties must be evaluated before preparing.");
    }

    /// <summary>A deploy-time-unknown or secret value cannot be judged at plan time.</summary>
    protected static bool IsOpaque(JsonNode? node)
    {
        return UnknownJson.IsUnknown(node) || SecretJson.IsSecret(node, out _);
    }

    protected static bool Declares(JsonObject properties, string name)
    {
        return JsonObjects.TryGetIgnoreCase(properties, name, out var value) && !JsonObjects.IsAbsent(value);
    }

    protected static JsonNode? Get(JsonObject properties, string name)
    {
        return JsonObjects.TryGetIgnoreCase(properties, name, out var value) && !JsonObjects.IsAbsent(value) ? value : null;
    }

    /// <summary>The trimmed text of a declared, known string property, or <c>null</c>.</summary>
    protected static string? Text(JsonObject properties, string name)
    {
        return Get(properties, name) is { } node && !IsOpaque(node) && JsonValues.TryGetString(node, out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;
    }

    /// <summary>A copy of a deploy-time-unknown property value (what-if: an object deployed in this template), or <c>null</c>.</summary>
    protected static JsonNode? Unknown(JsonObject properties, string name)
    {
        return Get(properties, name) is { } node && UnknownJson.IsUnknown(node) ? JsonValues.Clone(node) : null;
    }

    protected static void Set(JsonObject properties, string name, JsonNode? value)
    {
        ArgumentNullException.ThrowIfNull(properties);
        string key = JsonObjects.FindKey(properties, name) ?? name;
        properties[key] = value;
    }

    protected static void Remove(JsonObject properties, string name)
    {
        ArgumentNullException.ThrowIfNull(properties);
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

    private string RequireId(JsonObject live, ResourceInstance desired)
    {
        return JsonObjects.GetId(live)
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{TypeName}: Graph returned a schedule without 'id' for '{desired.Name}'.", desired.Id);
    }

    /// <summary>The catalog's definition of <paramref name="resourceType"/>.</summary>
    protected static ResourceTypeDefinition RequireDefinition(ResourceTypeCatalog catalog, string resourceType)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.Find(resourceType)
            ?? throw new InvalidOperationException($"The catalog does not define '{resourceType}'.");
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
