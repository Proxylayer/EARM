using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;

namespace EArm.TestSupport;

/// <summary>
/// The Azure role grants of <see cref="FakeAzureResourceManager"/>:
/// <list type="bullet">
/// <item><c>GET {scope}/providers/Microsoft.Authorization/roleEligibilitySchedules</c> (api-version 2020-10-01): the schedules at the scope, at its parents and
/// below it, each with its own <c>properties.scope</c> and <c>memberType</c> (as ARM lists them), paged by <see cref="PageSize"/>.</item>
/// <item><c>PUT {scope}/providers/Microsoft.Authorization/roleEligibilityScheduleRequests/{name}</c> (2022-04-01-preview): AdminAssign adds a Direct schedule (400
/// <c>RoleAssignmentExists</c> when one exists; the pending policy of a management group appears), AdminRemove removes it (400
/// <c>RoleAssignmentDoesNotExist</c> when none exists); <see cref="RequestRefusal"/> refuses a request instead.</item>
/// <item><c>GET</c>, <c>PUT</c> and <c>DELETE</c> of <c>{scope}/providers/Microsoft.Authorization/roleAssignments</c> (2022-04-01): the list (at the scope, its
/// parents and below, paged by <see cref="PageSize"/>), a create by name (409 <c>RoleAssignmentExists</c> when the grant exists under another name, the same
/// assignment again when it exists under that name) and a delete by name (204, also when it is gone).</item>
/// </list>
/// </summary>
public sealed partial class FakeAzureResourceManager
{
    private readonly List<GrantState> _schedules = [];
    private readonly List<GrantState> _assignments = [];

    /// <summary>The page size of the schedule and assignment lists; 0 (the default) answers one page.</summary>
    public int PageSize { get; set; }

    /// <summary>Refuses an eligibility request: given its body, an error to answer (status, code, message) or <c>null</c> to process it.</summary>
    public Func<JsonObject, (HttpStatusCode Status, string Code, string Message)?>? RequestRefusal { get; set; }

    /// <summary>Every eligibility request: its path and body as sent.</summary>
    public List<(string Path, JsonObject Body)> EligibilityRequests { get; } = [];

    /// <summary>Every role assignment PUT: its path and body as sent.</summary>
    public List<(string Path, JsonObject Body)> AssignmentPuts { get; } = [];

    /// <summary>Every role assignment DELETE path.</summary>
    public List<string> AssignmentDeletes { get; } = [];

    /// <summary>The eligibility schedules (scope, role GUID, principal, memberType, end).</summary>
    public IReadOnlyList<(string Scope, string Role, string PrincipalId, string MemberType, string? EndDateTime)> Schedules
    {
        get
        {
            lock (_gate)
            {
                return [.. _schedules.Select(g => (g.Scope, g.Role, g.PrincipalId, g.MemberType, g.EndDateTime))];
            }
        }
    }

    /// <summary>The role assignments (scope, role GUID, principal, name).</summary>
    public IReadOnlyList<(string Scope, string Role, string PrincipalId, string Name)> Assignments
    {
        get
        {
            lock (_gate)
            {
                return [.. _assignments.Select(g => (g.Scope, g.Role, g.PrincipalId, g.Name))];
            }
        }
    }

    /// <summary>Adds an eligibility schedule (the scope exists from then on).</summary>
    public void AddSchedule(string scope, string role, string principalId, string memberType = "Direct", string? endDateTime = null)
    {
        lock (_gate)
        {
            Scopes.Add(scope);
            _schedules.Add(new GrantState(scope, role, principalId, Guid.NewGuid().ToString("D")) { MemberType = memberType, EndDateTime = endDateTime });
        }
    }

    /// <summary>Adds a role assignment (the scope exists from then on).</summary>
    /// <returns>The assignment's name.</returns>
    public string AddAssignment(string scope, string role, string principalId, string? name = null)
    {
        lock (_gate)
        {
            Scopes.Add(scope);
            var assignment = new GrantState(scope, role, principalId, name ?? Guid.NewGuid().ToString("D"));
            _assignments.Add(assignment);
            return assignment.Name;
        }
    }

