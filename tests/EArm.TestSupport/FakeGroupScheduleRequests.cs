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
/// PIM for Groups schedule requests in the fake tenant: eligibility (<see cref="Eligibility"/>) or time-bound active assignment (<see cref="Assignment"/>).
/// <see cref="FakeDirectory"/> routes <c>POST {requests}</c> here: <c>adminAssign</c> creates a Direct schedule in <c>{schedules}</c> (its id is
/// <c>{groupId}_{accessId}_{requestId}</c>, which the response carries as <c>targetScheduleId</c>; an active assignment schedule also carries
/// <c>assignmentType</c> assigned), <c>adminUpdate</c> replaces its <c>scheduleInfo</c>, <c>adminRemove</c> removes it. The Graph rules modelled:
/// <c>accessId</c> (member | owner), <c>groupId</c>, <c>principalId</c> and <c>scheduleInfo</c> are required on every request; the group must exist and
/// be neither dynamic nor synchronised from on-premises; the principal must be a user or a group; a second adminAssign of the same tuple is
/// <c>400 RoleAssignmentExists</c>; an update or remove of a missing schedule is <c>400 RoleAssignmentDoesNotExist</c>; with
/// <see cref="RequireExpiration"/> (the group's default policy, whose Expiration_Admin_* rule forbids permanent schedules) a NoExpiration
/// request is <c>400 RoleAssignmentRequestPolicyValidationFailed</c>. The schedule list refuses a read without a <c>groupId</c> or <c>principalId</c>
/// filter (<c>400</c>, as Graph does). The first accepted request for a group onboards it (<see cref="OnboardedGroups"/>), permanently.
/// </summary>
public sealed class FakeGroupScheduleRequests
{
    public const string EligibilityRequestsPath = "/identityGovernance/privilegedAccess/group/eligibilityScheduleRequests";

    public const string EligibilitySchedulesPath = "/identityGovernance/privilegedAccess/group/eligibilitySchedules";

    public const string AssignmentRequestsPath = "/identityGovernance/privilegedAccess/group/assignmentScheduleRequests";

    public const string AssignmentSchedulesPath = "/identityGovernance/privilegedAccess/group/assignmentSchedules";

    private readonly HashSet<string> _onboarded = new(StringComparer.OrdinalIgnoreCase);

    private readonly bool _assignment;

    private FakeGroupScheduleRequests(string requestsPath, string schedulesPath, bool assignment)
    {
        RequestsPath = requestsPath;
        SchedulesPath = schedulesPath;
        _assignment = assignment;
    }

    /// <summary>PIM for Groups eligibility (<c>eligibilityScheduleRequests</c> / <c>eligibilitySchedules</c>).</summary>
    public static FakeGroupScheduleRequests Eligibility() => new(EligibilityRequestsPath, EligibilitySchedulesPath, assignment: false);

    /// <summary>PIM for Groups active assignment (<c>assignmentScheduleRequests</c> / <c>assignmentSchedules</c>).</summary>
    public static FakeGroupScheduleRequests Assignment() => new(AssignmentRequestsPath, AssignmentSchedulesPath, assignment: true);

    /// <summary>The request collection this fake answers.</summary>
    public string RequestsPath { get; }

    /// <summary>The schedule collection this fake maintains.</summary>
    public string SchedulesPath { get; }

    /// <summary>The groups' PIM policy forbids permanent schedules (Expiration_Admin_*): a NoExpiration request is refused.</summary>
    public bool RequireExpiration { get; set; }

    /// <summary>Every request body received, in order.</summary>
    public List<JsonObject> Requests { get; } = [];

