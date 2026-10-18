using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EArm.Graph;

namespace EArm.TestSupport;

/// <summary>
/// Entitlement management (<c>/identityGovernance/entitlementManagement</c>) in the fake tenant. <see cref="FakeDirectory"/> routes here:
/// <list type="bullet">
/// <item><c>catalogs</c>: list (<c>$filter=displayName eq</c>), get, POST, PATCH, DELETE (400 while the catalog holds packages);
/// <c>catalogs/{id}/resources</c> (<c>$filter=originId eq</c>, 404 for an unknown catalog) and <c>catalogs/{id}/resourceRoles</c> (Member and Owner for every
/// group resource, plus Eligible Member and Eligible Owner for a group PIM for Groups onboarded).</item>
/// <item><c>resourceRequests</c>: POST <c>adminAdd</c> of a group, an application or a site (FakeEntitlementManagement.Origins.cs; the group must exist; a
/// role-assignable group is a 403 unless <see cref="CallerMayAddRoleAssignableGroups"/>;
/// the new resource is left out of the next <see cref="ResourceListLag"/> resource lists; <see cref="NextRequestState"/> overrides the state) and
/// <c>adminRemove</c> (400 while a package grants a role of the resource).</item>
/// <item><c>accessPackages</c>: list (<c>$filter=displayName eq</c>), get (<c>$expand=catalog</c>, <c>resourceRoleScopes($expand=role,scope)</c>), POST (needs
/// <c>catalog.id</c>), PATCH (<c>catalog</c> refused), DELETE (400 while it has assignments); <c>accessPackages/{id}/resourceRoleScopes</c> POST and DELETE.</item>
/// <item><c>assignmentPolicies</c>: list (<c>$filter=accessPackage/id eq</c>), get, POST, PUT (a full replace: <c>displayName</c> and <c>accessPackage.id</c>
/// required, no PATCH), DELETE (400 while it has assignments). Graph fills in every stage field and a subject's read-only <c>description</c>, and so does
/// this fake.</item>
/// </list>
/// Assignments are seeded with <see cref="AddAssignment"/>.
/// </summary>
public sealed partial class FakeEntitlementManagement
{
    public const string Root = "/identityGovernance/entitlementManagement";

