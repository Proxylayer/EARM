using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EArm.Graph;

namespace EArm.TestSupport;

/// <summary>
/// PIM role management policies in the fake tenant. <see cref="FakeDirectory"/> routes here:
/// <list type="bullet">
/// <item><c>GET /policies/roleManagementPolicyAssignments?$filter=scopeId eq '…' and scopeType eq '…' [and roleDefinitionId eq '…']</c> (the scope filter is
/// required, as Graph says), with <c>$expand=policy($expand=rules)</c>. A directory role (scopeType DirectoryRole or Directory, scopeId '/') has a policy
/// for every role id except <see cref="RolesWithoutPolicy"/>; a group (scopeType Group) has a member and an owner policy once it exists, and before PIM for
/// Groups onboards it only when <see cref="PoliciesBeforeOnboarding"/> (opt-in, as Learn implies: ids change on onboarding); by default, as a real tenant does (probe PP7),
/// the expanded list answers <c>404 ResourceNotFound</c> and the plain list by member role an assignment with a pre-onboarding policy id
/// (<see cref="PreOnboardingPolicyId"/>, not a policy), and the policy appears on onboarding with its new id.</item>
/// <item><c>GET /policies/roleManagementPolicies/{id}</c> (with <c>$expand=rules</c>).</item>
/// <item><c>PATCH /policies/roleManagementPolicies/{id}/rules/{ruleId}</c>: the body must carry the rule's <c>@odata.type</c> (400 otherwise); an unknown
/// policy or rule id is a 404; the top-level members of the body replace the rule's. A group policy PATCH onboards the group.</item>
/// </list>
/// Policies are seeded lazily with Microsoft's default rules (the Learn examples). When a group is onboarded (by a rule PATCH here, or by a schedule request of
/// <see cref="FakeGroupScheduleRequests"/>) its policies and assignments get new ids and keep their rules (Learn: "the IDs of the PIM policies and policy
/// assignments for the specific group change").
/// </summary>
public sealed class FakeRoleManagementPolicies
{
    public const string AssignmentsPath = "/policies/roleManagementPolicyAssignments";

    public const string PoliciesPath = "/policies/roleManagementPolicies";

