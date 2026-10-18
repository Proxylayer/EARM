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
/// The in-memory tenant behind <see cref="FakeGraphClient"/>: collections of JSON objects keyed by Graph path,
/// reference navigations (<c>members</c>, <c>owners</c>, <c>manager</c>...), alternate-key and UPN routing,
/// <c>$filter prop eq 'value'</c>, <c>Prefer: create-if-missing</c> upserts, pagination, actions
/// (<c>addPassword</c>, <c>removePassword</c>, <c>assignLicense</c>, <c>updateAllowedCombinations</c>), soft-deleted
/// objects (<c>/directory/deletedItems/microsoft.graph.&lt;type&gt;</c> listing, <c>restore</c>, purge; seeded with
/// <see cref="AddDeleted"/> / <see cref="SoftDelete"/>) and the Graph-shaped errors the engine classifies (404 not
/// found, 400 "already exists", 400 bad appId). Every call is serialised on one lock; the planner's parallelism is
/// exercised, not the fake's.
/// <para>
/// Graph fidelity (Review 1D / Q-12, Review 3I): DELETE of a soft-deletable object (applications, Microsoft 365 and security groups, users, service
/// principals, administrative units; only distribution lists are deleted at once) moves it to <c>/directory/deletedItems</c> for 30 days (<see cref="Clock"/>), where it keeps its keys
/// (a create with the same uniqueName / userPrincipalName / appId / Microsoft 365 mailNickname conflicts until it is restored or purged);
/// a create without the properties Learn lists as required, a bogus Conditional Access <c>state</c>, a named location without its <c>@odata.type</c>, a <c>$top</c> over 999 and a
/// <c>$select</c> of a property the type does not have (<see cref="FakeDirectorySchema"/>) are 400s, typed objects come back with their default property set unless <c>$select</c> asks for more,
/// and a navigation below the <c>uniqueName</c> alternate key (which Learn documents for the entity upsert only) is not a route;
/// advanced queries (<c>$search</c>, <c>$count</c>, <c>ne</c> / <c>endswith</c> / <c>not</c> in <c>$filter</c>) on directory objects need
/// <c>ConsistencyLevel: eventual</c> (and <c>$count=true</c> for a filter); a <c>$filter</c> operator Graph does not offer on the endpoint is a
/// 400; an operation Graph does not document (PUT on an entity, PATCH of a read-only child, an untyped
/// <c>GET /directory/deletedItems</c>...) is a 405 / 400 instead of working.
/// </para>
/// </summary>
public sealed partial class FakeDirectory
{
    private static readonly string[] KnownRoots =
    [
        "directory/administrativeUnits",
        "directory/deletedItems",
        "identity/conditionalAccess/namedLocations",
        "identity/conditionalAccess/policies",
        "identity/conditionalAccess/authenticationContextClassReferences",
        "roleManagement/directory/roleDefinitions",
        "roleManagement/directory/roleAssignments",
        "roleManagement/directory/roleEligibilityScheduleRequests",
        "roleManagement/directory/roleEligibilitySchedules",
        "identityGovernance/privilegedAccess/group/eligibilityScheduleRequests",
        "identityGovernance/privilegedAccess/group/eligibilitySchedules",
        "roleManagement/directory/roleAssignmentScheduleRequests",
        "roleManagement/directory/roleAssignmentSchedules",
        "identityGovernance/privilegedAccess/group/assignmentScheduleRequests",
        "identityGovernance/privilegedAccess/group/assignmentSchedules",
        "policies/authenticationStrengthPolicies",
        "policies/authenticationMethodsPolicy/authenticationMethodConfigurations",
        "applications",
        "groups",
        "users",
        "servicePrincipals",
        "oauth2PermissionGrants",
        "organization",
        "directoryObjects",
        "subscribedSkus",
        "me",
    ];

    private static readonly HashSet<string> ReferenceNavigations = new(StringComparer.OrdinalIgnoreCase)
    {
        "members", "owners", "memberOf", "transitiveMembers", "manager", "directReports", "ownedObjects", "transitiveMemberOf",
    };

