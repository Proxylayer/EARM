using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core.Types;
using EArm.Core.Validation;
using EArm.Engine;
using EArm.Engine.Providers;
using EArm.Expressions.Values;
using EArm.Graph;

namespace EArm.Providers;

/// <summary>
/// <c>Microsoft.Entra/azureRoleEligibilityScheduleRequests</c>: a PIM eligibility for an Azure RBAC role at an Azure scope (a management group, a
/// subscription, a resource group or a resource), requested through Azure Resource Manager (<c>Microsoft.Authorization/roleEligibilityScheduleRequests</c>):
/// the Azure twin of <see cref="RoleEligibilityScheduleRequestsProvider"/>. The resource name is a deterministic GUID (a label); the eligibility is found by
/// (<c>scope</c>, <c>roleDefinitionId</c> or the sugar <c>role</c>, <c>principalId</c>) and read live on every run (<see cref="AzureRoleEligibilityLookup"/>).
/// <list type="bullet">
/// <item><b>Plan</b>: an eligibility of the principal at exactly the scope is adopted (<see cref="ChangeType.NoChange"/>); a declared schedule it does not
/// match is reported, never rewritten (an AdminUpdate is not sent: remove the eligibility and deploy again to change it). Inherited and group-held
/// eligibilities do not count. A missing one is a Create.</item>
/// <item><b>Create</b>: one request with requestType <c>AdminAssign</c>, the scheduleInfo declared (default: no expiration) with a start of now when none
/// is declared or the declared one is past, and the justification declared (default "Deployed by earm"). <c>RoleAssignmentExists</c> (made by another run,
/// or not listed yet when the create was planned) is adopted after a re-read. A request the role's PIM policy refuses is
/// <see cref="EngineDiagnosticCodes.PimPolicyViolation"/>, naming <c>Microsoft.Entra/azureRoleManagementPolicies</c>.</item>
/// <item><b>Delete</b> (<c>earm deployment delete</c>; incremental and complete deployments never delete one): one request with requestType
/// <c>AdminRemove</c>, as the Entra twin's adminRemove; an eligibility already gone is not an error.</item>
/// </list>
/// Not listed by the orphan report. Applied after the <c>azureRoleManagementPolicies</c> of the same scope and role
/// (<see cref="AzureRoleManagementPolicyApplyOrder"/>).
/// </summary>
[EntraProvider(ResourceType)]
public sealed class AzureRoleEligibilityScheduleRequestsProvider : AzureRoleGrantProvider
{
    public const string ResourceType = AzureRoleEligibilityScheduleRules.ResourceType;

    /// <param name="definition">The catalog definition of the type.</param>
    /// <param name="catalog">The catalog.</param>
    /// <param name="resourceManager">The run's Azure Resource Manager client; <c>null</c> in an offline registry (validate, permissions).</param>
    public AzureRoleEligibilityScheduleRequestsProvider(ResourceTypeDefinition definition, ResourceTypeCatalog? catalog, ArmClient? resourceManager)
        : base(definition, catalog, ResourceType, resourceManager)
    {
    }

    /// <summary>A provider without a Resource Manager client (offline: it validates and plans, and fails any Resource Manager call).</summary>
    public AzureRoleEligibilityScheduleRequestsProvider(ResourceTypeDefinition definition, ResourceTypeCatalog? catalog = null)
        : this(definition, catalog, null)
    {
    }

    public AzureRoleEligibilityScheduleRequestsProvider(ResourceTypeCatalog catalog)
        : this(RequireDefinition(catalog), catalog, null)
    {
    }

    public AzureRoleEligibilityScheduleRequestsProvider(ResourceTypeCatalog catalog, ArmClient? resourceManager)
        : this(RequireDefinition(catalog), catalog, resourceManager)
    {
    }

    /// <summary>The tolerance of the endDateTime comparison (ARM stores the times with its own precision).</summary>
    public static readonly TimeSpan TimeTolerance = TimeSpan.FromSeconds(1);

