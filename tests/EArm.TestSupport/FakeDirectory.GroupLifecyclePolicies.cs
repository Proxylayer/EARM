using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using EArm.Graph;

namespace EArm.TestSupport;

/// <summary>
/// Group lifecycle (expiration) policies, as Learn documents them (https://learn.microsoft.com/en-us/graph/api/resources/grouplifecyclepolicy?view=graph-rest-1.0):
/// one policy per tenant (a second POST is a 400), PATCH answers 200 with the updated policy, <c>addGroup</c> / <c>removeGroup</c> answer
/// <c>{ "value": true | false }</c> and are refused unless <c>managedGroupTypes</c> is <c>Selected</c>, only Microsoft 365 (Unified) groups can be
/// added, and <c>GET /groups/{id}/groupLifecyclePolicies</c> lists the policy governing the group (every Unified group under <c>All</c>, the added
/// groups under <c>Selected</c>).
/// </summary>
public sealed partial class FakeDirectory
{
    /// <summary>The collection of the group lifecycle policies.</summary>
    public const string GroupLifecyclePoliciesPath = "/groupLifecyclePolicies";

    private readonly Dictionary<string, HashSet<string>> _lifecyclePolicyGroups = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The ids of the groups added to a <c>Selected</c> policy.</summary>
    public IReadOnlyList<string> LifecyclePolicyGroups(string policyId)
    {
        lock (_gate)
        {
            return _lifecyclePolicyGroups.TryGetValue(policyId, out var groups) ? [.. groups.Order(StringComparer.Ordinal)] : [];
        }
    }

    private bool TryHandleGroupLifecyclePolicies(string method, string[] segments, IReadOnlyDictionary<string, string>? query, JsonNode? body, RetryHint hint, int pageSize, out (HttpStatusCode, JsonNode?) result)
    {
        result = default;
        if (segments.Length == 3 && StartsWith(segments, "groups") && segments[2].Equals("groupLifecyclePolicies", StringComparison.OrdinalIgnoreCase))
        {
            result = method == "GET" ? (HttpStatusCode.OK, ListOf(PoliciesOfGroup(segments[1], hint), "groups('" + segments[1] + "')/groupLifecyclePolicies")) : throw MethodNotAllowed($"{method} is not supported on a group's groupLifecyclePolicies.", hint);
            return true;
        }

        if (!StartsWith(segments, "groupLifecyclePolicies"))
        {
            return false;
        }

        var rest = segments[1..];
        if (rest.Length == 2 && method == "POST")
        {
            result = (HttpStatusCode.OK, LifecycleAction(rest[0], rest[1], body, hint));
            return true;
        }

        if (rest.Length == 1 && method == "PATCH")
        {
            HandlePolicyCollection(GroupLifecyclePoliciesPath, method, rest, query, body, hint, pageSize);
            result = (HttpStatusCode.OK, Collection(GroupLifecyclePoliciesPath).First(p => ValueEquals(GetString(p, "id"), rest[0])).DeepClone());
            return true;
        }

        if (rest.Length == 1 && method == "DELETE")
        {
            _lifecyclePolicyGroups.Remove(rest[0]);
        }

        result = HandlePolicyCollection(GroupLifecyclePoliciesPath, method, rest, query, body, hint, pageSize, created =>
        {
            if (Collection(GroupLifecyclePoliciesPath).Count > 0)
            {
                throw BadRequest("Only one group lifecycle policy can exist in the tenant.", hint);
            }

            if (created["groupLifetimeInDays"] is null || created["managedGroupTypes"] is null)
            {
                throw BadRequest("groupLifetimeInDays and managedGroupTypes are required.", hint);
            }
        });
        return true;
    }

    private JsonObject LifecycleAction(string policyId, string action, JsonNode? body, RetryHint hint)
    {
        var policy = Collection(GroupLifecyclePoliciesPath).FirstOrDefault(p => ValueEquals(GetString(p, "id"), policyId)) ?? throw NotFound(GroupLifecyclePoliciesPath + "/" + policyId, hint);
        string groupId = (body as JsonObject) is { } payload ? GetString(payload, "groupId") ?? throw BadRequest("groupId is required.", hint) : throw BadRequest("groupId is required.", hint);
        if (!"Selected".Equals(GetString(policy, "managedGroupTypes"), StringComparison.OrdinalIgnoreCase))
        {
            throw BadRequest("Groups can be added to or removed from a lifecycle policy only when managedGroupTypes is Selected.", hint);
        }

        var group = Collection("/groups").FirstOrDefault(g => ValueEquals(GetString(g, "id"), groupId)) ?? throw NotFound("/groups/" + groupId, hint);
        if (!IsUnified(group))
        {
            throw BadRequest("Only Microsoft 365 groups can be added to a group lifecycle policy.", hint);
        }

        if (!_lifecyclePolicyGroups.TryGetValue(policyId, out var groups))
        {
            groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _lifecyclePolicyGroups[policyId] = groups;
        }

        bool changed = action.ToLowerInvariant() switch
        {
            "addgroup" => groups.Add(groupId),
            "removegroup" => groups.Remove(groupId),
            _ => throw NotFound(GroupLifecyclePoliciesPath + "/" + policyId + "/" + action, hint),
        };
        return new JsonObject { ["@odata.context"] = "https://graph.microsoft.com/v1.0/$metadata#Edm.Boolean", ["value"] = changed };
    }

    private List<JsonObject> PoliciesOfGroup(string groupId, RetryHint hint)
    {
        var group = Collection("/groups").FirstOrDefault(g => ValueEquals(GetString(g, "id"), groupId)) ?? throw NotFound("/groups/" + groupId, hint);
        return Collection(GroupLifecyclePoliciesPath).Where(p => GetString(p, "managedGroupTypes")?.ToLowerInvariant() switch
        {
            "all" => IsUnified(group),
            "selected" => _lifecyclePolicyGroups.TryGetValue(GetString(p, "id")!, out var groups) && groups.Contains(groupId),
            _ => false,
        }).ToList();
    }
}