    /// <summary>
    /// Group or principal ids PIM has not seen yet (created moments earlier): a schedule list filtered on one answers <c>404 ResourceNotFound</c>, as
    /// Graph does, instead of an empty collection. An accepted adminAssign for the group and principal makes them seen.
    /// </summary>
    public HashSet<string> UnseenIds { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many adminUpdate requests answer <c>400 RoleAssignmentExists</c> first (a schedule that was just replaced and is still settling).</summary>
    public int ExistsOnUpdate { get; set; }

    /// <summary>How many adminRemove requests answer <c>400 ActiveDurationTooShort</c> first (PIM refuses to remove a schedule younger than five minutes).</summary>
    public int TooNewOnRemove { get; set; }

    /// <summary>Every adminRemove answers <c>400 InvalidRequest</c> (the removal is refused; nothing is removed).</summary>
    public bool RefuseRemove { get; set; }

    /// <summary>
    /// The caller lacks the active role PIM for Groups requires (only eligible for it, or Privileged Role Administrator on a group that is not
    /// role-assignable): every schedule list and request answers <c>403 UnauthorizedAccessException</c>, as Graph does.
    /// </summary>
    public bool AccessDenied { get; set; }

    /// <summary>The groups PIM for Groups has onboarded (on their first accepted request; never offboarded).</summary>
    public IReadOnlyCollection<string> OnboardedGroups => _onboarded;

    internal bool IsRequests(string collectionPath)
    {
        return collectionPath.TrimEnd('/').Equals(RequestsPath, StringComparison.OrdinalIgnoreCase);
    }

    internal bool IsSchedules(string collectionPath)
    {
        return collectionPath.TrimEnd('/').Equals(SchedulesPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A schedule list read: Graph requires a <c>groupId</c> or <c>principalId</c> filter; a filter naming an unseen id is a 404; a caller without the
    /// required active role gets a 403.
    /// </summary>
    internal void BeforeList(IReadOnlyDictionary<string, string>? query, RetryHint hint)
    {
        RequireAccess(hint);
        string? filter = query is not null && query.TryGetValue("$filter", out var value) ? value : null;
        if (string.IsNullOrWhiteSpace(filter)
            || (filter.IndexOf("groupId eq", StringComparison.OrdinalIgnoreCase) < 0 && filter.IndexOf("principalId eq", StringComparison.OrdinalIgnoreCase) < 0))
        {
            throw FakeDirectory.BadRequest("A $filter on groupId or principalId is required to list PIM for Groups schedules.", hint);
        }

        if (UnseenIds.FirstOrDefault(id => filter.Contains(id, StringComparison.OrdinalIgnoreCase)) is { } unseen)
        {
            throw new GraphException(HttpStatusCode.NotFound, "ResourceNotFound", $"The resource '{unseen}' could not be found.", hint);
        }
    }

    private void RequireAccess(RetryHint hint)
    {
        if (AccessDenied)
        {
            throw new GraphException(HttpStatusCode.Forbidden, "UnauthorizedAccessException", "Attempted to perform an unauthorized operation.", hint);
        }
    }

    /// <summary>Handles one request POST and returns the request object Graph answers with.</summary>
    /// <param name="body">The request body.</param>
    /// <param name="schedules">The schedule collection of the fake tenant.</param>
    /// <param name="locate">Finds a directory object by id: the object and its collection path, or <c>null</c>.</param>
    /// <param name="hint">The request's retry hint (for the errors).</param>
    internal JsonObject Submit(JsonNode? body, List<JsonObject> schedules, Func<string, (JsonObject Object, string Collection)?> locate, RetryHint hint)
    {
        if (body is not JsonObject request)
        {
            throw FakeDirectory.BadRequest("A JSON object body is required.", hint);
        }

        RequireAccess(hint);
        Requests.Add((JsonObject)request.DeepClone());
        string action = Text(request, "action") ?? throw FakeDirectory.BadRequest("The request needs an action.", hint);
        string groupId = Text(request, "groupId") ?? throw FakeDirectory.BadRequest("The request needs a groupId.", hint);
        string principalId = Text(request, "principalId") ?? throw FakeDirectory.BadRequest("The request needs a principalId.", hint);
        string accessId = Text(request, "accessId") ?? throw FakeDirectory.BadRequest("The request needs an accessId.", hint);
        if (!accessId.Equals("member", StringComparison.Ordinal) && !accessId.Equals("owner", StringComparison.Ordinal))
        {
            throw FakeDirectory.BadRequest($"Invalid accessId '{accessId}': member or owner.", hint);
        }

        if (request["scheduleInfo"] is not JsonObject)
        {
            throw FakeDirectory.BadRequest("The request needs a scheduleInfo.", hint);
        }

        RequireGroup(groupId, locate, hint);
        var current = schedules.FirstOrDefault(s => SameTuple(s, groupId, principalId, accessId));
        string requestId = Guid.NewGuid().ToString("D");
        string now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        switch (action.ToLowerInvariant())
        {
            case "adminassign":
            {
                RequirePrincipal(principalId, locate, hint);
                if (current is not null)
                {
                    throw new GraphException(HttpStatusCode.BadRequest, "RoleAssignmentExists", "The Role assignment already exists.", hint);
                }

                var scheduleInfo = Schedule(request, now, hint);
                string scheduleId = groupId + "_" + accessId + "_" + requestId;
                var schedule = new JsonObject
                {
                    ["id"] = scheduleId,
                    ["groupId"] = groupId,
                    ["principalId"] = principalId,
                    ["accessId"] = accessId,
                    ["createdUsing"] = requestId,
                    ["createdDateTime"] = now,
                    ["modifiedDateTime"] = null,
                    ["status"] = "Provisioned",
                    ["memberType"] = "direct",
                    ["scheduleInfo"] = scheduleInfo,
                };
                if (_assignment)
                {
                    schedule["assignmentType"] = "assigned";
                }

                schedules.Add(schedule);
                _onboarded.Add(groupId);
                UnseenIds.Remove(groupId);
                UnseenIds.Remove(principalId);
                return Response(requestId, "Provisioned", action, request, scheduleId, scheduleInfo.DeepClone());
            }

            case "adminupdate":
            {
                if (current is null)
                {
                    throw new GraphException(HttpStatusCode.BadRequest, "RoleAssignmentDoesNotExist", "The Role assignment does not exist.", hint);
                }

                if (ExistsOnUpdate > 0)
                {
                    ExistsOnUpdate--;
                    throw new GraphException(HttpStatusCode.BadRequest, "RoleAssignmentExists", "The Role assignment already exists.", hint);
                }

                var scheduleInfo = Schedule(request, now, hint);
                current["scheduleInfo"] = scheduleInfo;
                current["modifiedDateTime"] = now;
                return Response(requestId, "Provisioned", action, request, Text(current, "id"), scheduleInfo.DeepClone());
            }

            case "adminremove":
            {
                if (current is null)
                {
                    throw new GraphException(HttpStatusCode.BadRequest, "RoleAssignmentDoesNotExist", "The Role assignment does not exist.", hint);
                }

                if (RefuseRemove)
                {
                    throw new GraphException(HttpStatusCode.BadRequest, "InvalidRequest", "The removal request was refused.", hint);
                }

                if (TooNewOnRemove > 0)
                {
                    TooNewOnRemove--;
                    throw new GraphException(HttpStatusCode.BadRequest, "ActiveDurationTooShort", "The Active duration is too short. Minimum Required is 5 minutes.", hint);
                }

                schedules.Remove(current);
                return Response(requestId, "Revoked", action, request, null, null);
            }

            default:
                throw FakeDirectory.BadRequest($"The action '{action}' is not supported by the fake.", hint);
        }
    }

    private static void RequireGroup(string groupId, Func<string, (JsonObject Object, string Collection)?> locate, RetryHint hint)
    {
        if (locate(groupId) is not { } group || !group.Collection.Equals("/groups", StringComparison.OrdinalIgnoreCase))
        {
            throw FakeDirectory.BadRequest("The group does not exist.", hint);
        }

        if (group.Object["groupTypes"] is JsonArray types && types.Any(t => t is JsonValue v && v.TryGetValue<string>(out var text) && text.Equals("DynamicMembership", StringComparison.OrdinalIgnoreCase)))
        {
            throw FakeDirectory.BadRequest("Dynamic membership groups are not supported by PIM for Groups.", hint);
        }

        if (group.Object["onPremisesSyncEnabled"] is JsonValue synced && synced.GetValueKind() == JsonValueKind.True)
        {
            throw FakeDirectory.BadRequest("Groups synchronized from on-premises are not supported by PIM for Groups.", hint);
        }
    }

    private static void RequirePrincipal(string principalId, Func<string, (JsonObject Object, string Collection)?> locate, RetryHint hint)
    {
        if (locate(principalId) is not { } principal)
        {
            throw FakeDirectory.BadRequest("PrincipalNotFound: the principal does not exist or has not replicated yet.", hint);
        }

        if (!principal.Collection.Equals("/users", StringComparison.OrdinalIgnoreCase) && !principal.Collection.Equals("/groups", StringComparison.OrdinalIgnoreCase))
        {
            throw FakeDirectory.BadRequest("Only users and groups can hold a group's membership or ownership through PIM.", hint);
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
            ["accessId"] = request["accessId"]?.DeepClone(),
            ["groupId"] = request["groupId"]?.DeepClone(),
            ["principalId"] = request["principalId"]?.DeepClone(),
            ["isValidationOnly"] = false,
            ["targetScheduleId"] = targetScheduleId,
            ["justification"] = request["justification"]?.DeepClone(),
            ["scheduleInfo"] = scheduleInfo,
        };
    }

    private static bool SameTuple(JsonObject schedule, string groupId, string principalId, string accessId)
    {
        return FakeDirectory.ValueEquals(Text(schedule, "groupId"), groupId)
            && FakeDirectory.ValueEquals(Text(schedule, "principalId"), principalId)
            && FakeDirectory.ValueEquals(Text(schedule, "accessId"), accessId);
    }

    private static string? Text(JsonObject? obj, string property)
    {
        return obj?[property] is JsonValue value && value.TryGetValue(out string? text) ? text : null;
    }
}
