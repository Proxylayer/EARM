using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using EArm.Graph;

namespace EArm.TestSupport;

/// <summary>
/// The application policies (token lifetime, claims mapping and app management policies) and their <c>$ref</c> assignments to applications and service
/// principals, as Learn documents them: the policy collections are plain CRUD (POST needs <c>displayName</c> and, for the STS policies, <c>definition</c>);
/// <c>POST /{applications|servicePrincipals}/{id}/{navigation}/$ref</c> assigns (204; an existing assignment is Graph's 400 "already exist"; a second
/// token lifetime or app management policy is a 400, Graph allows one), <c>GET …/{navigation}</c> lists the assigned policies,
/// <c>DELETE …/{navigation}/{policyId}/$ref</c> unassigns, and <c>GET /policies/{collection}/{id}/appliesTo</c> lists the principals.
/// </summary>
public sealed partial class FakeDirectory
{
    public const string TokenLifetimePoliciesPath = "/policies/tokenLifetimePolicies";

    public const string ClaimsMappingPoliciesPath = "/policies/claimsMappingPolicies";

    public const string AppManagementPoliciesPath = "/policies/appManagementPolicies";

    /// <summary>navigation → (policy collection, principal collections, one per principal).</summary>
    private static readonly Dictionary<string, (string Collection, string[] Principals, bool OnePerPrincipal)> PolicyNavigations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tokenLifetimePolicies"] = (TokenLifetimePoliciesPath, ["applications", "servicePrincipals"], true),
        ["claimsMappingPolicies"] = (ClaimsMappingPoliciesPath, ["servicePrincipals"], false),
        ["appManagementPolicies"] = (AppManagementPoliciesPath, ["applications", "servicePrincipals"], true),
    };

    /// <summary>"{principalId}|{navigation}" → assigned policy ids.</summary>
    private readonly Dictionary<string, List<string>> _policyAssignments = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The ids of the policies assigned to a principal through <paramref name="navigation"/>.</summary>
    public IReadOnlyList<string> AssignedPolicies(string principalId, string navigation)
    {
        lock (_gate)
        {
            return _policyAssignments.TryGetValue(principalId + "|" + navigation, out var ids) ? [.. ids] : [];
        }
    }

    /// <summary>Seeds an assignment (no checks).</summary>
    public void AssignPolicy(string principalId, string navigation, string policyId)
    {
        lock (_gate)
        {
            AssignmentList(principalId, navigation).Add(policyId);
        }
    }

    private bool TryHandlePolicyAssignments(string method, string[] segments, IReadOnlyDictionary<string, string>? query, JsonNode? body, RetryHint hint, int pageSize, out (HttpStatusCode, JsonNode?) result)
    {
        result = default;
        if (segments.Length >= 2 && segments[0].Equals("policies", StringComparison.OrdinalIgnoreCase)
            && PolicyNavigations.Values.FirstOrDefault(n => n.Collection.Equals("/policies/" + segments[1], StringComparison.OrdinalIgnoreCase)) is { Collection: not null } policy)
        {
            var rest = segments[2..];
            if (rest.Length == 2 && rest[1].Equals("appliesTo", StringComparison.OrdinalIgnoreCase) && method == "GET")
            {
                var principals = _policyAssignments.Where(p => p.Value.Any(id => ValueEquals(id, rest[0])) && PolicyNavigations[p.Key.Split('|')[1]].Collection == policy.Collection)
                    .Select(p => LocateById(p.Key.Split('|')[0], out _)).OfType<JsonObject>().Select(WithODataType);
                result = (HttpStatusCode.OK, ListOf(principals, "directoryObjects"));
                return true;
            }

            if (rest.Length == 1 && method == "DELETE")
            {
                foreach (var list in _policyAssignments.Values)
                {
                    list.RemoveAll(id => ValueEquals(id, rest[0]));
                }
            }

            result = HandlePolicyCollection(policy.Collection, method, rest, query, body, hint, pageSize, created =>
            {
                if (GetString(created, "displayName") is null)
                {
                    throw BadRequest("displayName is required.", hint);
                }

                if (policy.Collection != AppManagementPoliciesPath && created["definition"] is not JsonArray)
                {
                    throw BadRequest("definition is required.", hint);
                }

                if (policy.Collection == AppManagementPoliciesPath && GetString(created, "description") is null)
                {
                    throw BadRequest("description is required.", hint);
                }
            });
            return true;
        }

        if (segments.Length < 3 || !PolicyNavigations.TryGetValue(segments[2], out var navigation)
            || !navigation.Principals.Contains(segments[0], StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        string principalId = segments[1];
        if (Collection("/" + segments[0]).FirstOrDefault(o => ValueEquals(GetString(o, "id"), principalId)) is null)
        {
            throw NotFound("/" + string.Join('/', segments), hint);
        }

        var assigned = AssignmentList(principalId, segments[2]);
        var policies = Collection(navigation.Collection);
        switch (method, segments.Length)
        {
            case ("GET", 3):
                result = (HttpStatusCode.OK, ListOf(policies.Where(p => assigned.Any(id => ValueEquals(id, GetString(p, "id")))), segments[0] + "/" + segments[2]));
                return true;
            case ("POST", 4) when segments[3] == "$ref":
                string target = (body as JsonObject) is { } payload && GetString(payload, "@odata.id") is { } odataId ? odataId.TrimEnd('/').Split('/')[^1] : throw BadRequest("@odata.id is required.", hint);
                if (!policies.Any(p => ValueEquals(GetString(p, "id"), target)))
                {
                    throw BadRequest($"Policy '{target}' does not exist.", hint);
                }

                if (assigned.Any(id => ValueEquals(id, target)))
                {
                    throw BadRequest("One or more added object references already exist for the following modified properties: '" + segments[2] + "'.", hint);
                }

                if (navigation.OnePerPrincipal && assigned.Count > 0)
                {
                    throw BadRequest($"Only one {segments[2]} policy can be assigned to the object.", hint);
                }

                assigned.Add(target);
                result = (HttpStatusCode.NoContent, null);
                return true;
            case ("DELETE", 5) when segments[4] == "$ref":
                result = assigned.RemoveAll(id => ValueEquals(id, segments[3])) > 0 ? (HttpStatusCode.NoContent, null) : throw NotFound("/" + string.Join('/', segments), hint);
                return true;
            default:
                throw MethodNotAllowed($"{method} is not supported on '/{string.Join('/', segments)}'.", hint);
        }
    }

    private List<string> AssignmentList(string principalId, string navigation)
    {
        string key = principalId + "|" + navigation;
        if (!_policyAssignments.TryGetValue(key, out var list))
        {
            list = [];
            _policyAssignments[key] = list;
        }

        return list;
    }
}
