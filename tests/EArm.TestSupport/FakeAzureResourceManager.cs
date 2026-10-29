using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;
using Microsoft.Extensions.Logging.Abstractions;

namespace EArm.TestSupport;

/// <summary>
/// An in-memory Azure Resource Manager for the PIM policies of Azure roles (<c>Microsoft.Authorization</c>, api-versions 2020-10-01 and 2022-04-01), served
/// as an <see cref="HttpMessageHandler"/> under a real <see cref="ArmClient"/> (<see cref="CreateClient"/>). It answers:
/// <list type="bullet">
/// <item><c>GET {scope}/providers/Microsoft.Authorization/roleManagementPolicyAssignments?$filter=roleDefinitionId eq '{id}'</c>: the assignments of that role at
/// the scope AND at its parent scopes (parents first, as an inherited policy is listed too), plus <see cref="ExtraAssignments"/>; 404 for a scope not in
/// <see cref="Scopes"/>; 400 InvalidARMRequest for a bare GUID in the filter (ARM wants the subscription-qualified id).</item>
/// <item><c>GET {scope}/providers/Microsoft.Authorization/roleManagementPolicies/{name}</c> and <c>PATCH</c> of it: each rule sent replaces the live rule of
/// its id whole (as ARM does); the answer is the whole policy, shaped by <see cref="PatchAnswer"/>.</item>
/// <item><c>GET {scope}/providers/Microsoft.Authorization/roleDefinitions?$filter=roleName eq '{name}'</c>: <see cref="RoleDefinitions"/> whose name equals the
/// filtered one ignoring case, or <see cref="RoleDefinitionAnswer"/> when set.</item>
/// </list>
/// The role grants (eligibility schedules and requests, role assignments) are the other part of the class (<c>FakeAzureResourceManager.Grants.cs</c>).
/// Management group scopes (<c>/providers/Microsoft.Management/managementGroups/{id}</c>) name roles by their tenant-level id; a role of a management group
/// added with <see cref="AddPolicyOnFirstRequest"/> has no policy until the first eligibility request of that role there, as ARM behaves.
/// Every request is recorded (<see cref="Requests"/>). Nothing waits: the client's retry policy runs on a no-delay provider.
/// </summary>
public sealed partial class FakeAzureResourceManager : HttpMessageHandler
{
    public const string Authorization = "/providers/Microsoft.Authorization";

    /// <summary>The built-in Reader role.</summary>
    public const string Reader = "acdd72a7-3385-48ef-bd42-f606fba81ae7";

    /// <summary>The built-in Contributor role.</summary>
    public const string Contributor = "b24988ac-6180-42a0-ab88-20f7382dd24c";

    private readonly object _gate = new();
    private readonly List<RecordedHttpRequest> _requests = [];
    private readonly List<AzurePolicyState> _policies = [];
    private readonly List<(string Scope, string Role)> _pendingPolicies = [];

    /// <summary>The scopes that exist (an assignment list of any other scope answers 404). Case-insensitive.</summary>
    public HashSet<string> Scopes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The role definitions <c>roleDefinitions?$filter=roleName eq</c> searches.</summary>
    public List<(string Id, string RoleName)> RoleDefinitions { get; } = [(Reader, "Reader"), (Contributor, "Contributor")];

    /// <summary>When set, every role definition list answers exactly these items (to model ARM answering more, or other casings, than the filter named).</summary>
    public List<JsonObject>? RoleDefinitionAnswer { get; set; }

    /// <summary>Assignments added to every assignment list (an assignment naming a policy of another scope, ...).</summary>
    public List<JsonObject> ExtraAssignments { get; } = [];

    /// <summary>Shapes the PATCH answer from the updated policy; returning <c>null</c> answers 200 with an empty body.</summary>
    public Func<JsonObject, JsonObject?>? PatchAnswer { get; set; }

    /// <summary>Every rules PATCH: the policy path and the body as sent.</summary>
    public List<(string Path, JsonObject Body)> Patches { get; } = [];