    /// <summary>The clock "now" and "a start in the past" are judged with; tests pin it.</summary>
    public TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>PIM processes requests asynchronously: the schedule list can miss a new eligibility for a while.</summary>
    public override bool KeyReadLagsCreate => true;

    protected override string InvalidCode => EngineDiagnosticCodes.RoleEligibilityInvalid;

    protected override string NameExample => "[guid(subscription().id, 'Contributor', parameters('groupName'), 'eligible')]";

    protected override string Noun => "Azure role eligibility";

    protected override IReadOnlyList<PimRequestFinding> Check(JsonObject properties)
    {
        return AzureRoleEligibilityScheduleRules.Check(properties, IsOpaque);
    }

    /// <summary>The Azure RBAC roles that may request the eligibility at its scope (<c>Microsoft.Authorization/roleEligibilityScheduleRequests/write</c>), for the what-if footer.</summary>
    public override IReadOnlyList<string> RequiredAzureRoles(ResourceInstance? desired)
    {
        return [$"Owner or User Access Administrator at '{DeclaredScope(desired)}' (Azure role eligibility)"];
    }

    // ---- read ----

    public override async Task<ExistingResource?> ReadAsync(ResourceKey key, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(graph);
        var grant = GrantOf(key, null);
        var schedule = await AzureRoleEligibilityLookup.FindAsync(ResourceManager, grant, cancellationToken).ConfigureAwait(false);
        return schedule is null ? null : ToExisting(grant, schedule);
    }

    // ---- plan ----

    protected override IEnumerable<string> CreateReasons(JsonObject properties)
    {
        return ["requested with one PIM AdminAssign request (PUT {scope}/providers/Microsoft.Authorization/roleEligibilityScheduleRequests/{new GUID})"];
    }

    /// <summary>The declared expiration the live schedule does not have (an AfterDuration is applied at creation and never compared).</summary>
    protected override IEnumerable<string> Drift(JsonObject properties, ExistingResource existing)
    {
        var (type, declaredEnd) = DeclaredExpiration(properties);
        string? liveText = JsonObjects.GetString(AzureRoleGrant.PropertiesOf(existing.Raw), "endDateTime");
        DateTimeOffset? liveEnd = PimScheduleRules.TryParseDateTime(liveText, out var parsed) ? parsed : null;
        string live = liveEnd is { } end ? "ends " + end.ToString("O", CultureInfo.InvariantCulture) : "is permanent";
        string? difference = type switch
        {
            PimScheduleRules.NoExpiration when liveEnd is not null => "the declaration is permanent (NoExpiration)",
            PimScheduleRules.AfterDateTime when declaredEnd is { } wanted && (liveEnd is not { } got || (got - wanted).Duration() > TimeTolerance)
                => "the declaration ends " + wanted.ToString("O", CultureInfo.InvariantCulture),
            _ => null,
        };
        return difference is null
            ? []
            : [$"the live eligibility {live}, but {difference}; an Azure eligibility is not changed in place, so it is kept as it is (remove it, for example with earm deployment delete, and deploy again to request the declared schedule)"];
    }

    // ---- create ----