    private static readonly HashSet<string> SingleValuedNavigations = new(StringComparer.OrdinalIgnoreCase) { "manager" };

    private const string DeletedItemsPath = "/directory/deletedItems";

    /// <summary>The <c>@odata.type</c> of the objects in each soft-deletable collection.</summary>
    private static readonly Dictionary<string, string> ODataTypeByCollection = new(StringComparer.OrdinalIgnoreCase)
    {
        ["/users"] = "#microsoft.graph.user",
        ["/groups"] = "#microsoft.graph.group",
        ["/servicePrincipals"] = "#microsoft.graph.servicePrincipal",
        ["/applications"] = "#microsoft.graph.application",
        ["/directory/administrativeUnits"] = "#microsoft.graph.administrativeUnit",
        ["/devices"] = "#microsoft.graph.device",
        ["/contacts"] = "#microsoft.graph.orgContact",
    };

    private static readonly Dictionary<string, string> CollectionByODataType = ODataTypeByCollection.ToDictionary(p => p.Value, p => p.Key, StringComparer.OrdinalIgnoreCase);

    private static readonly TimeSpan DeletedRetention = TimeSpan.FromDays(30);

    private readonly object _gate = new();
    private readonly Dictionary<string, StaleRead> _staleAuthenticationMethodReads = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<JsonObject>> _collections = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _references = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _singleReferences = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _lingeringTrust = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _lingeringReferences = new(StringComparer.OrdinalIgnoreCase);

    public FakeDirectory()
    {
        Add("/organization", new JsonObject
        {
            ["id"] = TenantId,
            ["displayName"] = "Contoso (fake)",
            ["verifiedDomains"] = new JsonArray(new JsonObject { ["name"] = "contoso.com", ["isDefault"] = true }),
        });

        // The authentication methods policy always holds one configuration per method (Microsoft's defaults).
        foreach (var configuration in FakeAuthenticationMethodsPolicy.Defaults())
        {
            Add(FakeAuthenticationMethodsPolicy.CollectionPath, configuration);
        }
    }

    /// <summary>The tenant id of the fake organization.</summary>
    public string TenantId { get; init; } = "11111111-1111-1111-1111-111111111111";

    /// <summary>
    /// The object id <c>GET /me</c> answers with (a delegated session); <c>null</c> makes <c>/me</c> fail like an
    /// app-only token does ("/me request is only valid with delegated authentication flow").
    /// </summary>
    public string? SignedInUserId { get; set; }

    /// <summary>
    /// Conditional Access propagation lag, counted in refused delete attempts (default 0: none). After a trusted named location is unmarked
    /// (<c>isTrusted: false</c>) its delete is still refused with Graph's <c>1177</c> this many times; after a policy is deleted, the
    /// authentication strength, authentication contexts and named locations it referenced are still refused as "referenced" this many times.
    /// </summary>
    public int ConditionalAccessLagAttempts { get; set; }

    /// <summary>
    /// How many GETs of an authentication method configuration still answer its state from before a PATCH (default 0: the PATCH is visible at once). Graph
    /// accepts the PATCH (204) and serves the old values for a while.
    /// </summary>
    public int AuthenticationMethodStaleReads { get; set; }

    /// <summary>
    /// The <c>secretText</c> every <c>addPassword</c> answers with; <c>null</c> (default) answers a fresh random one. Leak tests set a
    /// canary here and assert it never reaches an error, a log line or a diagnostic.
    /// </summary>
    public string? SecretText { get; set; }

    /// <summary>Entitlement management: catalogs, catalog resources, access packages, resource role scopes and assignment policies.</summary>
    public FakeEntitlementManagement EntitlementManagement { get; } = new();

    /// <summary>PIM eligibility for directory roles (the request endpoint, the schedules, the seeded policy and the processing lag).</summary>
    public FakeRoleScheduleRequests RoleEligibility { get; } = FakeRoleScheduleRequests.Eligibility();

    /// <summary>PIM time-bound active assignment of directory roles (the request endpoint, the schedules, the seeded policy and the processing lag).</summary>
    public FakeRoleScheduleRequests RoleAssignmentSchedules { get; } = FakeRoleScheduleRequests.Assignment();