    private (HttpStatusCode, JsonObject?)? RouteGrants(string method, Uri uri, string scope, string[] rest, string? body)
    {
        return (method, rest) switch
        {
            ("GET", ["roleEligibilitySchedules"]) => List(scope, uri, _schedules, "roleEligibilitySchedules"),
            ("PUT", ["roleEligibilityScheduleRequests", var name]) => EligibilityRequest(uri.AbsolutePath, scope, name, body),
            ("GET", ["roleAssignments"]) => List(scope, uri, _assignments, "roleAssignments"),
            ("PUT", ["roleAssignments", var name]) => PutAssignment(uri.AbsolutePath, scope, name, body),
            ("DELETE", ["roleAssignments", var name]) => DeleteAssignment(uri.AbsolutePath, scope, name),
            _ => null,
        };
    }

    /// <summary>The grants at the scope, at its parents and below it (ARM lists all three; the caller keeps its own scope), one page at a time.</summary>
    private (HttpStatusCode, JsonObject?) List(string scope, Uri uri, List<GrantState> grants, string collection)
    {
        if (!Scopes.Contains(scope))
        {
            return Error(HttpStatusCode.NotFound, "ResourceGroupNotFound", "Resource group or scope '" + scope + "' could not be found.");
        }

        var items = grants
            .Where(g => Related(g.Scope, scope))
            .Select(g => g.ToJson(collection))
            .ToList();
        int skip = int.TryParse(Query(uri, "$skiptoken"), NumberStyles.None, CultureInfo.InvariantCulture, out int token) ? token : 0;
        int take = PageSize > 0 ? PageSize : items.Count;
        var page = new JsonObject { ["value"] = new JsonArray(items.Skip(skip).Take(take).Select(i => (JsonNode?)i).ToArray()) };
        if (skip + take < items.Count)
        {
            page["nextLink"] = "https://management.azure.com" + uri.AbsolutePath + "?api-version=" + Query(uri, "api-version") + "&$skiptoken=" + (skip + take).ToString(CultureInfo.InvariantCulture);
        }

        return (HttpStatusCode.OK, page);
    }

    private (HttpStatusCode, JsonObject?) EligibilityRequest(string path, string scope, string name, string? body)
    {
        var parsed = JsonNode.Parse(body ?? "{}")!.AsObject();
        EligibilityRequests.Add((path, (JsonObject)parsed.DeepClone()));
        if (RequestRefusal?.Invoke(parsed) is { } refusal)
        {
            return Error(refusal.Status, refusal.Code, refusal.Message);
        }

        var properties = parsed["properties"]!.AsObject();
        string role = RoleOf(properties["roleDefinitionId"]!.GetValue<string>());
        string principal = properties["principalId"]!.GetValue<string>();
        string requestType = properties["requestType"]!.GetValue<string>();
        Scopes.Add(scope);
        var existing = _schedules.FirstOrDefault(g => g.MemberType == "Direct" && g.Is(scope, role, principal));
        string? target = null;
        switch (requestType)
        {
            case "AdminAssign" when existing is not null:
                return Error(HttpStatusCode.BadRequest, "RoleAssignmentExists", "The Role assignment already exists.");
            case "AdminAssign":
                var schedule = new GrantState(scope, role, principal, Guid.NewGuid().ToString("D"))
                {
                    EndDateTime = properties["scheduleInfo"]?["expiration"]?["endDateTime"]?.GetValue<string>(),
                };
                _schedules.Add(schedule);
                target = schedule.Path("roleEligibilitySchedules");
                if (_pendingPolicies.FirstOrDefault(p => string.Equals(p.Scope, scope, StringComparison.OrdinalIgnoreCase) && string.Equals(p.Role, role, StringComparison.OrdinalIgnoreCase)) is { Scope: not null } pending)
                {
                    _pendingPolicies.Remove(pending);
                    _policies.Add(new AzurePolicyState(pending.Scope, pending.Role, Guid.NewGuid().ToString("D"), DefaultRules()));
                }

                break;
            case "AdminRemove" when existing is null:
                return Error(HttpStatusCode.BadRequest, "RoleAssignmentDoesNotExist", "The Role assignment does not exist.");
            case "AdminRemove":
                _schedules.Remove(existing);
                break;
            default:
                return Error(HttpStatusCode.BadRequest, "InvalidRequestType", "unscripted requestType " + requestType);
        }

        var answer = (JsonObject)parsed.DeepClone();
        answer["id"] = path;
        answer["name"] = name;
        answer["type"] = "Microsoft.Authorization/RoleEligibilityScheduleRequests";
        answer["properties"]!["status"] = requestType == "AdminAssign" ? "Provisioned" : "Revoked";
        answer["properties"]!["scope"] = scope;
        answer["properties"]!["targetRoleEligibilityScheduleId"] = target;
        return (HttpStatusCode.Created, answer);
    }