    public override async Task<AppliedResource> CreateAsync(ResourceInstance desired, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(graph);
        var properties = desired.Properties ?? throw new InvalidOperationException($"{desired.Describe()}: properties must be evaluated before creating.");
        var key = ResolveKey(desired);
        var grant = GrantOf(key, desired.Id);
        string justification = Text(properties, "justification") ?? PimScheduleRules.DefaultJustification;
        var body = AzureRoleEligibilityLookup.RequestBody(AzureRoleEligibilityLookup.AdminAssign, grant, justification, ScheduleInfo(properties));
        JsonObject? request;
        try
        {
            request = await AzureRoleEligibilityLookup.SubmitAsync(ResourceManager, grant, body, cancellationToken).ConfigureAwait(false);
        }
        catch (GraphException ex) when (PimScheduleRequests.IsAlreadyExists(ex))
        {
            // RoleAssignmentExists: made by another run, or not listed yet when this create was planned. Adopt it; when it cannot be read back yet,
            // the engine waits out replication on the same answer.
            var found = await AzureRoleEligibilityLookup.FindAsync(ResourceManager, grant, cancellationToken).ConfigureAwait(false);
            if (found is null)
            {
                throw;
            }

            return Applied(desired, key, grant, found);
        }
        catch (GraphException ex) when (PimScheduleRequests.IsPolicyViolation(ex))
        {
            throw new ProviderException(
                EngineDiagnosticCodes.PimPolicyViolation,
                $"{desired.Describe()}: the AdminAssign request was refused by the PIM policy of the Azure role at {grant.Scope}: {ex.GraphMessage} The default Azure policy requires an eligibility to expire (Expiration_Admin_Eligibility, at most 365 days): declare scheduleInfo with an AfterDuration or AfterDateTime expiration, or allow permanent eligibility with a Microsoft.Entra/azureRoleManagementPolicies of the same scope and role (applied first).",
                desired.Id);
        }

        if (PimScheduleRequests.HasFailedStatus(request is null ? null : AzureRoleGrant.PropertiesOf(request), out string status))
        {
            throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: Azure Resource Manager answered the AdminAssign request with status {status}.", desired.Id);
        }

        var live = await AzureRoleEligibilityLookup.FindAsync(ResourceManager, grant, cancellationToken).ConfigureAwait(false);
        return live is not null ? Applied(desired, key, grant, live) : StandIn(desired, key, grant, request);
    }

    // ---- delete ----

    /// <summary>One <c>AdminRemove</c> request; an eligibility that is already gone is not an error.</summary>
    public override async Task DeleteAsync(ResourceKey key, ExistingResource live, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(graph);
        var grant = GrantOf(key, null);
        var body = AzureRoleEligibilityLookup.RequestBody(AzureRoleEligibilityLookup.AdminRemove, grant, PimScheduleRules.DefaultJustification, null);
        try
        {
            await AzureRoleEligibilityLookup.SubmitAsync(ResourceManager, grant, body, cancellationToken).ConfigureAwait(false);
        }
        catch (GraphException ex) when (PimScheduleRequests.IsAlreadyGone(ex))
        {
            // Already removed (or expired) outside EARM.
        }
        catch (GraphException ex) when (PimScheduleRequests.IsTooNewToRemove(ex))
        {
            throw new ProviderException(
                EngineDiagnosticCodes.ActiveAssignmentTooNew,
                $"{TypeName} {key.Describe()}: Azure Resource Manager refused to remove the eligibility because it is less than 5 minutes old; the eligibility remains, run the delete again in a few minutes. {ex.Message}");
        }
        catch (GraphException ex) when (PimScheduleRequests.IsPolicyViolation(ex))
        {
            throw new ProviderException(EngineDiagnosticCodes.PimPolicyViolation, $"{TypeName} {key.Describe()}: the AdminRemove request was refused by the PIM policy of the Azure role at {grant.Scope}: {ex.GraphMessage}");
        }
    }

    // ---- helpers ----