    private static readonly Regex Clause = new(@"(\w+)\s+eq\s+'((?:[^']|'')*)'", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private readonly List<Policy> _policies = [];
    private readonly HashSet<string> _onboarded = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The tenant id the directory role policy ids carry.</summary>
    public string TenantId { get; init; } = "11111111-1111-1111-1111-111111111111";

    /// <summary>
    /// Whether a group's policies are listed before PIM for Groups onboards it (Learn implies they are; default false). When false, the assignment list of a group
    /// that is not onboarded answers <c>404 ResourceNotFound</c>, which is what a real tenant does.
    /// </summary>
    public bool PoliciesBeforeOnboarding { get; set; }

    /// <summary>Role ids Graph lists no policy for.</summary>
    public HashSet<string> RolesWithoutPolicy { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every rule PATCH accepted: the policy id, the rule id and the body.</summary>
    public List<(string PolicyId, string RuleId, JsonObject Body)> RulePatches { get; } = [];

    /// <summary>The groups onboarded by a policy update here.</summary>
    public IReadOnlyCollection<string> OnboardedGroups => _onboarded;

    internal static bool Handles(string path)
    {
        string normalized = "/" + path.Trim('/');
        return normalized.StartsWith(AssignmentsPath, StringComparison.OrdinalIgnoreCase) || normalized.StartsWith(PoliciesPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Handles one request.</summary>
    /// <param name="groupExists">True when a group with the id exists.</param>
    /// <param name="onboardedElsewhere">True when PIM for Groups onboarded the group through a schedule request.</param>
    internal (HttpStatusCode, JsonNode?) Handle(string method, string path, IReadOnlyDictionary<string, string>? query, JsonNode? body, RetryHint hint, Func<string, bool> groupExists, Func<string, bool> onboardedElsewhere)
    {
        var segments = path.Trim('/').Split('/').Select(Uri.UnescapeDataString).ToArray();
        string collection = "/" + string.Join('/', segments.Take(2));
        if (collection.Equals(AssignmentsPath, StringComparison.OrdinalIgnoreCase) && segments.Length == 2)
        {
            if (method != "GET")
            {
                throw FakeDirectory.MethodNotAllowed($"{method} is not supported on {AssignmentsPath}.", hint);
            }

            return (HttpStatusCode.OK, ListAssignments(query, hint, groupExists, onboardedElsewhere));
        }

        if (!collection.Equals(PoliciesPath, StringComparison.OrdinalIgnoreCase) || segments.Length < 3)
        {
            throw FakeDirectory.MethodNotAllowed($"{method} {path} is not supported by the fake.", hint);
        }

        var policy = _policies.FirstOrDefault(p => p.Id.Equals(segments[2], StringComparison.OrdinalIgnoreCase))
            ?? throw new GraphException(HttpStatusCode.NotFound, "ResourceNotFound", $"The policy '{segments[2]}' was not found.", hint);
        if (segments.Length == 3 && method == "GET")
        {
            return (HttpStatusCode.OK, policy.ToJson(Expand(query).Contains("rules", StringComparison.OrdinalIgnoreCase)));
        }

        if (segments.Length == 5 && segments[3].Equals("rules", StringComparison.OrdinalIgnoreCase) && method == "PATCH")
        {
            return (HttpStatusCode.OK, PatchRule(policy, segments[4], body, hint, onboardedElsewhere));
        }

        throw FakeDirectory.MethodNotAllowed($"{method} {path} is not supported by the fake.", hint);
    }

    /// <summary>The live policy of a scope (null when none is listed now), for assertions.</summary>
    public JsonObject? PolicyOf(string scopeType, string scopeId, string roleDefinitionId)
    {
        return _policies.FirstOrDefault(p => p.Matches(Kind(scopeType), scopeId, roleDefinitionId))?.ToJson(expandRules: true);
    }

    /// <summary>The live rule of a scope's policy, for assertions and seeding (changes to the returned object change the policy).</summary>
    public JsonObject Rule(string scopeType, string scopeId, string roleDefinitionId, string ruleId)
    {
        var policy = GetOrCreate(Kind(scopeType), scopeId, roleDefinitionId);
        return policy.Rules.OfType<JsonObject>().First(r => string.Equals((string?)r["id"], ruleId, StringComparison.OrdinalIgnoreCase));
    }

    private JsonObject ListAssignments(IReadOnlyDictionary<string, string>? query, RetryHint hint, Func<string, bool> groupExists, Func<string, bool> onboardedElsewhere)
    {
        string? filter = query is not null && query.TryGetValue("$filter", out var value) ? value : null;
        var clauses = filter is null ? [] : Clause.Matches(filter).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.Replace("''", "'", StringComparison.Ordinal), StringComparer.OrdinalIgnoreCase);
        if (!clauses.TryGetValue("scopeId", out var scopeId) || !clauses.TryGetValue("scopeType", out var scopeType))
        {
            throw FakeDirectory.BadRequest("The $filter must name scopeId and scopeType (eq) to list role management policy assignments.", hint);
        }

        clauses.TryGetValue("roleDefinitionId", out var roleDefinitionId);
        string kind = Kind(scopeType);
        bool expandPolicy = Expand(query).Contains("policy", StringComparison.OrdinalIgnoreCase);
        bool expandRules = Expand(query).Contains("rules", StringComparison.OrdinalIgnoreCase);
        var found = new List<Policy>();
        if (kind == "DirectoryRole")
        {
            if (scopeId != "/")
            {
                throw FakeDirectory.BadRequest("Directory role policies are scoped to '/'.", hint);
            }

            if (roleDefinitionId is not null && Guid.TryParse(roleDefinitionId, out _) && !RolesWithoutPolicy.Contains(roleDefinitionId))
            {
                found.Add(GetOrCreate(kind, scopeId, roleDefinitionId));
            }
        }
        else if (kind == "Group" && groupExists(scopeId))
        {
            Reconcile(scopeId, onboardedElsewhere);
            if (!PoliciesBeforeOnboarding && !IsOnboarded(scopeId, onboardedElsewhere))
            {
                if (!expandPolicy && roleDefinitionId is "member" or "owner")
                {
                    // Real tenant (probe PP7): the plain lookup by member role lists an assignment whose policy id is a pre-onboarding id, which is not
                    // a policy (GET and PATCH answer 404) and differs from the id the group's policy gets on onboarding.
                    return new JsonObject { ["value"] = new JsonArray(PreOnboardingAssignment(scopeId, roleDefinitionId)) };
                }

                // Graph answers the (expanded) assignment list of a group PIM for Groups has not onboarded with a 404, not an empty collection.
                throw new GraphException(HttpStatusCode.NotFound, "ResourceNotFound", $"The resource '{scopeId}' could not be found.", hint);
            }

            foreach (var access in roleDefinitionId is null ? new[] { "member", "owner" } : [roleDefinitionId])
            {
                if (access is "member" or "owner")
                {
                    found.Add(GetOrCreate(kind, scopeId, access));
                }
            }
        }

        return new JsonObject { ["value"] = new JsonArray(found.Select(p => (JsonNode?)p.Assignment(expandPolicy, expandRules)).ToArray()) };
    }

    /// <summary>
    /// The policy id the plain member-role assignment lookup exposes for a group before onboarding when <see cref="PoliciesBeforeOnboarding"/> is false
    /// (a real tenant): not a policy, and never the id of the group's policy after onboarding.
    /// </summary>
    public static string PreOnboardingPolicyId(string groupId, string accessId)
    {
        return "Group_" + groupId.ToLowerInvariant() + "_" + StableGuid("pre-onboarding", groupId) + "_" + accessId.ToLowerInvariant();
    }

    private static JsonObject PreOnboardingAssignment(string groupId, string accessId)
    {
        string policyId = PreOnboardingPolicyId(groupId, accessId);
        return new JsonObject
        {
            ["id"] = policyId + "_" + accessId.ToLowerInvariant(),
            ["policyId"] = policyId,
            ["scopeId"] = groupId.ToLowerInvariant(),
            ["scopeType"] = "Group",
            ["roleDefinitionId"] = accessId.ToLowerInvariant(),
        };
    }

    private JsonObject PatchRule(Policy policy, string ruleId, JsonNode? body, RetryHint hint, Func<string, bool> onboardedElsewhere)
    {
        var rule = policy.Rules.OfType<JsonObject>().FirstOrDefault(r => string.Equals((string?)r["id"], ruleId, StringComparison.OrdinalIgnoreCase))
            ?? throw new GraphException(HttpStatusCode.NotFound, "ResourceNotFound", $"The rule '{ruleId}' was not found in policy '{policy.Id}'.", hint);
        if (body is not JsonObject patch)
        {
            throw FakeDirectory.BadRequest("A JSON object body is required.", hint);
        }

        if (!string.Equals((string?)patch["@odata.type"], (string?)rule["@odata.type"], StringComparison.OrdinalIgnoreCase))
        {
            throw FakeDirectory.BadRequest($"The @odata.type of the rule is required and must be {(string?)rule["@odata.type"]}.", hint);
        }

        if (patch["id"] is JsonValue id && !string.Equals(id.GetValue<string>(), ruleId, StringComparison.OrdinalIgnoreCase))
        {
            throw FakeDirectory.BadRequest("The id in the body does not match the rule id of the URL.", hint);
        }

        RulePatches.Add((policy.Id, ruleId, (JsonObject)patch.DeepClone()));
        foreach (var property in patch)
        {
            if (property.Key is "@odata.type" or "id")
            {
                continue;
            }

            rule[property.Key] = Echo(property.Key, property.Value?.DeepClone());
        }

        if (policy.ScopeType == "Group" && !IsOnboarded(policy.ScopeId, onboardedElsewhere))
        {
            _onboarded.Add(policy.ScopeId);
            Reid(policy.ScopeId);
        }

        return (JsonObject)rule.DeepClone();
    }

    /// <summary>Graph echoes approvers with their display name in <c>description</c> (read-only) and without <c>isBackup</c> (accepted, never returned).</summary>
    private static JsonNode? Echo(string name, JsonNode? value)
    {
        if (name == "setting" && value is JsonObject setting && setting["approvalStages"] is JsonArray stages)
        {
            foreach (var stage in stages.OfType<JsonObject>())
            {
                foreach (var list in new[] { "primaryApprovers", "escalationApprovers" })
                {
                    foreach (var approver in (stage[list] as JsonArray)?.OfType<JsonObject>() ?? [])
                    {
                        approver["description"] = "approver " + ((string?)approver["userId"] ?? (string?)approver["groupId"]);
                        approver.Remove("isBackup");
                    }
                }
            }
        }

        return value;
    }

    private bool IsOnboarded(string groupId, Func<string, bool> onboardedElsewhere)
    {
        return _onboarded.Contains(groupId) || onboardedElsewhere(groupId);
    }

    /// <summary>A group a schedule request onboarded since the last look gets its new policy ids.</summary>
    private void Reconcile(string groupId, Func<string, bool> onboardedElsewhere)
    {
        if (!_onboarded.Contains(groupId) && onboardedElsewhere(groupId))
        {
            _onboarded.Add(groupId);
            Reid(groupId);
        }
    }

    private void Reid(string groupId)
    {
        string suffix = StableGuid("onboarded", groupId);
        foreach (var policy in _policies.Where(p => p.ScopeType == "Group" && p.ScopeId.Equals(groupId, StringComparison.OrdinalIgnoreCase)))
        {
            policy.Id = "Group_" + groupId + "_" + suffix + "_" + policy.RoleDefinitionId;
        }
    }

    private Policy GetOrCreate(string kind, string scopeId, string roleDefinitionId)
    {
        var policy = _policies.FirstOrDefault(p => p.Matches(kind, scopeId, roleDefinitionId));
        if (policy is null)
        {
            // Without policies before onboarding (a real tenant) a group's policy first appears on onboarding, with its post-onboarding id.
            string groupSuffix = !PoliciesBeforeOnboarding && _onboarded.Contains(scopeId) ? StableGuid("onboarded", scopeId) : StableGuid("group", scopeId);
            string id = kind == "Group"
                ? "Group_" + scopeId.ToLowerInvariant() + "_" + groupSuffix + "_" + roleDefinitionId.ToLowerInvariant()
                : "DirectoryRole_" + TenantId + "_" + StableGuid("role", roleDefinitionId);
            policy = new Policy(id, kind, kind == "Group" ? scopeId.ToLowerInvariant() : "/", roleDefinitionId.ToLowerInvariant(), DefaultRules(kind == "Group"));
            _policies.Add(policy);
        }

        return policy;
    }

    /// <summary>A deterministic id (what-if goldens print policy ids).</summary>
    private static string StableGuid(string kind, string value)
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(kind + "|" + value.ToLowerInvariant()));
        return new Guid(hash.AsSpan(0, 16)).ToString("D");
    }

    private static string Kind(string scopeType)
    {
        return scopeType.Equals("Group", StringComparison.OrdinalIgnoreCase) ? "Group" : "DirectoryRole";
    }

    private static string Expand(IReadOnlyDictionary<string, string>? query)
    {
        return query is not null && query.TryGetValue("$expand", out var value) ? value : string.Empty;
    }

    /// <summary>Microsoft's default rules (Learn: policyroot-list-rolemanagementpolicyassignments examples).</summary>
    public static JsonArray DefaultRules(bool group)
    {
        string all = group ? "All" : "all";
        JsonObject Target(string caller, string level) => new()
        {
            ["caller"] = caller,
            ["operations"] = new JsonArray(all),
            ["level"] = level,
            ["inheritableSettings"] = new JsonArray(),
            ["enforcedSettings"] = new JsonArray(),
        };
        JsonObject Expiration(string caller, string level, bool required, string max) => new()
        {
            ["@odata.type"] = "#microsoft.graph.unifiedRoleManagementPolicyExpirationRule",
            ["id"] = $"Expiration_{caller}_{level}",
            ["isExpirationRequired"] = required,
            ["maximumDuration"] = max,
            ["target"] = Target(caller, level),
        };
        JsonObject Enablement(string caller, string level, params string[] rules) => new()
        {
            ["@odata.type"] = "#microsoft.graph.unifiedRoleManagementPolicyEnablementRule",
            ["id"] = $"Enablement_{caller}_{level}",
            ["enabledRules"] = new JsonArray(rules.Select(r => (JsonNode?)r).ToArray()),
            ["target"] = Target(caller, level),
        };
        IEnumerable<JsonObject> Notifications(string caller, string level) => new[] { "Admin", "Requestor", "Approver" }.Select(recipient => new JsonObject
        {
            ["@odata.type"] = "#microsoft.graph.unifiedRoleManagementPolicyNotificationRule",
            ["id"] = $"Notification_{recipient}_{caller}_{level}",
            ["notificationType"] = "Email",
            ["recipientType"] = recipient,
            ["notificationLevel"] = "All",
            ["isDefaultRecipientsEnabled"] = true,
            ["notificationRecipients"] = new JsonArray(),
            ["target"] = Target(caller, level),
        });

        var rules = new List<JsonObject>
        {
            Expiration("Admin", "Eligibility", group, "P365D"),
            Enablement("Admin", "Eligibility"),
        };
        rules.AddRange(Notifications("Admin", "Eligibility"));
        rules.Add(Expiration("Admin", "Assignment", group, "P180D"));
        rules.Add(Enablement("Admin", "Assignment", "Justification"));
        rules.AddRange(Notifications("Admin", "Assignment"));
        rules.Add(Expiration("EndUser", "Assignment", true, "PT8H"));
        rules.Add(Enablement("EndUser", "Assignment", "MultiFactorAuthentication", "Justification"));
        rules.Add(new JsonObject
        {
            ["@odata.type"] = "#microsoft.graph.unifiedRoleManagementPolicyApprovalRule",
            ["id"] = "Approval_EndUser_Assignment",
            ["target"] = Target("EndUser", "Assignment"),
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
                    ["isEscalationEnabled"] = false,
                    ["primaryApprovers"] = new JsonArray(),
                    ["escalationApprovers"] = new JsonArray(),
                }),
            },
        });
        rules.Add(new JsonObject
        {
            ["@odata.type"] = "#microsoft.graph.unifiedRoleManagementPolicyAuthenticationContextRule",
            ["id"] = "AuthenticationContext_EndUser_Assignment",
            ["isEnabled"] = false,
            ["claimValue"] = null,
            ["target"] = Target("EndUser", "Assignment"),
        });
        rules.AddRange(Notifications("EndUser", "Assignment"));
        return new JsonArray(rules.Select(r => (JsonNode?)r).ToArray());
    }

    private sealed class Policy(string id, string scopeType, string scopeId, string roleDefinitionId, JsonArray rules)
    {
        public string Id { get; set; } = id;

        public string ScopeType { get; } = scopeType;

        public string ScopeId { get; } = scopeId;

        public string RoleDefinitionId { get; } = roleDefinitionId;

        public JsonArray Rules { get; } = rules;

        public bool Matches(string kind, string scopeId, string roleDefinitionId)
        {
            return ScopeType == kind && ScopeId.Equals(kind == "Group" ? scopeId : "/", StringComparison.OrdinalIgnoreCase) && RoleDefinitionId.Equals(roleDefinitionId, StringComparison.OrdinalIgnoreCase);
        }

        public JsonObject ToJson(bool expandRules)
        {
            var json = new JsonObject
            {
                ["id"] = Id,
                ["displayName"] = ScopeType,
                ["description"] = ScopeType,
                ["isOrganizationDefault"] = false,
                ["scopeId"] = ScopeId,
                ["scopeType"] = ScopeType,
                ["lastModifiedDateTime"] = null,
            };
            if (expandRules)
            {
                json["rules"] = Rules.DeepClone();
            }

            return json;
        }

        public JsonObject Assignment(bool expandPolicy, bool expandRules)
        {
            var json = new JsonObject
            {
                ["id"] = Id + "_" + RoleDefinitionId,
                ["policyId"] = Id,
                ["scopeId"] = ScopeId,
                ["scopeType"] = ScopeType,
                ["roleDefinitionId"] = RoleDefinitionId,
            };
            if (expandPolicy)
            {
                json["policy"] = ToJson(expandRules);
            }

            return json;
        }
    }
}