    private (HttpStatusCode, JsonObject?) PutAssignment(string path, string scope, string name, string? body)
    {
        var parsed = JsonNode.Parse(body ?? "{}")!.AsObject();
        AssignmentPuts.Add((path, (JsonObject)parsed.DeepClone()));
        var properties = parsed["properties"]!.AsObject();
        string role = RoleOf(properties["roleDefinitionId"]!.GetValue<string>());
        string principal = properties["principalId"]!.GetValue<string>();
        Scopes.Add(scope);
        if (_assignments.FirstOrDefault(g => string.Equals(g.Scope, scope, StringComparison.OrdinalIgnoreCase) && string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)) is { } same)
        {
            return (HttpStatusCode.OK, same.ToJson("roleAssignments"));
        }

        if (_assignments.Any(g => g.Is(scope, role, principal)))
        {
            return Error(HttpStatusCode.Conflict, "RoleAssignmentExists", "The role assignment already exists.");
        }

        var assignment = new GrantState(scope, role, principal, name)
        {
            PrincipalType = properties["principalType"]?.GetValue<string>(),
            Description = properties["description"]?.GetValue<string>(),
        };
        _assignments.Add(assignment);
        return (HttpStatusCode.Created, assignment.ToJson("roleAssignments"));
    }

    private (HttpStatusCode, JsonObject?) DeleteAssignment(string path, string scope, string name)
    {
        AssignmentDeletes.Add(path);
        if (_assignments.FirstOrDefault(g => string.Equals(g.Scope, scope, StringComparison.OrdinalIgnoreCase) && string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)) is not { } assignment)
        {
            return (HttpStatusCode.NoContent, null);
        }

        _assignments.Remove(assignment);
        return (HttpStatusCode.OK, assignment.ToJson("roleAssignments"));
    }

    /// <summary>True when <paramref name="grantScope"/> is <paramref name="scope"/>, one of its parents, or below it.</summary>
    private static bool Related(string grantScope, string scope)
    {
        return string.Equals(grantScope, scope, StringComparison.OrdinalIgnoreCase)
            || scope.StartsWith(grantScope + "/", StringComparison.OrdinalIgnoreCase)
            || grantScope.StartsWith(scope + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string RoleOf(string roleDefinitionId)
    {
        return roleDefinitionId[(roleDefinitionId.LastIndexOf('/') + 1)..];
    }

    private sealed class GrantState(string scope, string role, string principalId, string name)
    {
        public string Scope { get; } = scope;

        public string Role { get; } = role;

        public string PrincipalId { get; } = principalId;

        public string Name { get; } = name;

        public string MemberType { get; init; } = "Direct";

        public string? EndDateTime { get; init; }

        public string? PrincipalType { get; init; }

        public string? Description { get; init; }

        public string Path(string collection)
        {
            return Scope + Authorization + "/" + collection + "/" + Name;
        }

        public bool Is(string scope, string role, string principalId)
        {
            return string.Equals(Scope, scope, StringComparison.OrdinalIgnoreCase) && string.Equals(Role, role, StringComparison.OrdinalIgnoreCase)
                && string.Equals(PrincipalId, principalId, StringComparison.OrdinalIgnoreCase);
        }

        public JsonObject ToJson(string collection)
        {
            var properties = new JsonObject
            {
                ["scope"] = Scope,
                ["roleDefinitionId"] = RoleDefinitionId(Scope, Role),
                ["principalId"] = PrincipalId,
                ["principalType"] = PrincipalType ?? "Group",
            };
            if (collection == "roleEligibilitySchedules")
            {
                properties["memberType"] = MemberType;
                properties["status"] = "Provisioned";
                properties["startDateTime"] = "2026-10-01T00:00:00Z";
                properties["endDateTime"] = EndDateTime;
            }
            else
            {
                properties["description"] = Description;
            }

            return new JsonObject
            {
                ["properties"] = properties,
                ["name"] = Name,
                ["type"] = "Microsoft.Authorization/" + collection,
                ["id"] = Path(collection),
            };
        }
    }
}