    /// <summary>
    /// The ARM <c>scheduleInfo</c> of the AdminAssign request: the declared start (now when absent or past) and expiration in ARM's casing (NoExpiration,
    /// AfterDateTime with endDateTime, AfterDuration with duration); no expiration when none is declared.
    /// </summary>
    private JsonObject ScheduleInfo(JsonObject properties)
    {
        var now = Clock.GetUtcNow();
        var schedule = Get(properties, "scheduleInfo") as JsonObject;
        var start = schedule is not null && Text(schedule, "startDateTime") is { } declaredStart && PimScheduleRules.TryParseDateTime(declaredStart, out var parsed) && parsed > now
            ? parsed
            : now;
        var expiration = new JsonObject();
        var (type, end) = DeclaredExpiration(properties);
        expiration["type"] = type switch
        {
            PimScheduleRules.AfterDateTime => "AfterDateTime",
            PimScheduleRules.AfterDuration => "AfterDuration",
            _ => "NoExpiration",
        };
        if (type == PimScheduleRules.AfterDateTime && end is { } endDateTime)
        {
            expiration["endDateTime"] = endDateTime.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        }
        else if (type == PimScheduleRules.AfterDuration && schedule is not null && Get(schedule, "expiration") is JsonObject declared && Text(declared, "duration") is { } duration)
        {
            expiration["duration"] = duration;
        }

        return new JsonObject
        {
            ["startDateTime"] = start.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            ["expiration"] = expiration,
        };
    }

    /// <summary>The declared expiration type (Graph's camelCase; NoExpiration when none is declared) and the declared end of an AfterDateTime.</summary>
    private static (string Type, DateTimeOffset? End) DeclaredExpiration(JsonObject properties)
    {
        if (Get(properties, "scheduleInfo") is not JsonObject schedule || Get(schedule, "expiration") is not JsonObject expiration)
        {
            return (PimScheduleRules.NoExpiration, null);
        }

        string type = PimScheduleRules.NormalizeExpirationType(Text(expiration, "type")) ?? PimScheduleRules.NoExpiration;
        DateTimeOffset? end = Text(expiration, "endDateTime") is { } text && PimScheduleRules.TryParseDateTime(text, out var parsed) ? parsed : null;
        return (type, end);
    }

    private ExistingResource ToExisting(AzureRoleGrant grant, JsonObject schedule)
    {
        string graphId = AzureRoleEligibilityLookup.SchedulePath(grant, schedule) ?? JsonObjects.GetId(schedule)
            ?? throw new ProviderException(EngineDiagnosticCodes.ReadFailed, $"{TypeName}: the eligibility schedule of {grant.Describe()} has no id.");
        var properties = AzureRoleGrant.PropertiesOf(schedule);
        var view = new JsonObject
        {
            ["scope"] = grant.Scope,
            ["roleDefinitionId"] = grant.RoleDefinitionId,
            ["principalId"] = grant.PrincipalId,
            ["startDateTime"] = JsonObjects.GetString(properties, "startDateTime"),
            ["endDateTime"] = JsonObjects.GetString(properties, "endDateTime"),
        };
        return new ExistingResource(graphId, view, (JsonObject)JsonValues.Clone(schedule)!);
    }

    private AppliedResource Applied(ResourceInstance desired, ResourceKey key, AzureRoleGrant grant, JsonObject schedule)
    {
        var existing = ToExisting(grant, schedule);
        return new AppliedResource(desired.Id, existing.GraphId, existing.Raw, key);
    }

    /// <summary>Until the schedule is listed, the request stands in: its target schedule (rebuilt in the scope's collection) is the id.</summary>
    private AppliedResource StandIn(ResourceInstance desired, ResourceKey key, AzureRoleGrant grant, JsonObject? request)
    {
        string? target = request is null ? null : JsonObjects.GetString(AzureRoleGrant.PropertiesOf(request), "targetRoleEligibilityScheduleId");
        string graphId = AzureRoleGrant.PathIn(AzureRoleEligibilityLookup.SchedulesPath(grant.Scope), target)
            ?? (request is null ? null : AzureRoleGrant.PathIn(AzureRoleEligibilityLookup.RequestsPath(grant.Scope), JsonObjects.GetId(request)))
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: the AdminAssign request returned no request object and the eligibility could not be read back.", desired.Id);
        return new AppliedResource(desired.Id, graphId, (JsonObject)JsonValues.Clone(request)!, key);
    }

    private static ResourceTypeDefinition RequireDefinition(ResourceTypeCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.Find(ResourceType) ?? throw new InvalidOperationException($"The catalog does not define '{ResourceType}'.");
    }
}
