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
public sealed class FakeDirectory
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
        "policies/authenticationStrengthPolicies",
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
    };

    private static readonly Dictionary<string, string> CollectionByODataType = ODataTypeByCollection.ToDictionary(p => p.Value, p => p.Key, StringComparer.OrdinalIgnoreCase);

    private static readonly TimeSpan DeletedRetention = TimeSpan.FromDays(30);

    private readonly object _gate = new();
    private readonly Dictionary<string, List<JsonObject>> _collections = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _references = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _singleReferences = new(StringComparer.OrdinalIgnoreCase);

    public FakeDirectory()
    {
        Add("/organization", new JsonObject
        {
            ["id"] = TenantId,
            ["displayName"] = "Contoso (fake)",
            ["verifiedDomains"] = new JsonArray(new JsonObject { ["name"] = "contoso.com", ["isDefault"] = true }),
        });
    }

    /// <summary>The tenant id of the fake organization.</summary>
    public string TenantId { get; init; } = "11111111-1111-1111-1111-111111111111";

    /// <summary>
    /// The object id <c>GET /me</c> answers with (a delegated session); <c>null</c> makes <c>/me</c> fail like an
    /// app-only token does ("/me request is only valid with delegated authentication flow").
    /// </summary>
    public string? SignedInUserId { get; set; }

    /// <summary>The clock the 30-day soft-delete retention is measured with; tests move it to expire a deleted item.</summary>
    public TimeProvider Clock { get; set; } = TimeProvider.System;

    /// <summary>The lock every operation takes; exposed so tests can inspect consistently.</summary>
    public object SyncRoot => _gate;

    // ---- seeding ----

    /// <summary>Adds an entity to a collection (<c>/applications</c>, <c>/applications/{id}/federatedIdentityCredentials</c>...), assigning an id when missing.</summary>
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

            Collection(Normalize(collectionPath)).Add(stored);
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

    /// <summary>
    /// Seeds a soft-deleted object that originally lived in <paramref name="collectionPath"/> (<c>/applications</c>,
    /// <c>/groups</c>, <c>/users</c>, <c>/servicePrincipals</c>, <c>/directory/administrativeUnits</c>): it lands in
    /// <c>/directory/deletedItems</c> with <c>@odata.type</c> and <c>deletedDateTime</c>, keeps its keys
    /// (<c>uniqueName</c>...) and can be listed, restored or purged like in Graph.
    /// </summary>
    public JsonObject AddDeleted(string collectionPath, JsonObject entity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionPath);
        ArgumentNullException.ThrowIfNull(entity);
        lock (_gate)
        {
            if (!ODataTypeByCollection.TryGetValue(Normalize(collectionPath), out var odataType))
            {
                throw new ArgumentException($"Objects of '{collectionPath}' are not soft-deletable in the fake.", nameof(collectionPath));
            }

            var deleted = (JsonObject)entity.DeepClone();
            if (GetString(deleted, "id") is null)
            {
                deleted["id"] = Guid.NewGuid().ToString("D");
            }

            deleted["@odata.type"] = odataType;
            deleted["deletedDateTime"] ??= DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            Collection(DeletedItemsPath).Add(deleted);
            return (JsonObject)deleted.DeepClone();
        }
    }

    /// <summary>
    /// Moves a live object to <c>/directory/deletedItems</c> (what Graph does on DELETE for applications, groups, users,
    /// service principals and administrative units) and drops the references to it. <c>DELETE</c> through the client
    /// still removes objects outright; call this to stage the "key held by a soft-deleted object" scenario.
    /// </summary>
    public JsonObject SoftDelete(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (_gate)
        {
            var found = LocateById(id, out var collectionPath)
                ?? throw new InvalidOperationException($"No object with id '{id}'.");
            if (!ODataTypeByCollection.TryGetValue(collectionPath, out var odataType))
            {
                throw new InvalidOperationException($"Objects of '{collectionPath}' are not soft-deletable in the fake.");
            }

            Collection(collectionPath).Remove(found);
            RemoveReferencesTo(id);
            var deleted = (JsonObject)found.DeepClone();
            deleted["@odata.type"] = odataType;
            deleted["deletedDateTime"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            Collection(DeletedItemsPath).Add(deleted);
            return (JsonObject)deleted.DeepClone();
        }
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

    internal (HttpStatusCode Status, JsonNode? Body) Handle(string method, string path, IReadOnlyDictionary<string, string>? query, IReadOnlyDictionary<string, string>? headers, JsonNode? body, RetryHint hint, int pageSize)
    {
        lock (_gate)
        {
            PurgeExpiredDeletedItems();
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
                : HandleNavigation(method, route, query, headers, body, hint, pageSize);
        }
    }

    private (HttpStatusCode, JsonNode?) HandleCollectionOrEntity(string method, Route route, IReadOnlyDictionary<string, string>? query, IReadOnlyDictionary<string, string>? headers, JsonNode? body, RetryHint hint, int pageSize)
    {
        string collectionPath = "/" + route.Root;
        if (route.Selector is { IsAlternateKey: false } cast && collectionPath.Equals(DeletedItemsPath, StringComparison.OrdinalIgnoreCase) && cast.Value.StartsWith("microsoft.graph.", StringComparison.OrdinalIgnoreCase))
        {
            // GET /directory/deletedItems/microsoft.graph.application: the type-cast collection of deleted objects.
            if (method != "GET")
            {
                throw BadRequest($"{method} is not supported on '{route.Path}'.", hint);
            }

            string odataType = "#" + cast.Value;
            var typed = Collection(collectionPath).Where(o => ValueEquals(GetString(o, "@odata.type"), odataType)).ToList();
            return (HttpStatusCode.OK, Page(Project(Filter(typed, query, hint), FakeDirectorySchema.ForODataType(odataType), query, hint), route.Root + "/" + cast.Value, query, pageSize));
        }

        if (route.Selector is null)
        {
            if (collectionPath.Equals(DeletedItemsPath, StringComparison.OrdinalIgnoreCase))
            {
                // Learn (directory-deleteditems-list): listing /directory/deletedItems without a type is not supported.
                throw BadRequest("Listing /directory/deletedItems requires a type cast (for example /directory/deletedItems/microsoft.graph.application).", hint);
            }

            return method switch
            {
                "GET" => (HttpStatusCode.OK, Page(Project(Filter(Collection(collectionPath), query, hint), FakeDirectorySchema.ForCollection(collectionPath), query, hint), route.Root, query, pageSize)),
                "POST" => (HttpStatusCode.Created, Create(collectionPath, body, hint)),
                _ => throw MethodNotAllowed($"{method} is not supported on collection '{collectionPath}'.", hint),
            };
        }

        var entity = Locate(collectionPath, route.Selector);
        switch (method)
        {
            case "GET":
                return entity is null ? throw NotFound(route.Path, hint) : (HttpStatusCode.OK, ProjectOne(entity, collectionPath.Equals(DeletedItemsPath, StringComparison.OrdinalIgnoreCase) ? FakeDirectorySchema.ForODataType(GetString(entity, "@odata.type")) : FakeDirectorySchema.ForCollection(collectionPath), query, hint));

            case "PATCH":
                if (entity is null)
                {
                    // Alternate-key upsert (applications, groups: Prefer: create-if-missing) or id upsert
                    // (authenticationContextClassReferences: Graph creates on PATCH /{id} without any header).
                    if (PrefersCreateIfMissing(headers) || collectionPath.Equals(AuthenticationContextsPath, StringComparison.OrdinalIgnoreCase))
                    {
                        var created = Create(collectionPath, Merge(new JsonObject(), body), hint, route.Selector);
                        return (HttpStatusCode.Created, created);
                    }

                    throw NotFound(route.Path, hint);
                }

                RejectBuiltInStrengthWrite(collectionPath, entity, hint);
                Patch(entity, body, collectionPath, hint);
                return (HttpStatusCode.NoContent, null);

            case "PUT":
                // Graph has no PUT on an entity (only PUT /users/{id}/manager/$ref and a few relationship links).
                throw MethodNotAllowed($"PUT is not supported on '{route.Path}'.", hint);

            case "DELETE":
                if (entity is null)
                {
                    throw NotFound(route.Path, hint);
                }

                RejectBuiltInStrengthWrite(collectionPath, entity, hint);
                RejectConditionalAccessDelete(collectionPath, entity, hint);
                RejectRoleDefinitionDelete(collectionPath, entity, hint);
                if (IsSoftDeletable(collectionPath, entity))
                {
                    SoftDeleteWithCascade(entity, collectionPath);
                    return (HttpStatusCode.NoContent, null);
                }

                Collection(collectionPath).Remove(entity);
                RemoveReferencesTo(GetString(entity, "id")!);
                return (HttpStatusCode.NoContent, null);

            default:
                throw MethodNotAllowed($"{method} is not supported on '{route.Path}'.", hint);
        }
    }

    private (HttpStatusCode, JsonNode?) HandleNavigation(string method, Route route, IReadOnlyDictionary<string, string>? query, IReadOnlyDictionary<string, string>? headers, JsonNode? body, RetryHint hint, int pageSize)
    {
        string collectionPath = "/" + route.Root;
        var entity = route.Selector is null ? null : Locate(collectionPath, route.Selector);
        if (entity is null)
        {
            throw NotFound(route.Path, hint);
        }

        string entityId = GetString(entity, "id")!;
        string nav = route.Nav!;

        // Learn documents navigations and actions under an object id or the appId alternate key; the uniqueName key appears only on the entity upsert
        // (application-upsert, group-upsert, federatedidentitycredential-upsert URL forms), so anything below it is not a Graph route.
        if (route.Selector is { IsAlternateKey: true } alternate && alternate.Property!.Equals("uniqueName", StringComparison.OrdinalIgnoreCase))
        {
            throw BadRequest($"The segment '{nav}' cannot follow the alternate key 'uniqueName' ({route.Path}); address the object by its id (or appId) first.", hint);
        }

        // Actions.
        switch (nav.ToLowerInvariant())
        {
            case "addpassword":
                return (HttpStatusCode.OK, AddPassword(entity, body));
            case "removepassword":
                RemovePassword(entity, body);
                return (HttpStatusCode.NoContent, null);
            case "assignlicense":
                AssignLicense(entity, body, trackStates: collectionPath.Equals("/users", StringComparison.OrdinalIgnoreCase));
                return (HttpStatusCode.OK, entity.DeepClone());
            case "updateallowedcombinations":
                RejectBuiltInStrengthWrite(collectionPath, entity, hint);
                if (body is JsonObject combos && combos["allowedCombinations"] is JsonArray allowed)
                {
                    entity["allowedCombinations"] = allowed.DeepClone();
                }

                return (HttpStatusCode.OK, new JsonObject { ["value"] = entity["allowedCombinations"]?.DeepClone() });
            case "restore":
                return (HttpStatusCode.OK, Restore(entity, collectionPath, hint));
        }

        // Principal-side view of app role assignments (Graph derives /{users|groups|servicePrincipals}/{id}/appRoleAssignments from appRoleAssignedTo).
        if (nav.Equals("appRoleAssignments", StringComparison.OrdinalIgnoreCase) && IsPrincipalCollection(collectionPath))
        {
            return HandlePrincipalAppRoleAssignments(method, route, entityId, query, body, hint, pageSize);
        }

        string navPath = collectionPath + "/" + entityId + "/" + nav;
        if (ReferenceNavigations.Contains(nav))
        {
            return HandleReferenceNavigation(method, route, navPath, nav, query, body, hint, pageSize);
        }

        // Child collection (federatedIdentityCredentials, appRoleAssignedTo, appRoleAssignments...).
        if (route.ChildSelector is null)
        {
            return method switch
            {
                "GET" => (HttpStatusCode.OK, Page(Project(Filter(Collection(navPath), query, hint), FakeDirectorySchema.ForCollection(navPath), query, hint), route.Root + "/" + entityId + "/" + nav, query, pageSize)),
                "POST" => (HttpStatusCode.Created, Create(navPath, body, hint)),
                _ => throw MethodNotAllowed($"{method} is not supported on '{route.Path}'.", hint),
            };
        }

        var child = Locate(navPath, route.ChildSelector);
        switch (method)
        {
            case "GET":
                return child is null ? throw NotFound(route.Path, hint) : (HttpStatusCode.OK, ProjectOne(child, FakeDirectorySchema.ForCollection(navPath), query, hint));
            case "PATCH":
                if (!nav.Equals("federatedIdentityCredentials", StringComparison.OrdinalIgnoreCase))
                {
                    // Graph documents no update for an app role assignment, a password credential...: only federated identity credentials are patched.
                    throw MethodNotAllowed($"PATCH is not supported on '{route.Path}'.", hint);
                }

                if (child is null)
                {
                    if (PrefersCreateIfMissing(headers))
                    {
                        return (HttpStatusCode.Created, Create(navPath, Merge(new JsonObject(), body), hint, route.ChildSelector));
                    }

                    throw NotFound(route.Path, hint);
                }

                Patch(child, body, navPath, hint);
                return (HttpStatusCode.NoContent, null);
            case "DELETE":
                if (child is null)
                {
                    throw NotFound(route.Path, hint);
                }

                Collection(navPath).Remove(child);
                return (HttpStatusCode.NoContent, null);
            default:
                throw BadRequest($"{method} is not supported on '{route.Path}'.", hint);
        }
    }

    private (HttpStatusCode, JsonNode?) HandleReferenceNavigation(string method, Route route, string navPath, string nav, IReadOnlyDictionary<string, string>? query, JsonNode? body, RetryHint hint, int pageSize)
    {
        bool single = SingleValuedNavigations.Contains(nav);
        if (route.IsRef)
        {
            switch (method)
            {
                case "POST" when !single:
                    {
                        string target = ReferenceTarget(body, hint);
                        var list = ReferencesList(navPath);
                        if (list.Contains(target, StringComparer.OrdinalIgnoreCase))
                        {
                            throw new GraphException(HttpStatusCode.BadRequest, "Request_BadRequest", "One or more added object references already exist for the following modified properties: 'members'.", hint);
                        }

                        list.Add(target);
                        return (HttpStatusCode.NoContent, null);
                    }

                case "PUT" when single:
                    _singleReferences[navPath] = ReferenceTarget(body, hint);
                    return (HttpStatusCode.NoContent, null);

                case "DELETE" when single:
                    if (!_singleReferences.Remove(navPath))
                    {
                        throw NotFound(route.Path, hint);
                    }

                    return (HttpStatusCode.NoContent, null);

                case "DELETE" when route.ChildSelector is not null:
                    {
                        var list = ReferencesList(navPath);
                        int removed = list.RemoveAll(id => ValueEquals(id, route.ChildSelector.Value));
                        if (removed == 0)
                        {
                            throw NotFound(route.Path, hint);
                        }

                        return (HttpStatusCode.NoContent, null);
                    }

                default:
                    throw BadRequest($"{method} is not supported on '{route.Path}'.", hint);
            }
        }

        if (method != "GET")
        {
            throw BadRequest($"{method} is not supported on '{route.Path}' (use $ref).", hint);
        }

        if (single)
        {
            if (!_singleReferences.TryGetValue(navPath, out var targetId) || LocateById(targetId, out _) is not { } target)
            {
                throw NotFound(route.Path, hint);
            }

            return (HttpStatusCode.OK, ProjectOne(WithODataType(target), SchemaOfId(GetString(target, "id")), query, hint));
        }

        var objects = new List<JsonObject>();
        foreach (var id in ReferencedIds(navPath, nav))
        {
            if (LocateById(id, out _) is { } referenced)
            {
                objects.Add(WithODataType(referenced));
            }
        }

        return (HttpStatusCode.OK, Page(ProjectMixed(Filter(objects, query, hint), query), route.Root + "/" + GetString(Locate("/" + route.Root, route.Selector!)!, "id") + "/" + nav, query, pageSize));
    }

    /// <summary>
    /// The ids a navigation lists: its explicit <c>$ref</c> list plus, for the reverse navigations Graph derives
    /// (<c>memberOf</c> / <c>transitiveMemberOf</c> from <c>members</c>, <c>ownedObjects</c> from <c>owners</c>,
    /// <c>directReports</c> from <c>manager</c>), the owners of the forward lists that name the entity.
    /// </summary>
    private IEnumerable<string> ReferencedIds(string navPath, string nav)
    {
        var ids = new List<string>(ReferencesList(navPath));
        string entityId = navPath.Split('/')[^2];
        string? forward = nav.ToLowerInvariant() switch
        {
            "memberof" or "transitivememberof" => "/members",
            "ownedobjects" => "/owners",
            _ => null,
        };

        if (forward is not null)
        {
            foreach (var pair in _references)
            {
                if (pair.Key.EndsWith(forward, StringComparison.OrdinalIgnoreCase) && pair.Value.Any(r => ValueEquals(r, entityId)))
                {
                    AddOwner(ids, pair.Key[..^forward.Length]);
                }
            }
        }
        else if (nav.Equals("directReports", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var pair in _singleReferences)
            {
                if (pair.Key.EndsWith("/manager", StringComparison.OrdinalIgnoreCase) && ValueEquals(pair.Value, entityId))
                {
                    AddOwner(ids, pair.Key[..^"/manager".Length]);
                }
            }
        }

        return ids;

        static void AddOwner(List<string> ids, string entityPath)
        {
            string ownerId = entityPath[(entityPath.LastIndexOf('/') + 1)..];
            if (!ids.Any(existing => ValueEquals(existing, ownerId)))
            {
                ids.Add(ownerId);
            }
        }
    }

    /// <summary><c>GET /me</c>: the signed-in user (<see cref="SignedInUserId"/>), or the app-only error.</summary>
    private (HttpStatusCode, JsonNode?) HandleMe(string method, Route route, IReadOnlyDictionary<string, string>? query, RetryHint hint)
    {
        if (method != "GET" || route.Selector is not null)
        {
            throw BadRequest($"{method} is not supported on '{route.Path}' by the fake.", hint);
        }

        if (SignedInUserId is null)
        {
            throw BadRequest("/me request is only valid with delegated authentication flow.", hint);
        }

        return LocateById(SignedInUserId, out _) is { } me ? (HttpStatusCode.OK, ProjectOne(me, FakeDirectorySchema.Users, query, hint)) : throw NotFound(route.Path, hint);
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

    private JsonObject Create(string collectionPath, JsonNode? body, RetryHint hint, Selector? key = null)
    {
        if (body is not JsonObject payload)
        {
            throw BadRequest("A JSON object body is required.", hint);
        }

        var entity = (JsonObject)payload.DeepClone();
        if (key is not null)
        {
            // The upsert key becomes the property (alternate key) or the id itself (id upsert).
            entity[key.Property ?? "id"] = key.Value;
        }

        if (GetString(entity, "id") is null)
        {
            entity["id"] = Guid.NewGuid().ToString("D");
        }

        string root = collectionPath.ToLowerInvariant();
        if (root != "/users")
        {
            RequireProperties(root, entity, hint);
        }

        if (root == "/applications")
        {
            entity["appId"] ??= Guid.NewGuid().ToString("D");
            entity["createdDateTime"] ??= DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            entity["publisherDomain"] ??= "contoso.com";
            RejectDuplicate(collectionPath, entity, "uniqueName", hint);
            RejectDuplicate(collectionPath, entity, "appId", hint);
            RejectDeletedDuplicate(collectionPath, entity, "uniqueName", hint);
        }
        else if (root == "/groups")
        {
            entity["createdDateTime"] ??= DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            entity["securityIdentifier"] ??= "S-1-12-1-" + Guid.NewGuid().ToString("N")[..12];
            RejectDuplicate(collectionPath, entity, "uniqueName", hint);
            RejectDeletedDuplicate(collectionPath, entity, "uniqueName", hint);
            if (IsUnified(entity))
            {
                // A soft-deleted Microsoft 365 group keeps its mailNickname for 30 days.
                RejectDeletedDuplicate(collectionPath, entity, "mailNickname", hint);
            }
        }
        else if (root == "/users")
        {
            if (GetString(entity, "userPrincipalName") is null)
            {
                throw BadRequest("A user requires a userPrincipalName.", hint);
            }

            RequireProperties(root, entity, hint);

            entity.Remove("passwordProfile");
            entity["createdDateTime"] ??= DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            RejectDuplicate(collectionPath, entity, "userPrincipalName", hint);
            RejectDeletedDuplicate(collectionPath, entity, "userPrincipalName", hint);
        }
        else if (root == "/serviceprincipals")
        {
            string? appId = GetString(entity, "appId");
            if (appId is null)
            {
                throw BadRequest("A service principal requires an appId.", hint);
            }

            RejectDuplicate(collectionPath, entity, "appId", hint);
            RejectDeletedDuplicate(collectionPath, entity, "appId", hint);
            var application = Collection("/applications").FirstOrDefault(a => ValueEquals(GetString(a, "appId"), appId));
            if (application is null)
            {
                throw new GraphException(HttpStatusCode.BadRequest, "Request_BadRequest", $"The appId '{appId}' of the service principal does not reference a valid application object.", hint);
            }

            entity["displayName"] ??= application["displayName"]?.DeepClone();
            entity["appRoles"] ??= application["appRoles"]?.DeepClone() ?? new JsonArray();
            entity["oauth2PermissionScopes"] ??= (application["api"] as JsonObject)?["oauth2PermissionScopes"]?.DeepClone() ?? new JsonArray();
            entity["servicePrincipalType"] ??= "Application";
        }
        else if (root.EndsWith("/federatedidentitycredentials", StringComparison.Ordinal))
        {
            RejectDuplicate(collectionPath, entity, "name", hint);
        }
        else if (root.EndsWith("/approleassignedto", StringComparison.Ordinal))
        {
            PrepareAppRoleAssignment(collectionPath, entity, hint);
        }
        else if (root == "/oauth2permissiongrants")
        {
            PrepareGrant(collectionPath, entity, hint);
        }
        else if (root == "/policies/authenticationstrengthpolicies")
        {
            // Everything created through the API is a custom strength; the built-ins are seeded with policyType builtIn.
            entity["policyType"] ??= "custom";
            entity["createdDateTime"] ??= DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            entity["modifiedDateTime"] ??= DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        }
        else if (root == "/rolemanagement/directory/roleassignments")
        {
            PrepareRoleAssignment(collectionPath, entity, hint);
        }
        else if (root == "/rolemanagement/directory/roledefinitions")
        {
            // Everything created through the API is a custom role; built-in roles are seeded with isBuiltIn true.
            entity["isBuiltIn"] ??= false;
            entity["templateId"] ??= entity["id"]!.DeepClone();
            entity["version"] ??= "1";
        }

        Collection(collectionPath).Add(entity);
        ApplyBinds(entity, collectionPath);
        return (JsonObject)entity.DeepClone();
    }

    /// <summary>
    /// <c>owners@odata.bind</c> / <c>members@odata.bind</c> in a create or update body (Learn: group-upsert, group-post-groups): the
    /// referenced objects become owners / members and the annotation is not stored.
    /// </summary>
    private void ApplyBinds(JsonObject entity, string collectionPath)
    {
        foreach (string key in entity.Select(p => p.Key).Where(k => k.EndsWith("@odata.bind", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            string nav = key[..^"@odata.bind".Length];
            if (entity[key] is JsonArray targets && GetString(entity, "id") is { } id)
            {
                var list = ReferencesList(collectionPath + "/" + id + "/" + nav);
                foreach (var target in targets)
                {
                    string? url = target is JsonValue v && v.TryGetValue<string>(out var text) ? text : null;
                    string targetId = url is null ? string.Empty : url.TrimEnd('/')[(url.TrimEnd('/').LastIndexOf('/') + 1)..];
                    if (targetId.Length > 0 && !list.Contains(targetId, StringComparer.OrdinalIgnoreCase))
                    {
                        list.Add(targetId);
                    }
                }
            }

            entity.Remove(key);
        }
    }

    // ---- directory roles ----

    private const string RoleAssignmentsPath = "/roleManagement/directory/roleAssignments";

    private const string RoleDefinitionsPath = "/roleManagement/directory/roleDefinitions";

    /// <summary>
    /// <c>POST /roleManagement/directory/roleAssignments</c>: the role and the principal are required, the scope
    /// defaults to <c>/</c>, the principal must exist and be a user, a role-assignable group or a service principal,
    /// and a second assignment of the same (roleDefinitionId, principalId, directoryScopeId) is the 400 Graph returns.
    /// The <c>@odata.type</c> of the body is not stored (Graph does not echo it).
    /// </summary>
    private void PrepareRoleAssignment(string collectionPath, JsonObject entity, RetryHint hint)
    {
        entity.Remove("@odata.type");
        string? roleDefinitionId = GetString(entity, "roleDefinitionId");
        string? principalId = GetString(entity, "principalId");
        if (roleDefinitionId is null || principalId is null)
        {
            throw BadRequest("A role assignment requires roleDefinitionId and principalId.", hint);
        }

        if (GetString(entity, "directoryScopeId") is null)
        {
            entity["directoryScopeId"] = "/";
        }

        var principal = LocateById(principalId, out var principalCollection);
        if (principal is null)
        {
            throw BadRequest("PrincipalNotFound: the principal does not exist or has not replicated yet.", hint);
        }

        if (principalCollection.Equals("/groups", StringComparison.OrdinalIgnoreCase)
            && (principal["isAssignableToRole"] is not JsonValue assignable || assignable.GetValueKind() != JsonValueKind.True))
        {
            throw BadRequest("The group must be role-assignable (isAssignableToRole) to receive a directory role assignment.", hint);
        }
        else if (!IsPrincipalCollection(principalCollection))
        {
            throw BadRequest("The principal must be a user, a group or a service principal.", hint);
        }

        if (Collection(collectionPath).Any(a => ValueEquals(GetString(a, "roleDefinitionId"), roleDefinitionId)
            && ValueEquals(GetString(a, "principalId"), principalId)
            && ValueEquals(GetString(a, "directoryScopeId"), GetString(entity, "directoryScopeId"))))
        {
            throw BadRequest("A conflicting object with one or more of the specified property values is present in the directory.", hint);
        }
    }

    /// <summary>Graph never deletes a built-in role definition, nor a custom one that role assignments still reference.</summary>
    private void RejectRoleDefinitionDelete(string collectionPath, JsonObject entity, RetryHint hint)
    {
        if (!collectionPath.Equals(RoleDefinitionsPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (entity["isBuiltIn"] is JsonValue builtIn && builtIn.GetValueKind() == JsonValueKind.True)
        {
            throw BadRequest("Built-in role definitions cannot be deleted.", hint);
        }

        string id = GetString(entity, "id")!;
        if (Collection(RoleAssignmentsPath).Any(a => ValueEquals(GetString(a, "roleDefinitionId"), id)))
        {
            throw BadRequest("The role definition is referenced by one or more role assignments and cannot be deleted.", hint);
        }
    }

    // ---- conditional access rules (authentication contexts and strengths) ----

    private const string AuthenticationContextsPath = "/identity/conditionalAccess/authenticationContextClassReferences";

    private const string AuthenticationStrengthsPath = "/policies/authenticationStrengthPolicies";

    private const string ConditionalAccessPoliciesPath = "/identity/conditionalAccess/policies";

    /// <summary>Graph never modifies or deletes a built-in authentication strength (<c>policyType: builtIn</c>).</summary>
    private static void RejectBuiltInStrengthWrite(string collectionPath, JsonObject entity, RetryHint hint)
    {
        if (collectionPath.Equals(AuthenticationStrengthsPath, StringComparison.OrdinalIgnoreCase) && ValueEquals(GetString(entity, "policyType"), "builtIn"))
        {
            throw BadRequest("Built-in authentication strength policies cannot be modified or deleted.", hint);
        }
    }

    /// <summary>
    /// The Conditional Access delete rules Graph enforces: an authentication context must be unavailable
    /// (<c>isAvailable: false</c>) and unreferenced by any policy's <c>includeAuthenticationContextClassReferences</c>;
    /// an authentication strength must be unreferenced by any policy's <c>grantControls.authenticationStrength</c>.
    /// </summary>
    private void RejectConditionalAccessDelete(string collectionPath, JsonObject entity, RetryHint hint)
    {
        string id = GetString(entity, "id")!;
        if (collectionPath.Equals(AuthenticationContextsPath, StringComparison.OrdinalIgnoreCase))
        {
            if (entity["isAvailable"] is not JsonValue available || available.GetValueKind() != JsonValueKind.False)
            {
                throw BadRequest("The authentication context class reference must be unavailable (isAvailable: false) before it can be deleted.", hint);
            }

            foreach (var policy in Collection(ConditionalAccessPoliciesPath))
            {
                if ((policy["conditions"] as JsonObject)?["applications"] is JsonObject applications
                    && applications["includeAuthenticationContextClassReferences"] is JsonArray contexts
                    && contexts.Any(c => c is JsonValue v && v.TryGetValue<string>(out var text) && ValueEquals(text, id)))
                {
                    throw BadRequest("The authentication context class reference is referenced by one or more conditional access policies.", hint);
                }
            }
        }
        else if (collectionPath.Equals(AuthenticationStrengthsPath, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var policy in Collection(ConditionalAccessPoliciesPath))
            {
                if ((policy["grantControls"] as JsonObject)?["authenticationStrength"] is JsonObject strength && ValueEquals(GetString(strength, "id"), id))
                {
                    throw BadRequest("The authentication strength policy is referenced by one or more conditional access policies.", hint);
                }
            }
        }
    }

    // ---- app role assignments and permission grants ----

    private static bool IsPrincipalCollection(string collectionPath)
    {
        return collectionPath.Equals("/users", StringComparison.OrdinalIgnoreCase)
            || collectionPath.Equals("/groups", StringComparison.OrdinalIgnoreCase)
            || collectionPath.Equals("/servicePrincipals", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Every <c>/servicePrincipals/{id}/appRoleAssignedTo</c> collection with the resource principal's id.</summary>
    private IEnumerable<(string ResourceId, List<JsonObject> Assignments)> AssignedToCollections()
    {
        foreach (var pair in _collections.ToList())
        {
            if (pair.Key.EndsWith("/appRoleAssignedTo", StringComparison.OrdinalIgnoreCase))
            {
                var segments = pair.Key.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length == 3)
                {
                    yield return (segments[1], pair.Value);
                }
            }
        }
    }

    /// <summary>
    /// <c>GET /{principals}/{id}/appRoleAssignments[/{assignmentId}]</c>, <c>POST</c> (creates under the resource's
    /// <c>appRoleAssignedTo</c>) and <c>DELETE …/{assignmentId}</c>, all over the per-resource collections.
    /// </summary>
    private (HttpStatusCode, JsonNode?) HandlePrincipalAppRoleAssignments(string method, Route route, string principalId, IReadOnlyDictionary<string, string>? query, JsonNode? body, RetryHint hint, int pageSize)
    {
        var mine = AssignedToCollections()
            .SelectMany(c => c.Assignments.Select(a => (c.Assignments, Assignment: a)))
            .Where(p => ValueEquals(GetString(p.Assignment, "principalId"), principalId))
            .ToList();

        if (route.ChildSelector is null)
        {
            switch (method)
            {
                case "GET":
                    return (HttpStatusCode.OK, Page(Filter(mine.Select(p => p.Assignment), query, hint), route.Root + "/" + principalId + "/appRoleAssignments", query, pageSize));
                case "POST":
                    {
                        if (body is not JsonObject payload)
                        {
                            throw BadRequest("A JSON object body is required.", hint);
                        }

                        string? resourceId = GetString(payload, "resourceId");
                        if (resourceId is null || Locate("/servicePrincipals", new Selector(null, resourceId)) is null)
                        {
                            throw NotFound(route.Path, hint);
                        }

                        var entity = (JsonObject)payload.DeepClone();
                        entity["principalId"] = principalId;
                        return (HttpStatusCode.Created, Create("/servicePrincipals/" + resourceId + "/appRoleAssignedTo", entity, hint));
                    }

                default:
                    throw BadRequest($"{method} is not supported on '{route.Path}'.", hint);
            }
        }

        var found = mine.FirstOrDefault(p => ValueEquals(GetString(p.Assignment, "id"), route.ChildSelector.Value));
        switch (method)
        {
            case "GET":
                return found.Assignment is null ? throw NotFound(route.Path, hint) : (HttpStatusCode.OK, found.Assignment.DeepClone());
            case "DELETE":
                if (found.Assignment is null)
                {
                    throw NotFound(route.Path, hint);
                }

                found.Assignments.Remove(found.Assignment);
                return (HttpStatusCode.NoContent, null);
            default:
                throw BadRequest($"{method} is not supported on '{route.Path}'.", hint);
        }
    }

    /// <summary>
    /// <c>POST /servicePrincipals/{id}/appRoleAssignedTo</c>: the resource is the path's principal, the principal must
    /// exist, a second assignment of the same role to the same principal is the 400 Graph returns, and the
    /// server-managed fields (<c>principalType</c>, display names, <c>createdDateTime</c>) are filled in.
    /// </summary>
    private void PrepareAppRoleAssignment(string collectionPath, JsonObject entity, RetryHint hint)
    {
        string resourceId = collectionPath.Split('/', StringSplitOptions.RemoveEmptyEntries)[1];
        var resource = Locate("/servicePrincipals", new Selector(null, resourceId)) ?? throw NotFound(collectionPath, hint);
        if (GetString(entity, "resourceId") is { } declaredResource && !ValueEquals(declaredResource, GetString(resource, "id")))
        {
            throw BadRequest("The resourceId in the body does not match the service principal addressed by the URL.", hint);
        }

        string? principalId = GetString(entity, "principalId");
        string? appRoleId = GetString(entity, "appRoleId");
        if (principalId is null || appRoleId is null)
        {
            throw BadRequest("An app role assignment requires principalId and appRoleId.", hint);
        }

        var principal = LocateById(principalId, out var principalCollection);
        if (principal is null)
        {
            throw BadRequest("PrincipalNotFound: the principal does not exist or has not replicated yet.", hint);
        }

        if (Collection(collectionPath).Any(a => ValueEquals(GetString(a, "principalId"), principalId) && ValueEquals(GetString(a, "appRoleId"), appRoleId)))
        {
            throw BadRequest("Permission being assigned already exists on the object.", hint);
        }

        entity["resourceId"] = GetString(resource, "id");
        entity["principalType"] = principalCollection.ToLowerInvariant() switch
        {
            "/users" => "User",
            "/groups" => "Group",
            "/serviceprincipals" => "ServicePrincipal",
            _ => "DirectoryObject",
        };
        entity["principalDisplayName"] = GetString(principal, "displayName");
        entity["resourceDisplayName"] = GetString(resource, "displayName");
        entity["createdDateTime"] ??= DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        entity["deletedDateTime"] = null;
    }

    /// <summary><c>POST /oauth2PermissionGrants</c>: the key properties are required and a second grant with the same (clientId, resourceId, consentType, principalId) is the 400 Graph returns.</summary>
    private void PrepareGrant(string collectionPath, JsonObject entity, RetryHint hint)
    {
        string? clientId = GetString(entity, "clientId");
        string? resourceId = GetString(entity, "resourceId");
        string? consentType = GetString(entity, "consentType");
        if (clientId is null || resourceId is null || consentType is null || GetString(entity, "scope") is null)
        {
            throw BadRequest("A permission grant requires clientId, resourceId, consentType and scope.", hint);
        }

        string? principalId = GetString(entity, "principalId");
        if (Collection(collectionPath).Any(g => ValueEquals(GetString(g, "clientId"), clientId)
            && ValueEquals(GetString(g, "resourceId"), resourceId)
            && ValueEquals(GetString(g, "consentType"), consentType)
            && ValueEquals(GetString(g, "principalId"), principalId)))
        {
            throw BadRequest("Permission entry already exists.", hint);
        }

        if (principalId is null)
        {
            entity["principalId"] = null;
        }
    }

    private void RejectDuplicate(string collectionPath, JsonObject entity, string property, RetryHint hint)
    {
        string? value = GetString(entity, property);
        if (value is not null && Collection(collectionPath).Any(o => ValueEquals(GetString(o, property), value)))
        {
            throw new GraphException(HttpStatusCode.BadRequest, "Request_BadRequest", $"Another object with the same value for property {property} already exists.", hint);
        }
    }

    private void Patch(JsonObject entity, JsonNode? body, string collectionPath, RetryHint hint)
    {
        if (body is not JsonObject payload)
        {
            throw BadRequest("A JSON object body is required.", hint);
        }

        if (collectionPath.Equals(ConditionalAccessPoliciesPath, StringComparison.OrdinalIgnoreCase) && GetString(payload, "state") is { } newState && !PolicyStates.Contains(newState))
        {
            throw BadRequest($"The value '{newState}' of property 'state' is not valid; use enabled, disabled or enabledForReportingButNotEnforced.", hint);
        }

        foreach (var property in payload)
        {
            if (property.Key.Equals("id", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (property.Key.EndsWith("@odata.bind", StringComparison.OrdinalIgnoreCase))
            {
                var bind = new JsonObject { [property.Key] = property.Value?.DeepClone(), ["id"] = GetString(entity, "id") };
                ApplyBinds(bind, collectionPath);
                continue;
            }

            string? existingKey = entity.FirstOrDefault(p => p.Key.Equals(property.Key, StringComparison.OrdinalIgnoreCase)).Key;
            string key = existingKey ?? property.Key;
            if (property.Key.Equals("uniqueName", StringComparison.OrdinalIgnoreCase) && GetString(entity, "uniqueName") is { } current && !ValueEquals(current, GetString(payload, "uniqueName")))
            {
                throw BadRequest("Property uniqueName is immutable once set.", hint);
            }

            if (property.Value is null || property.Value.GetValueKind() == JsonValueKind.Null)
            {
                entity.Remove(key);
            }
            else
            {
                entity[key] = property.Value.DeepClone();
            }
        }

        _ = collectionPath;
    }

    private static JsonObject Merge(JsonObject target, JsonNode? body)
    {
        if (body is JsonObject payload)
        {
            foreach (var property in payload)
            {
                target[property.Key] = property.Value?.DeepClone();
            }
        }

        return target;
    }

    /// <summary><c>POST /directory/deletedItems/{id}/restore</c>: the object goes back to the collection its <c>@odata.type</c> names.</summary>
    private JsonObject Restore(JsonObject entity, string collectionPath, RetryHint hint)
    {
        if (!collectionPath.Equals(DeletedItemsPath, StringComparison.OrdinalIgnoreCase))
        {
            return (JsonObject)entity.DeepClone();
        }

        string? odataType = GetString(entity, "@odata.type");
        if (odataType is null || !CollectionByODataType.TryGetValue(odataType, out var target))
        {
            throw BadRequest("The deleted item has no restorable @odata.type.", hint);
        }

        var restored = (JsonObject)entity.DeepClone();
        restored.Remove("deletedDateTime");
        restored.Remove("@odata.type");

        // The keys are free again only if nobody took them while the object sat in the recycle bin.
        foreach (string key in new[] { "uniqueName", "userPrincipalName", "appId" })
        {
            if (GetString(restored, key) is { } value && Collection(target).Any(o => ValueEquals(GetString(o, key), value)))
            {
                throw new GraphException(HttpStatusCode.BadRequest, "Request_BadRequest", $"Another object with the same value for property {key} already exists.", hint);
            }
        }

        Collection(collectionPath).Remove(entity);
        Collection(target).Add(restored);
        return (JsonObject)restored.DeepClone();
    }

    // ---- soft delete ----

    /// <summary>
    /// Graph soft-deletes applications, service principals, users, administrative units, Microsoft 365 groups AND security groups (Learn group-delete:
    /// "both Microsoft 365 and security groups are moved to a temporary container and can be restored within 30 days ... This doesn't apply to Distribution
    /// groups which are permanently deleted immediately"); distribution lists and every other type are deleted permanently.
    /// </summary>
    private static bool IsSoftDeletable(string collectionPath, JsonObject entity)
    {
        if (!ODataTypeByCollection.ContainsKey(collectionPath))
        {
            return false;
        }

        return !collectionPath.Equals("/groups", StringComparison.OrdinalIgnoreCase) || !IsDistributionList(entity);
    }

    /// <summary>Mail-enabled, not security-enabled, no <c>Unified</c>: the one group kind that is deleted immediately.</summary>
    private static bool IsDistributionList(JsonObject entity)
    {
        return !IsUnified(entity) && GetBool(entity, "mailEnabled") == true && GetBool(entity, "securityEnabled") != true;
    }

    private static bool? GetBool(JsonObject entity, string property)
    {
        foreach (var pair in entity)
        {
            if (pair.Key.Equals(property, StringComparison.OrdinalIgnoreCase) && pair.Value is JsonValue value)
            {
                return value.GetValueKind() switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => null,
                };
            }
        }

        return null;
    }
    private static bool IsUnified(JsonObject entity)
    {
        return entity["groupTypes"] is JsonArray types && types.Any(t => t is JsonValue v && v.TryGetValue<string>(out var text) && text.Equals("Unified", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Deleting an application also soft-deletes its service principal (Graph cascades the home principal).</summary>
    private void SoftDeleteWithCascade(JsonObject entity, string collectionPath)
    {
        string id = GetString(entity, "id")!;
        if (collectionPath.Equals("/applications", StringComparison.OrdinalIgnoreCase) && GetString(entity, "appId") is { } appId)
        {
            foreach (var principal in Collection("/servicePrincipals").Where(p => ValueEquals(GetString(p, "appId"), appId)).ToList())
            {
                SoftDelete(GetString(principal, "id")!);
            }
        }

        SoftDelete(id);
    }

    /// <summary>A key held by a soft-deleted object of the same type conflicts like a live one until the object is restored or purged.</summary>
    private void RejectDeletedDuplicate(string collectionPath, JsonObject entity, string property, RetryHint hint)
    {
        string? value = GetString(entity, property);
        if (value is null || !ODataTypeByCollection.TryGetValue(collectionPath, out var odataType))
        {
            return;
        }

        if (Collection(DeletedItemsPath).Any(d => ValueEquals(GetString(d, "@odata.type"), odataType) && ValueEquals(GetString(d, property), value)))
        {
            throw new GraphException(HttpStatusCode.BadRequest, "Request_BadRequest", $"Another object with the same value for property {property} already exists.", hint);
        }
    }

    private void PurgeExpiredDeletedItems()
    {
        if (!_collections.TryGetValue(DeletedItemsPath, out var deleted) || deleted.Count == 0)
        {
            return;
        }

        var cutoff = Clock.GetUtcNow() - DeletedRetention;
        deleted.RemoveAll(d => GetString(d, "deletedDateTime") is { } stamp
            && DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when)
            && when < cutoff);
    }

    // ---- advanced queries ----

    /// <summary>The largest <c>$top</c> Graph accepts on a directory collection (users, groups, applications, servicePrincipals, administrative units, deleted items).</summary>
    private const int MaxDirectoryPageSize = 999;

    private static readonly HashSet<string> DirectoryObjectRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        "users", "groups", "applications", "servicePrincipals", "directoryObjects", "directory/administrativeUnits", "directory/deletedItems",
    };

    /// <summary>
    /// Graph rejects <c>$search</c>, <c>$count</c> and the advanced <c>$filter</c> operators (<c>ne</c>, <c>endswith</c>, <c>not</c>) on
    /// directory objects unless <c>ConsistencyLevel: eventual</c> is sent, and a <c>$filter</c> of that kind also needs <c>$count=true</c>.
    /// </summary>
    private static void EnforceQueryRules(Route route, IReadOnlyDictionary<string, string>? query, IReadOnlyDictionary<string, string>? headers, RetryHint hint)
    {
        if (query is null || query.Count == 0 || !DirectoryObjectRoots.Contains(route.Root))
        {
            return;
        }

        if (query.TryGetValue("$top", out var top) && int.TryParse(top, NumberStyles.Integer, CultureInfo.InvariantCulture, out var topValue) && topValue > MaxDirectoryPageSize)
        {
            throw new GraphException(HttpStatusCode.BadRequest, "Request_UnsupportedQuery", $"Invalid page size specified: '{topValue}'. Must be between 1 and {MaxDirectoryPageSize} inclusive.", hint);
        }

        bool search = query.ContainsKey("$search");
        bool count = query.TryGetValue("$count", out var countValue) && countValue.Equals("true", StringComparison.OrdinalIgnoreCase);
        query.TryGetValue("$filter", out var filter);
        bool advancedFilter = !string.IsNullOrWhiteSpace(filter) && AdvancedFilter.IsMatch(filter);
        if (!search && !count && !advancedFilter)
        {
            return;
        }

        bool eventual = headers is not null && headers.Any(h => h.Key.Equals("ConsistencyLevel", StringComparison.OrdinalIgnoreCase) && h.Value.Equals("eventual", StringComparison.OrdinalIgnoreCase));
        if (!eventual)
        {
            string what = search ? "$search" : count ? "$count" : "this $filter";
            throw new GraphException(HttpStatusCode.BadRequest, "Request_UnsupportedQuery", $"The request with {what} is only supported on directory objects with the header 'ConsistencyLevel: eventual'.", hint);
        }

        if (advancedFilter && !count)
        {
            throw new GraphException(HttpStatusCode.BadRequest, "Request_UnsupportedQuery", "Operator applied to the $filter property is not supported without $count=true (advanced query).", hint);
        }
    }

    private static readonly System.Text.RegularExpressions.Regex AdvancedFilter = new(@"(\s(ne)\s)|endswith\s*\(|(^|\s|\()not(\s|\()", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private void RemoveReferencesTo(string id)
    {
        foreach (var list in _references.Values)
        {
            list.RemoveAll(r => ValueEquals(r, id));
        }

        foreach (var key in _singleReferences.Where(p => ValueEquals(p.Value, id)).Select(p => p.Key).ToList())
        {
            _singleReferences.Remove(key);
        }

        foreach (var key in _collections.Keys.Where(k => k.Contains("/" + id + "/", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            _collections.Remove(key);
        }

        // Graph cascades: a deleted principal loses its app role assignments and permission grants.
        foreach (var (_, assignments) in AssignedToCollections())
        {
            assignments.RemoveAll(a => ValueEquals(GetString(a, "principalId"), id));
        }

        if (_collections.TryGetValue("/oauth2PermissionGrants", out var grants))
        {
            grants.RemoveAll(g => ValueEquals(GetString(g, "clientId"), id) || ValueEquals(GetString(g, "resourceId"), id) || ValueEquals(GetString(g, "principalId"), id));
        }

        // ... and its directory role assignments.
        if (_collections.TryGetValue(RoleAssignmentsPath, out var roleAssignments))
        {
            roleAssignments.RemoveAll(a => ValueEquals(GetString(a, "principalId"), id));
        }
    }

    // ---- actions ----

    private static JsonObject AddPassword(JsonObject entity, JsonNode? body)
    {
        var request = (body as JsonObject)?["passwordCredential"] as JsonObject ?? new JsonObject();

        // Everything the caller sent is stored (Graph keeps displayName/start/end; the fake keeps extras so a
        // round-trip diff of the echo provider is a no-op), plus the server-assigned fields.
        var credential = (JsonObject)request.DeepClone();
        credential["keyId"] = Guid.NewGuid().ToString("D");
        credential["startDateTime"] ??= JsonValue.Create(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        credential["endDateTime"] ??= JsonValue.Create(DateTimeOffset.UtcNow.AddDays(180).ToString("O", CultureInfo.InvariantCulture));
        credential["hint"] = "fak";
        credential["customKeyIdentifier"] = null;

        if (entity["passwordCredentials"] is not JsonArray credentials)
        {
            credentials = [];
            entity["passwordCredentials"] = credentials;
        }

        credentials.Add(credential.DeepClone());
        var response = (JsonObject)credential.DeepClone();
        response["secretText"] = "fake-secret-" + Guid.NewGuid().ToString("N");
        return response;
    }

    private static void RemovePassword(JsonObject entity, JsonNode? body)
    {
        string? keyId = (body as JsonObject)?["keyId"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        if (keyId is null || entity["passwordCredentials"] is not JsonArray credentials)
        {
            return;
        }

        for (int i = credentials.Count - 1; i >= 0; i--)
        {
            if (credentials[i] is JsonObject credential && ValueEquals(GetString(credential, "keyId"), keyId))
            {
                credentials.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// <c>POST …/assignLicense</c>. For users (<paramref name="trackStates"/>) the fake also keeps
    /// <c>licenseAssignmentStates</c> like Graph does: every direct add gets an entry with <c>assignedByGroup: null</c>
    /// and a direct remove drops that entry only, so a seeded group-inherited state (<c>assignedByGroup</c> set)
    /// survives and lets the users provider tell direct from inherited assignments.
    /// </summary>
    private static void AssignLicense(JsonObject entity, JsonNode? body, bool trackStates = false)
    {
        if (body is not JsonObject payload)
        {
            return;
        }

        if (entity["assignedLicenses"] is not JsonArray licenses)
        {
            licenses = [];
            entity["assignedLicenses"] = licenses;
        }

        JsonArray? states = null;
        if (trackStates)
        {
            if (entity["licenseAssignmentStates"] is not JsonArray existingStates)
            {
                existingStates = [];
                entity["licenseAssignmentStates"] = existingStates;
            }

            states = existingStates;
        }

        if (payload["removeLicenses"] is JsonArray removals)
        {
            foreach (var removal in removals)
            {
                string? skuId = removal is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
                for (int i = licenses.Count - 1; i >= 0; i--)
                {
                    if (licenses[i] is JsonObject license && ValueEquals(GetString(license, "skuId"), skuId))
                    {
                        licenses.RemoveAt(i);
                    }
                }

                if (states is not null)
                {
                    for (int i = states.Count - 1; i >= 0; i--)
                    {
                        if (states[i] is JsonObject state && ValueEquals(GetString(state, "skuId"), skuId) && GetString(state, "assignedByGroup") is null)
                        {
                            states.RemoveAt(i);
                        }
                    }
                }
            }
        }

        if (payload["addLicenses"] is JsonArray additions)
        {
            foreach (var addition in additions)
            {
                if (addition is JsonObject license)
                {
                    licenses.Add(license.DeepClone());
                    if (states is not null)
                    {
                        states.Add(new JsonObject
                        {
                            ["skuId"] = GetString(license, "skuId"),
                            ["disabledPlans"] = license["disabledPlans"]?.DeepClone() ?? new JsonArray(),
                            ["assignedByGroup"] = null,
                            ["state"] = "Active",
                            ["error"] = "None",
                        });
                    }
                }
            }
        }
    }

    // ---- $select and default property sets ----

    private static IReadOnlyList<string>? SelectOf(IReadOnlyDictionary<string, string>? query)
    {
        if (query is null || !query.TryGetValue("$select", out var select) || string.IsNullOrWhiteSpace(select))
        {
            return null;
        }

        var names = select.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return names.Length == 0 ? null : names;
    }

    /// <summary>Graph answers a <c>$select</c> of a property the type does not have with a 400, even when nothing matches.</summary>
    private static void ValidateSelect(FakeDirectorySchema schema, IReadOnlyList<string>? select, RetryHint hint)
    {
        if (select is null)
        {
            return;
        }

        foreach (var name in select)
        {
            if (name != "*" && !schema.IsKnown(name))
            {
                throw new GraphException(HttpStatusCode.BadRequest, "Request_BadRequest", $"Could not find a property named '{name}' on type '{schema.Name}'.", hint);
            }
        }
    }

    /// <summary>
    /// What Graph returns for a typed object: <c>id</c>, the <c>@odata.*</c> annotations and either the selected properties or, without <c>$select</c>, only the
    /// type's default property set (stored properties outside both are left out; a selected property the object holds no value for is left out too, where Graph would
    /// answer <c>null</c>: keeping the stored order and omitting it avoids a diff against objects seeded without it). Untyped objects, and <c>$select=*</c>, are returned whole.
    /// </summary>
    private static JsonObject ProjectObject(JsonObject entity, FakeDirectorySchema? schema, IReadOnlyList<string>? select)
    {
        if (schema is null || (select is not null && select.Contains("*")))
        {
            return (JsonObject)entity.DeepClone();
        }

        IReadOnlyCollection<string> wanted = select ?? schema.Default;
        var result = new JsonObject();
        foreach (var pair in entity)
        {
            if (pair.Key.Contains('@', StringComparison.Ordinal) || pair.Key.Equals("id", StringComparison.OrdinalIgnoreCase) || wanted.Contains(pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                result[pair.Key] = pair.Value?.DeepClone();
            }
        }

        return result;
    }
    private static List<JsonObject> Project(IEnumerable<JsonObject> items, FakeDirectorySchema? schema, IReadOnlyDictionary<string, string>? query, RetryHint hint)
    {
        var list = items.ToList();
        if (schema is null)
        {
            return list;
        }

        var select = SelectOf(query);
        ValidateSelect(schema, select, hint);
        return list.Select(o => ProjectObject(o, schema, select)).ToList();
    }

    private static JsonNode ProjectOne(JsonObject entity, FakeDirectorySchema? schema, IReadOnlyDictionary<string, string>? query, RetryHint hint)
    {
        if (schema is null)
        {
            return entity.DeepClone();
        }

        var select = SelectOf(query);
        ValidateSelect(schema, select, hint);
        return ProjectObject(entity, schema, select);
    }

    /// <summary>A navigation that mixes object types (members, owners): each object is projected by its own type.</summary>
    private List<JsonObject> ProjectMixed(IEnumerable<JsonObject> items, IReadOnlyDictionary<string, string>? query)
    {
        var select = SelectOf(query);
        return items.Select(o => ProjectObject(o, SchemaOfId(GetString(o, "id")), select?.Where(n => SchemaOfId(GetString(o, "id"))?.IsKnown(n) != false).ToList())).ToList();
    }

    private FakeDirectorySchema? SchemaOfId(string? id)
    {
        return id is not null && LocateById(id, out var collectionPath) is not null ? FakeDirectorySchema.ForCollection(collectionPath) : null;
    }

    // ---- required properties ----

    private static readonly HashSet<string> PolicyStates = new(StringComparer.OrdinalIgnoreCase) { "enabled", "disabled", "enabledForReportingButNotEnforced" };

    /// <summary>
    /// The properties Graph requires on create (Learn: group-post-groups / group-upsert, user-post-users, application-post-applications,
    /// directory-post-administrativeunits, rbacapplication-post-roledefinitions, conditionalaccessroot-post-policies / post-namedlocations).
    /// A body without them is a 400, not an empty object.
    /// </summary>
    private static void RequireProperties(string root, JsonObject entity, RetryHint hint)
    {
        var missing = new List<string>();
        void Need(string property)
        {
            if (JsonObjects_IsAbsent(entity, property))
            {
                missing.Add(property);
            }
        }

        switch (root)
        {
            case "/applications":
            case "/directory/administrativeunits":
            case "/rolemanagement/directory/roledefinitions":
                Need("displayName");
                break;
            case "/groups":
                Need("displayName");
                Need("mailNickname");
                Need("mailEnabled");
                Need("securityEnabled");
                break;
            case "/users":
                Need("accountEnabled");
                Need("displayName");
                Need("mailNickname");
                Need("passwordProfile");
                if (entity.FirstOrDefault(p => p.Key.Equals("passwordProfile", StringComparison.OrdinalIgnoreCase)).Value is JsonObject profile && JsonObjects_IsAbsent(profile, "password"))
                {
                    missing.Add("passwordProfile.password");
                }

                break;
            case "/identity/conditionalaccess/policies":
                Need("displayName");
                Need("state");
                if (GetString(entity, "state") is { } state && !PolicyStates.Contains(state))
                {
                    throw BadRequest($"The value '{state}' of property 'state' is not valid; use enabled, disabled or enabledForReportingButNotEnforced.", hint);
                }

                break;
            case "/identity/conditionalaccess/namedlocations":
                Need("displayName");
                string? type = GetString(entity, "@odata.type");
                if (type is null)
                {
                    missing.Add("@odata.type");
                }
                else if (type.Equals("#microsoft.graph.ipNamedLocation", StringComparison.OrdinalIgnoreCase))
                {
                    Need("ipRanges");
                }
                else if (type.Equals("#microsoft.graph.countryNamedLocation", StringComparison.OrdinalIgnoreCase))
                {
                    Need("countriesAndRegions");
                }
                else if (!type.Equals("#microsoft.graph.compliantNetworkNamedLocation", StringComparison.OrdinalIgnoreCase))
                {
                    throw BadRequest($"The @odata.type '{type}' is not a named location type (use #microsoft.graph.ipNamedLocation or #microsoft.graph.countryNamedLocation).", hint);
                }

                break;
        }

        if (missing.Count > 0)
        {
            throw BadRequest($"Required properties are missing from the request body: {string.Join(", ", missing)}.", hint);
        }
    }

    private static bool JsonObjects_IsAbsent(JsonObject entity, string property)
    {
        var pair = entity.FirstOrDefault(p => p.Key.Equals(property, StringComparison.OrdinalIgnoreCase));
        return pair.Key is null || pair.Value is null || (pair.Value is JsonValue value && value.TryGetValue<string>(out var text) && string.IsNullOrWhiteSpace(text));
    }
    // ---- query ----

    private static List<JsonObject> Filter(IEnumerable<JsonObject> objects, IReadOnlyDictionary<string, string>? query, RetryHint hint)
    {
        var list = objects.ToList();
        if (query is null || !query.TryGetValue("$filter", out var filter) || string.IsNullOrWhiteSpace(filter))
        {
            return list;
        }

        var clauses = filter.Split(" and ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var clause in clauses)
        {
            var negation = System.Text.RegularExpressions.Regex.Match(clause, @"^\s*not\s*\((?<inner>.*)\)\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            string text = negation.Success ? negation.Groups["inner"].Value : clause;
            var before = list;
            var match = FilterClause.Match(text);
            if (!match.Success)
            {
                throw new GraphException(HttpStatusCode.BadRequest, "Request_UnsupportedQuery", $"Unsupported $filter clause '{clause}' (the fake supports eq, ne, startswith and endswith joined by and).", hint);
            }

            string property = match.Groups["prop"].Success ? match.Groups["prop"].Value.Trim() : match.Groups["fprop"].Value.Trim();
            string literal = (match.Groups["lit"].Success ? match.Groups["lit"].Value : match.Groups["flit"].Value).Trim();
            string? expected = literal.Equals("null", StringComparison.OrdinalIgnoreCase)
                ? null
                : literal.StartsWith('\'') && literal.EndsWith('\'') && literal.Length >= 2
                    ? literal[1..^1].Replace("''", "'", StringComparison.Ordinal)
                    : literal;
            string op = match.Groups["op"].Success ? match.Groups["op"].Value.ToLowerInvariant() : match.Groups["fn"].Value.ToLowerInvariant();
            list = op switch
            {
                "eq" => list.Where(o => ValueEquals(GetScalar(o, property), expected)).ToList(),
                "ne" => list.Where(o => !ValueEquals(GetScalar(o, property), expected)).ToList(),
                "startswith" => list.Where(o => GetScalar(o, property)?.StartsWith(expected ?? string.Empty, StringComparison.OrdinalIgnoreCase) == true).ToList(),
                _ => list.Where(o => GetScalar(o, property)?.EndsWith(expected ?? string.Empty, StringComparison.OrdinalIgnoreCase) == true).ToList(),
            };
            if (negation.Success)
            {
                list = before.Where(o => !list.Contains(o)).ToList();
            }
        }

        return list;
    }

    private static readonly System.Text.RegularExpressions.Regex FilterClause = new(
        @"^\s*(?:(?<prop>[A-Za-z_@][\w.@/]*)\s+(?<op>eq|ne)\s+(?<lit>'(?:[^']|'')*'|[^\s'()]+)|(?<fn>startswith|endswith)\s*\(\s*(?<fprop>[A-Za-z_@][\w.@/]*)\s*,\s*(?<flit>'(?:[^']|'')*')\s*\))\s*$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static JsonObject Page(List<JsonObject> objects, string relativePath, IReadOnlyDictionary<string, string>? query, int pageSize)
    {
        int skip = 0;
        if (query is not null && query.TryGetValue("$skiptoken", out var token) && int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            skip = Math.Max(0, parsed);
        }

        int size = pageSize;
        if (query is not null && query.TryGetValue("$top", out var top) && int.TryParse(top, NumberStyles.Integer, CultureInfo.InvariantCulture, out var topValue) && topValue > 0)
        {
            size = Math.Min(size, topValue);
        }

        var page = new JsonArray();
        foreach (var item in objects.Skip(skip).Take(size))
        {
            page.Add(item.DeepClone());
        }

        var result = new JsonObject
        {
            ["@odata.context"] = "https://graph.microsoft.com/v1.0/$metadata#" + relativePath,
            ["value"] = page,
        };

        if (skip + size < objects.Count)
        {
            var parameters = new List<string> { "$skiptoken=" + (skip + size).ToString(CultureInfo.InvariantCulture) };
            if (query is not null)
            {
                foreach (var pair in query)
                {
                    if (pair.Key is not "$skiptoken")
                    {
                        parameters.Add(pair.Key + "=" + Uri.EscapeDataString(pair.Value));
                    }
                }
            }

            result["@odata.nextLink"] = "https://graph.microsoft.com/v1.0/" + relativePath + "?" + string.Join("&", parameters);
        }

        return result;
    }

    // ---- helpers ----

    private static bool PrefersCreateIfMissing(IReadOnlyDictionary<string, string>? headers)
    {
        return headers is not null && headers.Any(h => h.Key.Equals("Prefer", StringComparison.OrdinalIgnoreCase) && h.Value.Contains("create-if-missing", StringComparison.OrdinalIgnoreCase));
    }

    private static string ReferenceTarget(JsonNode? body, RetryHint hint)
    {
        string? odataId = (body as JsonObject)?["@odata.id"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        if (string.IsNullOrEmpty(odataId))
        {
            throw BadRequest("A $ref body needs '@odata.id'.", hint);
        }

        return odataId.TrimEnd('/')[(odataId.TrimEnd('/').LastIndexOf('/') + 1)..];
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

    // ---- routing ----

    /// <summary>An entity selector: an id/UPN segment or an alternate key <c>(property='value')</c>.</summary>
    private sealed record Selector(string? Property, string Value)
    {
        public bool IsAlternateKey => Property is not null;
    }

    private sealed class Route
    {
        public required string Path { get; init; }

        /// <summary>The collection root without leading slash, canonical casing (<c>applications</c>, <c>directory/administrativeUnits</c>).</summary>
        public required string Root { get; init; }

        public Selector? Selector { get; init; }

        public string? Nav { get; init; }

        public Selector? ChildSelector { get; init; }

        public bool IsRef { get; init; }

        public static Route? Parse(string path, string[] knownRoots)
        {
            string trimmed = path.Trim();
            if (trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                int apiIndex = trimmed.IndexOf("/v1.0/", StringComparison.OrdinalIgnoreCase);
                if (apiIndex < 0)
                {
                    apiIndex = trimmed.IndexOf("/beta/", StringComparison.OrdinalIgnoreCase);
                }

                if (apiIndex >= 0)
                {
                    trimmed = trimmed[(apiIndex + 6)..];
                }
            }

            var segments = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length == 0)
            {
                return null;
            }

            foreach (var root in knownRoots.OrderByDescending(r => r.Length))
            {
                var rootSegments = root.Split('/');
                if (segments.Length < rootSegments.Length)
                {
                    continue;
                }

                bool match = true;
                string? rootKey = null;
                for (int i = 0; i < rootSegments.Length; i++)
                {
                    var (name, key) = SplitKey(segments[i]);
                    if (!name.Equals(rootSegments[i], StringComparison.OrdinalIgnoreCase) || (key is not null && i != rootSegments.Length - 1))
                    {
                        match = false;
                        break;
                    }

                    rootKey = key;
                }

                if (!match)
                {
                    continue;
                }

                int next = rootSegments.Length;
                Selector? selector = null;
                if (rootKey is not null)
                {
                    selector = ParseKey(rootKey);
                }
                else if (next < segments.Length)
                {
                    selector = new Selector(null, Decode(segments[next]));
                    next++;
                }

                string? nav = null;
                Selector? childSelector = null;
                bool isRef = false;
                if (next < segments.Length)
                {
                    var (navName, navKey) = SplitKey(segments[next]);
                    nav = navName;
                    next++;
                    if (navKey is not null)
                    {
                        childSelector = ParseKey(navKey);
                    }

                    if (next < segments.Length && segments[next] == "$ref")
                    {
                        isRef = true;
                        next++;
                    }
                    else if (next < segments.Length)
                    {
                        childSelector ??= new Selector(null, Decode(segments[next]));
                        next++;
                        if (next < segments.Length && segments[next] == "$ref")
                        {
                            isRef = true;
                            next++;
                        }
                    }
                }

                if (next < segments.Length)
                {
                    return null;
                }

                return new Route { Path = path, Root = root, Selector = selector, Nav = nav, ChildSelector = childSelector, IsRef = isRef };
            }

            return null;
        }

        private static (string Name, string? Key) SplitKey(string segment)
        {
            int open = segment.IndexOf('(');
            if (open > 0 && segment.EndsWith(')'))
            {
                return (segment[..open], segment[(open + 1)..^1]);
            }

            return (segment, null);
        }

        private static Selector ParseKey(string key)
        {
            int eq = key.IndexOf('=');
            if (eq <= 0)
            {
                return new Selector(null, Decode(key.Trim('\'')));
            }

            string property = key[..eq].Trim();
            string value = key[(eq + 1)..].Trim();
            if (value.StartsWith('\'') && value.EndsWith('\'') && value.Length >= 2)
            {
                value = value[1..^1];
            }

            return new Selector(property, Decode(value).Replace("''", "'", StringComparison.Ordinal));
        }

        private static string Decode(string segment)
        {
            return Uri.UnescapeDataString(segment);
        }
    }
}
