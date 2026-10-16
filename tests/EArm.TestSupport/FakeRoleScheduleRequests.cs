using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using EArm.Graph;

namespace EArm.TestSupport;

/// <summary>
/// PIM schedule requests for directory roles in the fake tenant: eligibility (<see cref="Eligibility"/>) or time-bound active assignment
/// (<see cref="Assignment"/>). <see cref="FakeDirectory"/> routes <c>POST {requests}</c> here: <c>adminAssign</c> creates a Direct schedule in
/// <c>{schedules}</c> (its id is the request id, which the response carries as <c>targetScheduleId</c>; an active assignment schedule also carries
/// <c>assignmentType</c> Assigned), <c>adminUpdate</c> replaces its <c>scheduleInfo</c>, <c>adminRemove</c> revokes it. The Graph rules modelled: one
/// scope (<c>directoryScopeId</c> or <c>appScopeId</c>) is required; the principal must exist and a group must be role-assignable; a second adminAssign
/// of the same tuple is <c>400 RoleAssignmentExists</c>; an update or remove of a missing schedule is <c>400 RoleAssignmentDoesNotExist</c>; with
/// <see cref="RequireExpiration"/> (a seeded policy whose ExpirationRule forbids permanent schedules) a NoExpiration request is
/// <c>400 RoleAssignmentRequestPolicyValidationFailed</c>. With <see cref="ProcessingLagLists"/> a request is accepted at once but its schedule change only
/// becomes visible after that many schedule list reads (PIM processes requests asynchronously).
/// </summary>
public sealed class FakeRoleScheduleRequests
{
    public const string EligibilityRequestsPath = "/roleManagement/directory/roleEligibilityScheduleRequests";

    public const string EligibilitySchedulesPath = "/roleManagement/directory/roleEligibilitySchedules";

    public const string AssignmentRequestsPath = "/roleManagement/directory/roleAssignmentScheduleRequests";

    public const string AssignmentSchedulesPath = "/roleManagement/directory/roleAssignmentSchedules";

    private readonly List<PendingChange> _pending = [];

    private readonly bool _assignment;

    private FakeRoleScheduleRequests(string requestsPath, string schedulesPath, bool assignment)
    {
        RequestsPath = requestsPath;
        SchedulesPath = schedulesPath;
        _assignment = assignment;
    }

    /// <summary>PIM eligibility for directory roles (<c>roleEligibilityScheduleRequests</c> / <c>roleEligibilitySchedules</c>).</summary>
    public static FakeRoleScheduleRequests Eligibility() => new(EligibilityRequestsPath, EligibilitySchedulesPath, assignment: false);

    /// <summary>PIM active assignment of directory roles (<c>roleAssignmentScheduleRequests</c> / <c>roleAssignmentSchedules</c>).</summary>
    public static FakeRoleScheduleRequests Assignment() => new(AssignmentRequestsPath, AssignmentSchedulesPath, assignment: true);

    /// <summary>The request collection this fake answers.</summary>
    public string RequestsPath { get; }

    /// <summary>The schedule collection this fake maintains.</summary>
    public string SchedulesPath { get; }

    /// <summary>The role management policy forbids permanent schedules (its ExpirationRule): a NoExpiration request is refused.</summary>
    public bool RequireExpiration { get; set; }

    /// <summary>How many schedule list reads miss a request's change after it was accepted (default 0: visible at once).</summary>
    public int ProcessingLagLists { get; set; }

    /// <summary>Every request body received, in order.</summary>
    public List<JsonObject> Requests { get; } = [];