    /// <summary>PIM for Groups eligibility (the request endpoint, the schedules, the seeded group policy and the onboarded groups).</summary>
    public FakeGroupScheduleRequests GroupEligibility { get; } = FakeGroupScheduleRequests.Eligibility();

    /// <summary>PIM for Groups time-bound active assignment (the request endpoint, the schedules, the seeded group policy and the onboarded groups).</summary>
    public FakeGroupScheduleRequests GroupAssignmentSchedules { get; } = FakeGroupScheduleRequests.Assignment();

    /// <summary>PIM role management policies (the policy assignments, the policies with Microsoft's default rules, rule PATCHes, group onboarding).</summary>
    public FakeRoleManagementPolicies RoleManagementPolicies { get; } = new();

    /// <summary>The clock the 30-day soft-delete retention is measured with; tests move it to expire a deleted item.</summary>
    public TimeProvider Clock { get; set; } = TimeProvider.System;

    /// <summary>The lock every operation takes; exposed so tests can inspect consistently.</summary>
    public object SyncRoot => _gate;

    // ---- seeding ----

    /// <summary>
    /// Adds an entity to a collection (<c>/applications</c>, <c>/applications/{id}/federatedIdentityCredentials</c>...), assigning an id when missing. In the
    /// authentication methods policy (<see cref="FakeAuthenticationMethodsPolicy.CollectionPath"/>, singletons) an entity replaces the configuration with its id.
    /// </summary>
    public JsonObject Add(string collectionPath, JsonObject entity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionPath);
        ArgumentNullException.ThrowIfNull(entity);
        lock (_gate)
        {
            var stored = (JsonObject)entity.DeepClone();
            if (GetString(stored, "id") is null)
            {
                stored["id"] = Guid.NewGuid().ToString("D");
            }

            var collection = Collection(Normalize(collectionPath));
            if (FakeAuthenticationMethodsPolicy.IsCollection(Normalize(collectionPath)))
            {
                collection.RemoveAll(o => ValueEquals(GetString(o, "id"), GetString(stored, "id")));
            }

            collection.Add(stored);
            return stored;
        }
    }

    public JsonObject AddUser(string userPrincipalName, string displayName, Action<JsonObject>? configure = null)
    {
        var user = new JsonObject
        {
            ["userPrincipalName"] = userPrincipalName,
            ["displayName"] = displayName,
            ["mailNickname"] = userPrincipalName.Split('@')[0],
            ["accountEnabled"] = true,
        };
        configure?.Invoke(user);
        return Add("/users", user);
    }

    public JsonObject AddGroup(string uniqueName, string displayName, Action<JsonObject>? configure = null)
    {
        var group = new JsonObject
        {
            ["uniqueName"] = uniqueName,
            ["displayName"] = displayName,
            ["mailEnabled"] = false,
            ["mailNickname"] = uniqueName,
            ["securityEnabled"] = true,
            ["groupTypes"] = new JsonArray(),
        };
        configure?.Invoke(group);
        return Add("/groups", group);
    }

    public JsonObject AddApplication(string uniqueName, string displayName, string? appId = null, Action<JsonObject>? configure = null)
    {
        var app = new JsonObject
        {
            ["uniqueName"] = uniqueName,
            ["displayName"] = displayName,
            ["appId"] = appId ?? Guid.NewGuid().ToString("D"),
            ["signInAudience"] = "AzureADMyOrg",
            ["createdDateTime"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        };
        configure?.Invoke(app);
        return Add("/applications", app);
    }

    public JsonObject AddServicePrincipal(string appId, string displayName, Action<JsonObject>? configure = null)
    {
        var sp = new JsonObject
        {
            ["appId"] = appId,
            ["displayName"] = displayName,
            ["accountEnabled"] = true,
            ["servicePrincipalType"] = "Application",
            ["appRoles"] = new JsonArray(),
            ["oauth2PermissionScopes"] = new JsonArray(),
        };
        configure?.Invoke(sp);
        return Add("/servicePrincipals", sp);
    }

    // ---- queries for assertions ----

    /// <summary>A snapshot of a collection.</summary>
    public IReadOnlyList<JsonObject> Objects(string collectionPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionPath);
        lock (_gate)
        {
            return _collections.TryGetValue(Normalize(collectionPath), out var list) ? list.Select(o => (JsonObject)o.DeepClone()).ToList() : [];
        }
    }

    /// <summary>The first object in a collection whose property equals the value (GUID-aware, case-insensitive).</summary>
    public JsonObject? Find(string collectionPath, string property, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionPath);
        lock (_gate)
        {
            var found = Collection(Normalize(collectionPath)).FirstOrDefault(o => ValueEquals(GetString(o, property), value));
            return found is null ? null : (JsonObject)found.DeepClone();
        }
    }

    /// <summary>Finds an object by id across every collection.</summary>
    public JsonObject? FindById(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        lock (_gate)
        {
            return LocateById(id, out _) is { } found ? (JsonObject)found.DeepClone() : null;
        }
    }

    /// <summary>The object ids referenced by a navigation (<c>/groups/{id}/members</c>).</summary>
    public IReadOnlyList<string> References(string navigationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(navigationPath);
        lock (_gate)
        {
            if (_singleReferences.TryGetValue(Normalize(navigationPath), out var single))
            {
                return [single];
            }

            return _references.TryGetValue(Normalize(navigationPath), out var list) ? list.ToList() : [];
        }
    }

    public int Count(string collectionPath)
    {
        lock (_gate)
        {
            return _collections.TryGetValue(Normalize(collectionPath), out var list) ? list.Count : 0;
        }
    }

    // ---- request handling ----

    /// <param name="beta">True for a request to the beta endpoint; the v1.0 quirks this fake models (see <see cref="HandleReferenceNavigation"/>) do not apply there.</param>
    internal (HttpStatusCode Status, JsonNode? Body) Handle(string method, string path, IReadOnlyDictionary<string, string>? query, IReadOnlyDictionary<string, string>? headers, JsonNode? body, RetryHint hint, int pageSize, bool beta = false)
    {
        lock (_gate)
        {
            PurgeExpiredDeletedItems();
            if (FakeRoleManagementPolicies.Handles(path))
            {
                return RoleManagementPolicies.Handle(
                    method,
                    path,
                    query,
                    body,
                    hint,
                    groupId => Collection("/groups").Any(g => ValueEquals(GetString(g, "id"), groupId)),
                    groupId => GroupEligibility.OnboardedGroups.Contains(groupId, StringComparer.OrdinalIgnoreCase) || GroupAssignmentSchedules.OnboardedGroups.Contains(groupId, StringComparer.OrdinalIgnoreCase));
            }

            if (FakeEntitlementManagement.Handles(path))
            {
                return EntitlementManagement.Handle(
                    method,
                    path,
                    query,
                    body,
                    hint,
                    groupId => Collection("/groups").FirstOrDefault(g => ValueEquals(GetString(g, "id"), groupId)),
                    groupId => GroupEligibility.OnboardedGroups.Contains(groupId, StringComparer.OrdinalIgnoreCase) || GroupAssignmentSchedules.OnboardedGroups.Contains(groupId, StringComparer.OrdinalIgnoreCase));
            }

            if (TryHandleTenantPolicies(method, path, query, body, hint, pageSize, out var policyResult))
            {
                return policyResult;
            }

            var route = Route.Parse(path, KnownRoots);
            if (route is null)
            {
                throw NotFound(path, hint);
            }

            if (method == "GET")
            {
                EnforceQueryRules(route, query, headers, hint);
            }

            if (route.Root.Equals("me", StringComparison.OrdinalIgnoreCase))
            {
                return HandleMe(method, route, query, hint);
            }

            return route.Nav is null
                ? HandleCollectionOrEntity(method, route, query, headers, body, hint, pageSize)
                : HandleNavigation(method, route, query, headers, body, hint, pageSize, beta);
        }
    }

    // ---- entities ----

    private List<JsonObject> Collection(string path)
    {
        if (!_collections.TryGetValue(path, out var list))
        {
            list = [];
            _collections[path] = list;
        }

        return list;
    }

    private List<string> ReferencesList(string navPath)
    {
        if (!_references.TryGetValue(navPath, out var list))
        {
            list = [];
            _references[navPath] = list;
        }

        return list;
    }

    private JsonObject? Locate(string collectionPath, Selector selector)
    {
        var collection = Collection(collectionPath);
        if (selector.IsAlternateKey)
        {
            return collection.FirstOrDefault(o => ValueEquals(GetString(o, selector.Property!), selector.Value));
        }

        var byId = collection.FirstOrDefault(o => ValueEquals(GetString(o, "id"), selector.Value));
        if (byId is not null)
        {
            return byId;
        }

        if (collectionPath.Equals("/users", StringComparison.OrdinalIgnoreCase))
        {
            return collection.FirstOrDefault(o => ValueEquals(GetString(o, "userPrincipalName"), selector.Value));
        }

        if (collectionPath.Equals("/directoryObjects", StringComparison.OrdinalIgnoreCase))
        {
            return LocateById(selector.Value, out _) is { } found ? WithODataType(found) : null;
        }

        return null;
    }

    private (JsonObject Object, string Collection)? LocatePrincipal(string id)
    {
        return LocateById(id, out var collectionPath) is { } found ? (found, collectionPath) : null;
    }

    private JsonObject? LocateById(string id, out string collectionPath)
    {
        foreach (var pair in _collections)
        {
            var found = pair.Value.FirstOrDefault(o => ValueEquals(GetString(o, "id"), id));
            if (found is not null)
            {
                collectionPath = pair.Key;
                return found;
            }
        }

        collectionPath = string.Empty;
        return null;
    }

    // ---- helpers ----

    private static bool PrefersCreateIfMissing(IReadOnlyDictionary<string, string>? headers)
    {
        return headers is not null && headers.Any(h => h.Key.Equals("Prefer", StringComparison.OrdinalIgnoreCase) && h.Value.Contains("create-if-missing", StringComparison.OrdinalIgnoreCase));
    }

    private JsonObject WithODataType(JsonObject entity)
    {
        var copy = (JsonObject)entity.DeepClone();
        if (!copy.ContainsKey("@odata.type"))
        {
            LocateById(GetString(entity, "id")!, out var collectionPath);
            if (ODataTypeByCollection.TryGetValue(collectionPath, out var type))
            {
                copy.Insert(0, "@odata.type", type);
            }
        }

        return copy;
    }

    private static string Normalize(string path)
    {
        return "/" + path.Trim().Trim('/');
    }

    private static string? GetString(JsonObject? obj, string property)
    {
        if (obj is null)
        {
            return null;
        }

        foreach (var pair in obj)
        {
            if (pair.Key.Equals(property, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
            }
        }

        return null;
    }

    private static string? GetScalar(JsonObject obj, string property)
    {
        foreach (var pair in obj)
        {
            if (!pair.Key.Equals(property, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return pair.Value switch
            {
                null => null,
                JsonValue value when value.TryGetValue<string>(out var text) => text,
                JsonValue value when value.GetValueKind() is JsonValueKind.True => "true",
                JsonValue value when value.GetValueKind() is JsonValueKind.False => "false",
                JsonValue value when value.GetValueKind() is JsonValueKind.Null => null,
                JsonValue value => value.ToJsonString(),
                _ => pair.Value.ToJsonString(),
            };
        }

        return null;
    }

    internal static bool ValueEquals(string? left, string? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        if (Guid.TryParse(left, out var l) && Guid.TryParse(right, out var r))
        {
            return l == r;
        }

        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    internal static GraphException NotFound(string path, RetryHint hint)
    {
        return new GraphException(HttpStatusCode.NotFound, "Request_ResourceNotFound", $"Resource '{path}' does not exist or one of its queried reference-property objects are not present.", hint);
    }

    internal static GraphException MethodNotAllowed(string message, RetryHint hint)
    {
        return new GraphException(HttpStatusCode.MethodNotAllowed, "Request_BadRequest", message, hint);
    }

    internal static GraphException BadRequest(string message, RetryHint hint)
    {
        return new GraphException(HttpStatusCode.BadRequest, "Request_BadRequest", message, hint);
    }
}