    public IReadOnlyList<RecordedHttpRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>A real <see cref="ArmClient"/> on the public cloud sending to this fake, with a fake token and a retry policy that never waits.</summary>
    public ArmClient CreateClient()
    {
        var http = new HttpClient(this, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        var retry = new RetryPolicy(new RetryOptions(), new NoDelay(), NullLogger<RetryPolicy>.Instance);
        return new ArmClient(http, new FakeJwtTokenProvider(), retry, GraphCloud.Public, TimeSpan.FromSeconds(30));
    }

    /// <summary>The prefix of a management group scope.</summary>
    public const string ManagementGroups = "/providers/Microsoft.Management/managementGroups/";

    /// <summary>The subscription-qualified id of a role at the subscription of <paramref name="scope"/>, or its tenant-level id at a management group.</summary>
    public static string RoleDefinitionId(string scope, string role)
    {
        if (IsManagementGroup(scope))
        {
            return Authorization + "/roleDefinitions/" + role;
        }

        string subscription = scope.Split('/', StringSplitOptions.RemoveEmptyEntries)[1].ToLowerInvariant();
        return "/subscriptions/" + subscription + Authorization + "/roleDefinitions/" + role;
    }

    /// <summary>True for a management group scope.</summary>
    public static bool IsManagementGroup(string scope)
    {
        return scope.StartsWith(ManagementGroups, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Makes <paramref name="scope"/> (a management group) exist with no policy for <paramref name="role"/> until the first eligibility request of that role
    /// there, which adds it (ARM's behaviour at a management group).
    /// </summary>
    public void AddPolicyOnFirstRequest(string scope, string role)
    {
        lock (_gate)
        {
            Scopes.Add(scope);
            _pendingPolicies.Add((scope, role));
        }
    }

    /// <summary>True when the policy of <paramref name="role"/> at <paramref name="scope"/> exists.</summary>
    public bool HasPolicy(string scope, string role)
    {
        lock (_gate)
        {
            return _policies.Any(p => string.Equals(p.Scope, scope, StringComparison.OrdinalIgnoreCase) && string.Equals(p.Role, role, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Adds the policy of <paramref name="role"/> at <paramref name="scope"/> (the scope exists from then on) with ARM's default Azure rules.</summary>
    /// <param name="name">The policy name; a fresh GUID when <c>null</c> (ARM does not document it as the role GUID).</param>
    /// <returns>The policy's resource path.</returns>
    public string AddPolicy(string scope, string role, string? name = null)
    {
        lock (_gate)
        {
            Scopes.Add(scope);
            var policy = new AzurePolicyState(scope, role, name ?? Guid.NewGuid().ToString("D"), DefaultRules());
            _policies.Add(policy);
            return policy.Path;
        }
    }

    /// <summary>Overrides the <c>properties.scope</c> a policy reports (a policy claiming another scope).</summary>
    public void SetReportedScope(string scope, string role, string reportedScope)
    {
        lock (_gate)
        {
            Find(scope, role).ReportedScope = reportedScope;
        }
    }

    /// <summary>A copy of a live rule.</summary>
    public JsonObject Rule(string scope, string role, string ruleId)
    {
        lock (_gate)
        {
            return (JsonObject)Find(scope, role).Rules.OfType<JsonObject>().Single(r => string.Equals(r["id"]?.GetValue<string>(), ruleId, StringComparison.OrdinalIgnoreCase)).DeepClone();
        }
    }

    /// <summary>A copy of the whole policy as GET answers it.</summary>
    public JsonObject Policy(string scope, string role)
    {
        lock (_gate)
        {
            return Find(scope, role).ToJson();
        }
    }

    /// <summary>The Azure default rules of a role (Learn: role-based-access-control/pim-resource-roles-configure-role-settings; ARM's shape, with its nulls).</summary>
    public static JsonArray DefaultRules()
    {
        JsonObject Target(string caller, string level) => new()
        {
            ["caller"] = caller,
            ["operations"] = new JsonArray("All"),
            ["level"] = level,
            ["targetObjects"] = null,
            ["inheritableSettings"] = null,
            ["enforcedSettings"] = null,
        };
        JsonObject Expiration(string caller, string level, bool required, string max) => new()
        {
            ["isExpirationRequired"] = required,
            ["maximumDuration"] = max,
            ["id"] = $"Expiration_{caller}_{level}",
            ["ruleType"] = "RoleManagementPolicyExpirationRule",
            ["target"] = Target(caller, level),
        };
        JsonObject Enablement(string caller, string level, params string[] rules) => new()
        {
            ["enabledRules"] = new JsonArray(rules.Select(r => (JsonNode?)r).ToArray()),
            ["id"] = $"Enablement_{caller}_{level}",
            ["ruleType"] = "RoleManagementPolicyEnablementRule",
            ["target"] = Target(caller, level),
        };
        IEnumerable<JsonObject> Notifications(string caller, string level) => new[] { "Admin", "Requestor", "Approver" }.Select(recipient => new JsonObject
        {
            ["notificationType"] = "Email",
            ["recipientType"] = recipient,
            ["isDefaultRecipientsEnabled"] = true,
            ["notificationLevel"] = "All",
            ["notificationRecipients"] = null,
            ["id"] = $"Notification_{recipient}_{caller}_{level}",
            ["ruleType"] = "RoleManagementPolicyNotificationRule",
            ["target"] = Target(caller, level),
        });

        var rules = new List<JsonObject>
        {
            Expiration("Admin", "Eligibility", true, "P365D"),
            Enablement("Admin", "Eligibility"),
        };
        rules.AddRange(Notifications("Admin", "Eligibility"));
        rules.Add(Expiration("Admin", "Assignment", true, "P180D"));
        rules.Add(Enablement("Admin", "Assignment", "Justification", "MultiFactorAuthentication"));
        rules.AddRange(Notifications("Admin", "Assignment"));
        rules.Add(Expiration("EndUser", "Assignment", true, "PT8H"));
        rules.Add(Enablement("EndUser", "Assignment", "MultiFactorAuthentication"));
        rules.Add(new JsonObject
        {
            ["setting"] = new JsonObject
            {
                ["isApprovalRequired"] = false,
                ["isApprovalRequiredForExtension"] = false,
                ["isRequestorJustificationRequired"] = true,
                ["approvalMode"] = "SingleStage",
                ["approvalStages"] = new JsonArray(new JsonObject
                {
                    ["approvalStageTimeOutInDays"] = 1,
                    ["isApproverJustificationRequired"] = true,
                    ["escalationTimeInMinutes"] = 0,
                    ["primaryApprovers"] = null,
                    ["isEscalationEnabled"] = false,
                    ["escalationApprovers"] = null,
                }),
            },
            ["id"] = "Approval_EndUser_Assignment",
            ["ruleType"] = "RoleManagementPolicyApprovalRule",
            ["target"] = Target("EndUser", "Assignment"),
        });
        rules.Add(new JsonObject
        {
            ["isEnabled"] = false,
            ["claimValue"] = string.Empty,
            ["id"] = "AuthenticationContext_EndUser_Assignment",
            ["ruleType"] = "RoleManagementPolicyAuthenticationContextRule",
            ["target"] = Target("EndUser", "Assignment"),
        });
        rules.AddRange(Notifications("EndUser", "Assignment"));
        return new JsonArray(rules.Select(r => (JsonNode?)r).ToArray());
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var recorded = new RecordedHttpRequest(request.Method.Method, request.RequestUri!, headers, body);
        lock (_gate)
        {
            _requests.Add(recorded);
            var (status, answer) = Route(request.Method.Method, request.RequestUri!, body);
            var response = new HttpResponseMessage(status) { RequestMessage = request };
            response.Content = new StringContent(answer?.ToJsonString() ?? string.Empty, new UTF8Encoding(false), "application/json");
            return response;
        }
    }

    private (HttpStatusCode Status, JsonObject? Body) Route(string method, Uri uri, string? body)
    {
        string path = uri.AbsolutePath;
        string? filter = Query(uri, "$filter");
        int at = path.LastIndexOf(Authorization + "/", StringComparison.OrdinalIgnoreCase);
        if (at <= 0)
        {
            return Error(HttpStatusCode.BadRequest, "InvalidResourceType", "unscripted path " + path);
        }

        string scope = path[..at];
        string[] rest = path[(at + Authorization.Length + 1)..].Split('/');
        switch (rest)
        {
            case ["roleManagementPolicyAssignments"] when method == "GET":
                return ListAssignments(scope, filter);
            case ["roleManagementPolicies", var name] when method == "GET":
                return _policies.FirstOrDefault(p => p.Is(scope, name)) is { } policy ? (HttpStatusCode.OK, policy.ToJson()) : Error(HttpStatusCode.NotFound, "RoleManagementPolicyNotFound", "no policy " + path);
            case ["roleManagementPolicies", var name] when method == "PATCH":
                return Patch(path, scope, name, body);
            case ["roleDefinitions"] when method == "GET":
                return ListRoleDefinitions(scope, filter);
            default:
                return RouteGrants(method, uri, scope, rest, body) ?? Error(HttpStatusCode.BadRequest, "InvalidResourceType", "unscripted " + method + " " + path);
        }
    }

    private (HttpStatusCode, JsonObject?) ListAssignments(string scope, string? filter)
    {
        if (!Scopes.Contains(scope))
        {
            return Error(HttpStatusCode.NotFound, "ResourceGroupNotFound", "Resource group or scope '" + scope + "' could not be found.");
        }

        string? roleId = Between(filter, "roleDefinitionId eq '", "'");
        bool qualified = roleId is not null
            && (roleId.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase) || (IsManagementGroup(scope) && roleId.StartsWith(Authorization + "/roleDefinitions/", StringComparison.OrdinalIgnoreCase)));
        if (!qualified || roleId is null)
        {
            return Error(HttpStatusCode.BadRequest, "InvalidARMRequest", "The filter must name a subscription-qualified (or, at a management group, tenant-level) role definition id.");
        }

        string role = roleId[(roleId.LastIndexOf('/') + 1)..];
        var items = _policies
            .Where(p => string.Equals(p.Role, role, StringComparison.OrdinalIgnoreCase) && (string.Equals(p.Scope, scope, StringComparison.OrdinalIgnoreCase) || scope.StartsWith(p.Scope + "/", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(p => p.Scope.Length)
            .Select(p => (JsonNode?)p.Assignment())
            .Concat(ExtraAssignments.Select(a => (JsonNode?)a.DeepClone()))
            .ToArray();
        return (HttpStatusCode.OK, new JsonObject { ["value"] = new JsonArray(items) });
    }

    private (HttpStatusCode, JsonObject?) Patch(string path, string scope, string name, string? body)
    {
        if (_policies.FirstOrDefault(p => p.Is(scope, name)) is not { } policy)
        {
            return Error(HttpStatusCode.NotFound, "RoleManagementPolicyNotFound", "no policy " + path);
        }

        var parsed = JsonNode.Parse(body ?? "{}")!.AsObject();
        Patches.Add((path, (JsonObject)parsed.DeepClone()));
        if (parsed["properties"]?["rules"] is not JsonArray rules)
        {
            return Error(HttpStatusCode.BadRequest, "InvalidRoleManagementPolicy", "properties.rules is required");
        }

        foreach (var rule in rules.OfType<JsonObject>())
        {
            string id = rule["id"]!.GetValue<string>();
            int index = policy.Rules.Select((r, i) => (r, i)).Single(x => string.Equals(x.r!["id"]!.GetValue<string>(), id, StringComparison.OrdinalIgnoreCase)).i;
            policy.Rules[index] = rule.DeepClone();
        }

        policy.Modified++;
        var answer = policy.ToJson();
        return (HttpStatusCode.OK, PatchAnswer is null ? answer : PatchAnswer(answer));
    }

    private (HttpStatusCode, JsonObject?) ListRoleDefinitions(string scope, string? filter)
    {
        string? name = Between(filter, "roleName eq '", "'");
        string? subscription = IsManagementGroup(scope) ? null : scope.Split('/', StringSplitOptions.RemoveEmptyEntries)[1];
        var items = RoleDefinitionAnswer?.Select(d => (JsonNode?)d.DeepClone()).ToArray()
            ?? RoleDefinitions.Where(d => string.Equals(d.RoleName, name, StringComparison.OrdinalIgnoreCase)).Select(d => (JsonNode?)RoleDefinition(subscription, d.Id, d.RoleName)).ToArray();
        return (HttpStatusCode.OK, new JsonObject { ["value"] = new JsonArray(items) });
    }

    /// <summary>A role definition as ARM lists it (tenant-level when <paramref name="subscription"/> is <c>null</c>: a management group's list).</summary>
    public static JsonObject RoleDefinition(string? subscription, string id, string roleName)
    {
        return new JsonObject
        {
            ["id"] = (subscription is null ? string.Empty : "/subscriptions/" + subscription) + Authorization + "/roleDefinitions/" + id,
            ["name"] = id,
            ["type"] = "Microsoft.Authorization/roleDefinitions",
            ["properties"] = new JsonObject { ["roleName"] = roleName, ["type"] = "BuiltInRole" },
        };
    }

    private AzurePolicyState Find(string scope, string role)
    {
        return _policies.Single(p => string.Equals(p.Scope, scope, StringComparison.OrdinalIgnoreCase) && string.Equals(p.Role, role, StringComparison.OrdinalIgnoreCase));
    }

    private static (HttpStatusCode, JsonObject?) Error(HttpStatusCode status, string code, string message)
    {
        return (status, new JsonObject { ["error"] = new JsonObject { ["code"] = code, ["message"] = message } });
    }

    private static string? Query(Uri uri, string name)
    {
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0 && Uri.UnescapeDataString(part[..eq]) == name)
            {
                return Uri.UnescapeDataString(part[(eq + 1)..]);
            }
        }

        return null;
    }

    private static string? Between(string? text, string start, string end)
    {
        if (text is null || text.IndexOf(start, StringComparison.OrdinalIgnoreCase) is var i && i < 0)
        {
            return null;
        }

        int from = i + start.Length;
        int to = text.IndexOf(end, from, StringComparison.Ordinal);
        return to < 0 ? null : text[from..to];
    }

    private sealed class NoDelay : IDelayProvider
    {
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class AzurePolicyState(string scope, string role, string name, JsonArray rules)
    {
        public string Scope { get; } = scope;

        public string Role { get; } = role;

        public string Name { get; } = name;

        public JsonArray Rules { get; } = rules;

        public string? ReportedScope { get; set; }

        public int Modified { get; set; }

        public string Path => Scope + Authorization + "/roleManagementPolicies/" + Name;

        public bool Is(string scope, string name)
        {
            return string.Equals(Scope.TrimEnd('/'), scope.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) && string.Equals(Name, name, StringComparison.OrdinalIgnoreCase);
        }

        public JsonObject ToJson()
        {
            return new JsonObject
            {
                ["properties"] = new JsonObject
                {
                    ["scope"] = ReportedScope ?? Scope,
                    ["displayName"] = "Default",
                    ["isOrganizationDefault"] = false,
                    ["lastModifiedDateTime"] = Modified == 0 ? null : new DateTimeOffset(2026, 10, 1, 0, 0, Modified, TimeSpan.Zero).ToString("O"),
                    ["rules"] = Rules.DeepClone(),
                },
                ["name"] = Name,
                ["type"] = "Microsoft.Authorization/RoleManagementPolicies",
                ["id"] = Path,
            };
        }

        public JsonObject Assignment()
        {
            return new JsonObject
            {
                ["properties"] = new JsonObject
                {
                    ["scope"] = Scope,
                    ["roleDefinitionId"] = RoleDefinitionId(Scope, Role),
                    ["policyId"] = Path,
                },
                ["name"] = Name + "_" + Role,
                ["type"] = "Microsoft.Authorization/RoleManagementPolicyAssignment",
                ["id"] = Scope + Authorization + "/roleManagementPolicyAssignments/" + Name + "_" + Role,
            };
        }
    }
}