    private static readonly Regex Clause = new(@"([\w/]+)\s+eq\s+'((?:[^']|'')*)'", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private readonly List<JsonObject> _catalogs = [];
    private readonly List<JsonObject> _resources = [];
    private readonly List<JsonObject> _packages = [];
    private readonly List<JsonObject> _roleScopes = [];
    private readonly List<JsonObject> _policies = [];
    private readonly List<(string PackageId, string? PolicyId)> _assignments = [];
    private readonly Dictionary<string, int> _hiddenResources = new(StringComparer.OrdinalIgnoreCase);
    private Func<string, bool> _onboardedElsewhere = _ => false;
    private Func<string, JsonObject?> _findServicePrincipal = _ => null;

    /// <summary>Whether the caller holds Global Administrator / Privileged Role Administrator (adding a role-assignable group needs it).</summary>
    public bool CallerMayAddRoleAssignableGroups { get; set; } = true;

    /// <summary>How many resource lists of a catalog leave out a resource just requested (the request is processed asynchronously).</summary>
    public int ResourceListLag { get; set; }

    /// <summary>The state the next resource request answers with (<c>delivered</c> when null); reset after use.</summary>
    public string? NextRequestState { get; set; }

    /// <summary>
    /// How many more resource role lists leave out the eligible roles of a group onboarded by a schedule request (PIM processes the onboarding
    /// asynchronously); counted down by every such list. A negative value hides them for good.
    /// </summary>
    public int EligibleRolesHiddenReads { get; set; }

    /// <summary>When true, the assignment list answers 403 (the caller may not read assignments).</summary>
    public bool AssignmentsForbidden { get; set; }

    /// <summary>The groups PIM for Groups manages (besides those onboarded by the schedule request fakes).</summary>
    public HashSet<string> OnboardedGroups { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<JsonObject> Catalogs => _catalogs;

    public IReadOnlyList<JsonObject> Resources => _resources;

    public IReadOnlyList<JsonObject> Packages => _packages;

    public IReadOnlyList<JsonObject> RoleScopes => _roleScopes;

    public IReadOnlyList<JsonObject> Policies => _policies;

    /// <summary>Seeds an assignment of a package (and policy): the package and policy can no longer be deleted.</summary>
    public void AddAssignment(string packageId, string? policyId = null)
    {
        _assignments.Add((packageId, policyId));
    }

    /// <summary>Seeds a catalog (for example a second one with the same display name).</summary>
    public JsonObject AddCatalog(string displayName)
    {
        var catalog = new JsonObject { ["id"] = NewId(), ["displayName"] = displayName, ["state"] = "published", ["catalogType"] = "userManaged", ["isExternallyVisible"] = true };
        _catalogs.Add(catalog);
        return catalog;
    }

    internal static bool Handles(string path)
    {
        return ("/" + path.Trim('/')).StartsWith(Root + "/", StringComparison.OrdinalIgnoreCase);
    }

    /// <param name="findGroup">The group with the id, or null.</param>
    /// <param name="onboardedElsewhere">True when PIM for Groups onboarded the group through a schedule request.</param>
    /// <param name="findServicePrincipal">The service principal with the id, or null.</param>
    internal (HttpStatusCode, JsonNode?) Handle(string method, string path, IReadOnlyDictionary<string, string>? query, JsonNode? body, RetryHint hint, Func<string, JsonObject?> findGroup, Func<string, bool> onboardedElsewhere, Func<string, JsonObject?> findServicePrincipal)
    {
        _onboardedElsewhere = onboardedElsewhere;
        _findServicePrincipal = findServicePrincipal;
        var s = ("/" + path.Trim('/'))[Root.Length..].Trim('/').Split('/').Select(Uri.UnescapeDataString).ToArray();
        var filter = Filter(query);
        string expand = query is not null && query.TryGetValue("$expand", out var e) ? e : string.Empty;
        switch (s[0])
        {
            case "catalogs" when s.Length == 1:
                return method switch
                {
                    "GET" => List(_catalogs.Where(c => Matches(c, filter))),
                    "POST" => Created(AddWithId(_catalogs, Body(body, hint), c => c["catalogType"] = "userManaged")),
                    _ => throw FakeDirectory.MethodNotAllowed($"{method} on catalogs", hint),
                };
            case "catalogs" when s.Length == 2:
                return Entity(method, _catalogs, s[1], body, hint, onDelete: catalog =>
                {
                    if (_packages.Any(p => Same(CatalogOf(p), s[1])))
                    {
                        throw FakeDirectory.BadRequest("The catalog cannot be deleted because it has access packages.", hint);
                    }

                    _resources.RemoveAll(r => Same(Str(r, "catalogId"), s[1]));
                });
            case "catalogs" when s.Length == 3 && s[2] == "resources":
                RequireGet(method, hint);
                if (Find(_catalogs, s[1]) is null)
                {
                    throw FakeDirectory.NotFound(path, hint);
                }

                return List(_resources.Where(r => Same(Str(r, "catalogId"), s[1]) && Matches(r, filter) && Visible(r)).Select(Public));
            case "catalogs" when s.Length == 3 && s[2] == "resourceRoles":
                RequireGet(method, hint);
                string resourceId = filter.TryGetValue("resource/id", out var rid) ? rid : throw FakeDirectory.BadRequest("resource/id filter required", hint);
                var resource = _resources.FirstOrDefault(r => Same(Str(r, "id"), resourceId) && Same(Str(r, "catalogId"), s[1]));
                bool hideEligible = EligibleRolesHiddenReads != 0;
                if (EligibleRolesHiddenReads > 0)
                {
                    EligibleRolesHiddenReads--;
                }

                return List(resource is null ? [] : RolesOf(resource, hideEligible ? _ => false : onboardedElsewhere));
            case "assignments" when s.Length == 1 && method == "GET":
                if (AssignmentsForbidden)
                {
                    throw new GraphException(HttpStatusCode.Forbidden, "Forbidden", "The caller may not read assignments.", hint);
                }

                string assignedPackage = filter.TryGetValue("accessPackage/id", out var ap) ? ap : throw FakeDirectory.BadRequest("accessPackage/id filter required", hint);
                return List(_assignments.Where(a => Same(a.PackageId, assignedPackage)).Select(a => new JsonObject { ["id"] = NewId(), ["state"] = "delivered" }));
            case "resourceRequests" when s.Length == 1 && method == "POST":
                return ResourceRequest(Body(body, hint), hint, findGroup);
            case "accessPackages" when s.Length == 1:
                return method switch
                {
                    "GET" => List(_packages.Where(p => Matches(p, filter)).Select(p => Expand(p, expand))),
                    "POST" => CreatePackage(Body(body, hint), hint),
                    _ => throw FakeDirectory.MethodNotAllowed($"{method} on accessPackages", hint),
                };
            case "accessPackages" when s.Length == 2:
                if (method == "GET")
                {
                    return Find(_packages, s[1]) is { } package ? (HttpStatusCode.OK, Expand(package, expand)) : throw FakeDirectory.NotFound(path, hint);
                }

                if (method == "PATCH" && Body(body, hint).ContainsKey("catalog"))
                {
                    throw FakeDirectory.BadRequest("The catalog of an access package cannot be changed.", hint);
                }

                return Entity(method, _packages, s[1], body, hint, onDelete: _ =>
                {
                    if (_assignments.Any(a => Same(a.PackageId, s[1])))
                    {
                        throw FakeDirectory.BadRequest("Access package cannot be deleted because it has active assignments.", hint);
                    }

                    _roleScopes.RemoveAll(r => Same(Str(r, "accessPackageId"), s[1]));
                });
            case "accessPackages" when s.Length >= 3 && s[2] == "resourceRoleScopes":
                return HandleRoleScopes(method, s[1], s.Length == 4 ? s[3] : null, body, path, hint);
            case "assignmentPolicies" when s.Length == 1:
                return method switch
                {
                    "GET" => List(_policies.Where(p => Matches(p, filter)).Select(Public)),
                    "POST" => Created(SavePolicy(null, Body(body, hint), hint)),
                    _ => throw FakeDirectory.MethodNotAllowed($"{method} on assignmentPolicies", hint),
                };
            case "assignmentPolicies" when s.Length == 2:
                switch (method)
                {
                    case "GET":
                        return Find(_policies, s[1]) is { } policy ? (HttpStatusCode.OK, Public(policy)) : throw FakeDirectory.NotFound(path, hint);
                    case "PUT":
                        _ = Find(_policies, s[1]) ?? throw FakeDirectory.NotFound(path, hint);
                        return (HttpStatusCode.OK, Public(SavePolicy(s[1], Body(body, hint), hint)));
                    case "DELETE":
                        _ = Find(_policies, s[1]) ?? throw FakeDirectory.NotFound(path, hint);
                        if (_assignments.Any(a => Same(a.PolicyId, s[1])))
                        {
                            throw FakeDirectory.BadRequest("The policy cannot be deleted because it has active assignments.", hint);
                        }

                        _policies.RemoveAll(p => Same(Str(p, "id"), s[1]));
                        return (HttpStatusCode.NoContent, null);
                    default:
                        throw FakeDirectory.MethodNotAllowed($"{method} on an assignment policy (Graph offers PUT only)", hint);
                }

            default:
                throw FakeDirectory.NotFound(path, hint);
        }
    }

    // ---- resource requests ----

    private (HttpStatusCode, JsonNode?) ResourceRequest(JsonObject body, RetryHint hint, Func<string, JsonObject?> findGroup)
    {
        string type = Str(body, "requestType") ?? string.Empty;
        string catalogId = Str(body["catalog"] as JsonObject, "id") ?? throw FakeDirectory.BadRequest("catalog.id is required", hint);
        _ = Find(_catalogs, catalogId) ?? throw FakeDirectory.NotFound("catalog " + catalogId, hint);
        var resource = body["resource"] as JsonObject ?? throw FakeDirectory.BadRequest("resource is required", hint);
        string state = NextRequestState ?? "delivered";
        NextRequestState = null;
        if (type == "adminAdd")
        {
            string originId = Str(resource, "originId") ?? throw FakeDirectory.BadRequest("resource.originId is required", hint);
            string originSystem = Str(resource, "originSystem") ?? "AadGroup";
            var origin = ResolveOrigin(originSystem, originId, findGroup, hint);

            if (_resources.Any(r => Same(Str(r, "catalogId"), catalogId) && Same(Str(r, "originId"), originId)))
            {
                throw FakeDirectory.BadRequest("The resource already exists in the catalog.", hint);
            }

            if (state == "delivered")
            {
                var added = new JsonObject
                {
                    ["id"] = NewId(), ["catalogId"] = catalogId, ["originId"] = originId, ["originSystem"] = originSystem,
                    ["displayName"] = Str(origin, "displayName"), ["description"] = Str(origin, "description"),
                };
                _resources.Add(added);
                if (ResourceListLag > 0)
                {
                    _hiddenResources[Str(added, "id")!] = ResourceListLag;
                }
            }
        }
        else if (type == "adminRemove")
        {
            string id = Str(resource, "id") ?? throw FakeDirectory.BadRequest("resource.id is required", hint);
            var existing = _resources.FirstOrDefault(r => Same(Str(r, "id"), id)) ?? throw FakeDirectory.NotFound("resource " + id, hint);
            if (_roleScopes.Any(r => Same(Str(r["role"]?["resource"] as JsonObject, "id"), id)))
            {
                throw FakeDirectory.BadRequest("The resource is used by an access package of the catalog.", hint);
            }

            _resources.Remove(existing);
        }
        else
        {
            throw FakeDirectory.BadRequest("Unsupported requestType " + type, hint);
        }

        return (HttpStatusCode.Created, new JsonObject { ["id"] = NewId(), ["requestType"] = type, ["state"] = state });
    }

    private IEnumerable<JsonObject> RolesOf(JsonObject resource, Func<string, bool> onboardedElsewhere)
    {
        if (!string.Equals(Str(resource, "originSystem") ?? "AadGroup", "AadGroup", StringComparison.OrdinalIgnoreCase))
        {
            return OriginRolesOf(resource);
        }

        string groupId = Str(resource, "originId")!;
        var roles = new List<(string Name, string Prefix)> { ("Member", "Member_"), ("Owner", "Owner_") };
        if (OnboardedGroups.Contains(groupId) || onboardedElsewhere(groupId))
        {
            roles.Add(("Eligible Member", "EligibleMember_"));
            roles.Add(("Eligible Owner", "EligibleOwner_"));
        }

        return roles.Select(r => new JsonObject
        {
            ["displayName"] = r.Name, ["originId"] = r.Prefix + groupId, ["originSystem"] = "AadGroup",
            ["resource"] = new JsonObject { ["id"] = Str(resource, "id"), ["originId"] = groupId, ["originSystem"] = "AadGroup" },
        });
    }

    // ---- packages and role scopes ----

    private (HttpStatusCode, JsonNode?) CreatePackage(JsonObject body, RetryHint hint)
    {
        string catalogId = Str(body["catalog"] as JsonObject, "id") ?? throw FakeDirectory.BadRequest("catalog.id is required to create an access package", hint);
        _ = Find(_catalogs, catalogId) ?? throw FakeDirectory.NotFound("catalog " + catalogId, hint);
        if (body.ContainsKey("catalogId"))
        {
            throw FakeDirectory.BadRequest("Property 'catalogId' does not exist on accessPackage.", hint);
        }

        var package = AddWithId(_packages, body, p => p["catalog"] = new JsonObject { ["id"] = catalogId });
        return (HttpStatusCode.Created, Public(package));
    }

    private (HttpStatusCode, JsonNode?) HandleRoleScopes(string method, string packageId, string? scopeId, JsonNode? body, string path, RetryHint hint)
    {
        var package = Find(_packages, packageId) ?? throw FakeDirectory.NotFound(path, hint);
        if (method == "POST" && scopeId is null)
        {
            var request = Body(body, hint);
            var role = request["role"] as JsonObject ?? throw FakeDirectory.BadRequest("role is required", hint);
            string resourceId = Str(role["resource"] as JsonObject, "id") ?? throw FakeDirectory.BadRequest("role.resource.id is required", hint);
            var resource = _resources.FirstOrDefault(r => Same(Str(r, "id"), resourceId) && Same(Str(r, "catalogId"), CatalogOf(package)))
                ?? throw FakeDirectory.BadRequest("The resource is not in the access package's catalog.", hint);
            if (!RolesOf(resource, _onboardedElsewhere).Any(r => Str(r, "originId") == Str(role, "originId")))
            {
                throw FakeDirectory.BadRequest("Unknown role " + Str(role, "originId"), hint);
            }

            var stored = new JsonObject { ["id"] = NewId(), ["accessPackageId"] = Str(package, "id"), ["role"] = role.DeepClone(), ["scope"] = request["scope"]?.DeepClone() };
            _roleScopes.Add(stored);
            return (HttpStatusCode.Created, new JsonObject { ["id"] = Str(stored, "id"), ["createdDateTime"] = "2026-10-16T00:00:00Z" });
        }

        if (method == "DELETE" && scopeId is not null)
        {
            return _roleScopes.RemoveAll(r => Same(Str(r, "id"), scopeId) && Same(Str(r, "accessPackageId"), packageId)) > 0
                ? (HttpStatusCode.NoContent, null)
                : throw FakeDirectory.NotFound(path, hint);
        }

        throw FakeDirectory.MethodNotAllowed($"{method} on resourceRoleScopes (Graph offers list, create and delete)", hint);
    }

    private JsonObject Expand(JsonObject package, string expand)
    {
        var copy = Public(package);
        if (!expand.Contains("catalog", StringComparison.OrdinalIgnoreCase))
        {
            copy.Remove("catalog");
        }

        if (expand.Contains("resourceRoleScopes", StringComparison.OrdinalIgnoreCase))
        {
            copy["resourceRoleScopes"] = new JsonArray(_roleScopes.Where(r => Same(Str(r, "accessPackageId"), Str(package, "id")))
                .Select(r => (JsonNode)new JsonObject { ["id"] = Str(r, "id"), ["role"] = r["role"]?.DeepClone(), ["scope"] = r["scope"]?.DeepClone() }).ToArray());
        }

        return copy;
    }

    // ---- policies ----

    private JsonObject SavePolicy(string? id, JsonObject body, RetryHint hint)
    {
        string packageId = Str(body["accessPackage"] as JsonObject, "id") ?? throw FakeDirectory.BadRequest("accessPackage.id is required", hint);
        _ = Find(_packages, packageId) ?? throw FakeDirectory.NotFound("access package " + packageId, hint);
        if (Str(body, "displayName") is null)
        {
            throw FakeDirectory.BadRequest("displayName is required", hint);
        }

        var policy = (JsonObject)body.DeepClone();
        policy.Remove("accessPackage");
        policy["id"] = id ?? NewId();
        policy["accessPackageId"] = packageId;
        policy["createdDateTime"] = "2026-10-16T00:00:00Z";
        policy["automaticRequestSettings"] ??= null;
        policy["reviewSettings"] ??= null;
        if (policy["requestApprovalSettings"] is JsonObject approval)
        {
            approval["isApprovalRequiredForUpdate"] ??= false;
            foreach (var stage in (approval["stages"] as JsonArray)?.OfType<JsonObject>() ?? [])
            {
                stage["durationBeforeEscalation"] ??= "PT0S";
                stage["isEscalationEnabled"] ??= false;
                stage["fallbackPrimaryApprovers"] ??= new JsonArray();
                stage["escalationApprovers"] ??= new JsonArray();
                stage["fallbackEscalationApprovers"] ??= new JsonArray();
                foreach (var subject in (stage["primaryApprovers"] as JsonArray)?.OfType<JsonObject>() ?? [])
                {
                    subject["description"] ??= "Approver group";
                }
            }
        }

        _policies.RemoveAll(p => Same(Str(p, "id"), id));
        _policies.Add(policy);
        return policy;
    }

    // ---- helpers ----

    private (HttpStatusCode, JsonNode?) Entity(string method, List<JsonObject> collection, string id, JsonNode? body, RetryHint hint, Action<JsonObject> onDelete)
    {
        var entity = Find(collection, id) ?? throw FakeDirectory.NotFound(id, hint);
        switch (method)
        {
            case "GET":
                return (HttpStatusCode.OK, Public(entity));
            case "PATCH":
                foreach (var (name, value) in Body(body, hint))
                {
                    entity[name] = value?.DeepClone();
                }

                return (HttpStatusCode.NoContent, null);
            case "DELETE":
                onDelete(entity);
                collection.Remove(entity);
                return (HttpStatusCode.NoContent, null);
            default:
                throw FakeDirectory.MethodNotAllowed($"{method} on {id}", hint);
        }
    }

    private bool Visible(JsonObject resource)
    {
        string id = Str(resource, "id")!;
        if (_hiddenResources.TryGetValue(id, out int left) && left > 0)
        {
            _hiddenResources[id] = left - 1;
            return false;
        }

        return true;
    }

    private static JsonObject AddWithId(List<JsonObject> collection, JsonObject body, Action<JsonObject>? configure = null)
    {
        var entity = (JsonObject)body.DeepClone();
        entity["id"] = NewId();
        entity["createdDateTime"] = "2026-10-16T00:00:00Z";
        configure?.Invoke(entity);
        collection.Add(entity);
        return entity;
    }

    private static JsonObject Public(JsonObject entity)
    {
        var copy = (JsonObject)entity.DeepClone();
        copy.Remove("catalogId");
        copy.Remove("accessPackageId");
        return copy;
    }

    private static (HttpStatusCode, JsonNode?) Created(JsonObject entity)
    {
        return (HttpStatusCode.Created, Public(entity));
    }

    private static (HttpStatusCode, JsonNode?) List(IEnumerable<JsonObject> items)
    {
        return (HttpStatusCode.OK, new JsonObject { ["value"] = new JsonArray(items.Select(i => (JsonNode)i.DeepClone()).ToArray()) });
    }

    private static Dictionary<string, string> Filter(IReadOnlyDictionary<string, string>? query)
    {
        var clauses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (query is not null && query.TryGetValue("$filter", out var filter))
        {
            foreach (Match match in Clause.Matches(filter))
            {
                clauses[match.Groups[1].Value] = match.Groups[2].Value.Replace("''", "'", StringComparison.Ordinal);
            }
        }

        return clauses;
    }

    private static bool Matches(JsonObject entity, Dictionary<string, string> filter)
    {
        foreach (var (property, value) in filter)
        {
            string? actual = property switch
            {
                "accessPackage/id" => Str(entity, "accessPackageId"),
                "originSystem" or "resource/id" => value,
                _ => Str(entity, property),
            };
            if (!string.Equals(actual, value, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static void RequireGet(string method, RetryHint hint)
    {
        if (method != "GET")
        {
            throw FakeDirectory.MethodNotAllowed(method + " is not supported here", hint);
        }
    }

    private static JsonObject Body(JsonNode? body, RetryHint hint)
    {
        return body as JsonObject ?? throw FakeDirectory.BadRequest("A JSON object body is required.", hint);
    }

    private static JsonObject? Find(List<JsonObject> collection, string id)
    {
        return collection.FirstOrDefault(e => Same(Str(e, "id"), id));
    }

    private static string? CatalogOf(JsonObject package)
    {
        return Str(package["catalog"] as JsonObject, "id");
    }

    private static string? Str(JsonObject? obj, string name)
    {
        return obj?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }

    private static bool Same(string? left, string? right)
    {
        return left is not null && right is not null && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static string NewId()
    {
        return Guid.NewGuid().ToString();
    }
}