    /// <summary>
    /// Principal ids PIM has not seen yet (created moments earlier): a schedule list filtered on one answers <c>404 ResourceNotFound</c>, as Graph does,
    /// instead of an empty collection. An accepted adminAssign for the principal makes it seen.
    /// </summary>
    public HashSet<string> UnseenIds { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many adminUpdate requests answer <c>400 RoleAssignmentExists</c> first (a schedule that was just replaced and is still settling).</summary>
    public int ExistsOnUpdate { get; set; }

    internal bool IsRequests(string collectionPath)
    {
        return collectionPath.TrimEnd('/').Equals(RequestsPath, StringComparison.OrdinalIgnoreCase);
    }

    internal bool IsSchedules(string collectionPath)
    {
        return collectionPath.TrimEnd('/').Equals(SchedulesPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A schedule list read: a filter naming an unseen principal is a 404; the changes whose lag ran out are applied first.</summary>
    internal void BeforeList(List<JsonObject> schedules, IReadOnlyDictionary<string, string>? query, RetryHint hint)
    {
        string? filter = query is not null && query.TryGetValue("$filter", out var value) ? value : null;
        if (filter is not null && UnseenIds.FirstOrDefault(id => filter.Contains(id, StringComparison.OrdinalIgnoreCase)) is { } unseen)
        {
            throw new GraphException(HttpStatusCode.NotFound, "ResourceNotFound", $"The resource '{unseen}' could not be found.", hint);
        }

        foreach (var change in _pending.ToList())
        {
            if (--change.ListsLeft <= 0)
            {
                change.Apply(schedules);
                _pending.Remove(change);
            }
        }
    }

    /// <summary>Handles one request POST and returns the request object Graph answers with.</summary>
    /// <param name="body">The request body.</param>
    /// <param name="schedules">The schedule collection of the fake tenant.</param>
    /// <param name="locatePrincipal">Finds a directory object by id: the object and its collection path, or <c>null</c>.</param>
    /// <param name="hint">The request's retry hint (for the errors).</param>
    internal JsonObject Submit(JsonNode? body, List<JsonObject> schedules, Func<string, (JsonObject Object, string Collection)?> locatePrincipal, RetryHint hint)
    {
        if (body is not JsonObject request)
        {
            throw FakeDirectory.BadRequest("A JSON object body is required.", hint);
        }

        Requests.Add((JsonObject)request.DeepClone());
        string action = Text(request, "action") ?? throw FakeDirectory.BadRequest("The request needs an action.", hint);
        string principalId = Text(request, "principalId") ?? throw FakeDirectory.BadRequest("The request needs a principalId.", hint);
        string roleDefinitionId = Text(request, "roleDefinitionId") ?? throw FakeDirectory.BadRequest("The request needs a roleDefinitionId.", hint);
        string? directoryScopeId = Text(request, "directoryScopeId");
        string? appScopeId = Text(request, "appScopeId");
        if ((directoryScopeId is null) == (appScopeId is null))
        {
            throw FakeDirectory.BadRequest("Either directoryScopeId or appScopeId is required (not both).", hint);
        }

        var current = schedules.FirstOrDefault(s => SameTuple(s, principalId, roleDefinitionId, directoryScopeId, appScopeId));
        bool pendingAssign = _pending.Any(p => p.Assigns && SameTuple(p.Schedule, principalId, roleDefinitionId, directoryScopeId, appScopeId));
        bool pendingRemove = _pending.Any(p => !p.Assigns && SameTuple(p.Schedule, principalId, roleDefinitionId, directoryScopeId, appScopeId));
        bool exists = (current is not null && !pendingRemove) || pendingAssign;
        string requestId = Guid.NewGuid().ToString("D");
        string now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        switch (action.ToLowerInvariant())
        {
            case "adminassign":
            {
                RequirePrincipal(principalId, locatePrincipal, hint);
                if (exists)
                {
                    throw new GraphException(HttpStatusCode.BadRequest, "RoleAssignmentExists", "The Role assignment already exists.", hint);
                }

                var scheduleInfo = Schedule(request, now, hint);
                var schedule = new JsonObject
                {
                    ["id"] = requestId,
                    ["principalId"] = principalId,
                    ["roleDefinitionId"] = roleDefinitionId,
                    ["directoryScopeId"] = directoryScopeId,
                    ["appScopeId"] = appScopeId,
                    ["createdUsing"] = requestId,
                    ["createdDateTime"] = now,
                    ["modifiedDateTime"] = "0001-01-01T08:00:00Z",
                    ["status"] = "Provisioned",
                    ["memberType"] = "Direct",
                    ["scheduleInfo"] = scheduleInfo,
                };
                if (_assignment)
                {
                    schedule["assignmentType"] = "Assigned";
                }

                Queue(new PendingChange(schedule, Assigns: true, list => list.Add((JsonObject)schedule.DeepClone())), schedules);
                UnseenIds.Remove(principalId);
                return Response(requestId, "Provisioned", action, request, requestId, scheduleInfo.DeepClone());
            }

            case "adminupdate":
            {
                if (!exists || current is null)
                {
                    throw new GraphException(HttpStatusCode.BadRequest, "RoleAssignmentDoesNotExist", "The Role assignment does not exist.", hint);
                }

                if (ExistsOnUpdate > 0)
                {
                    ExistsOnUpdate--;
                    throw new GraphException(HttpStatusCode.BadRequest, "RoleAssignmentExists", "The Role assignment already exists.", hint);
                }

                var scheduleInfo = Schedule(request, now, hint);
                string scheduleId = Text(current, "id")!;
                Queue(new PendingChange(current, Assigns: false, list =>
                {
                    if (list.FirstOrDefault(s => FakeDirectory.ValueEquals(Text(s, "id"), scheduleId)) is { } target)
                    {
                        target["scheduleInfo"] = scheduleInfo.DeepClone();
                        target["modifiedDateTime"] = now;
                    }
                }), schedules);
                return Response(requestId, "Provisioned", action, request, scheduleId, scheduleInfo.DeepClone());
            }

            case "adminremove":
            {
                if (!exists || current is null)
                {
                    throw new GraphException(HttpStatusCode.BadRequest, "RoleAssignmentDoesNotExist", "The Role assignment does not exist.", hint);
                }

                string scheduleId = Text(current, "id")!;
                Queue(new PendingChange(current, Assigns: false, list => list.RemoveAll(s => FakeDirectory.ValueEquals(Text(s, "id"), scheduleId))), schedules);
                return Response(requestId, "Revoked", action, request, null, null);
            }

            default:
                throw FakeDirectory.BadRequest($"The action '{action}' is not supported by the fake.", hint);
        }
    }

    private void Queue(PendingChange change, List<JsonObject> schedules)
    {
        if (ProcessingLagLists <= 0)
        {
            change.Apply(schedules);
            return;
        }

        change.ListsLeft = ProcessingLagLists + 1;
        _pending.Add(change);
    }

    private static void RequirePrincipal(string principalId, Func<string, (JsonObject Object, string Collection)?> locatePrincipal, RetryHint hint)
    {
        if (locatePrincipal(principalId) is not { } principal)
        {
            throw FakeDirectory.BadRequest("PrincipalNotFound: the principal does not exist or has not replicated yet.", hint);
        }

        if (principal.Collection.Equals("/groups", StringComparison.OrdinalIgnoreCase)
            && (principal.Object["isAssignableToRole"] is not JsonValue assignable || assignable.GetValueKind() != JsonValueKind.True))
        {
            throw FakeDirectory.BadRequest("The group must be role-assignable (isAssignableToRole) to hold a directory role.", hint);
        }
    }

    /// <summary>The stored schedule: the start defaults to now (a past start too), the expiration as requested; the policy is enforced here.</summary>
    private JsonObject Schedule(JsonObject request, string now, RetryHint hint)
    {
        var declared = request["scheduleInfo"] as JsonObject;
        var expiration = declared?["expiration"] as JsonObject;
        string type = Text(expiration, "type") ?? "noExpiration";
        if (RequireExpiration && type.Equals("noExpiration", StringComparison.OrdinalIgnoreCase))
        {
            throw new GraphException(HttpStatusCode.BadRequest, "RoleAssignmentRequestPolicyValidationFailed", "The following policy rules failed: [\"ExpirationRule\"]", hint);
        }

        string start = now;
        if (Text(declared, "startDateTime") is { } requested
            && DateTimeOffset.TryParse(requested, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            && parsed > DateTimeOffset.UtcNow)
        {
            start = requested;
        }

        return new JsonObject
        {
            ["startDateTime"] = start,
            ["recurrence"] = null,
            ["expiration"] = new JsonObject
            {
                ["type"] = type,
                ["endDateTime"] = Text(expiration, "endDateTime"),
                ["duration"] = Text(expiration, "duration"),
            },
        };
    }

    private static JsonObject Response(string id, string status, string action, JsonObject request, string? targetScheduleId, JsonNode? scheduleInfo)
    {
        return new JsonObject
        {
            ["id"] = id,
            ["status"] = status,
            ["action"] = action,
            ["principalId"] = request["principalId"]?.DeepClone(),
            ["roleDefinitionId"] = request["roleDefinitionId"]?.DeepClone(),
            ["directoryScopeId"] = request["directoryScopeId"]?.DeepClone(),
            ["appScopeId"] = request["appScopeId"]?.DeepClone(),
            ["isValidationOnly"] = false,
            ["targetScheduleId"] = targetScheduleId,
            ["justification"] = request["justification"]?.DeepClone(),
            ["scheduleInfo"] = scheduleInfo,
            ["ticketInfo"] = request["ticketInfo"]?.DeepClone() ?? new JsonObject { ["ticketNumber"] = null, ["ticketSystem"] = null },
        };
    }

    private static bool SameTuple(JsonObject schedule, string principalId, string roleDefinitionId, string? directoryScopeId, string? appScopeId)
    {
        return FakeDirectory.ValueEquals(Text(schedule, "principalId"), principalId)
            && FakeDirectory.ValueEquals(Text(schedule, "roleDefinitionId"), roleDefinitionId)
            && FakeDirectory.ValueEquals(Text(schedule, "directoryScopeId"), directoryScopeId)
            && FakeDirectory.ValueEquals(Text(schedule, "appScopeId"), appScopeId);
    }

    private static string? Text(JsonObject? obj, string property)
    {
        return obj?[property] is JsonValue value && value.TryGetValue(out string? text) ? text : null;
    }

    /// <summary>A schedule change waiting out <see cref="ProcessingLagLists"/>.</summary>
    private sealed record PendingChange(JsonObject Schedule, bool Assigns, Action<List<JsonObject>> Apply)
    {
        public int ListsLeft { get; set; }
    }
}
