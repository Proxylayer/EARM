using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using EArm.Graph;

namespace EArm.TestSupport;

/// <summary>The navigation routes of <see cref="FakeDirectory"/>: actions, child collections and the reference navigations (<c>members</c>, <c>owners</c>, <c>manager</c>...).</summary>
public sealed partial class FakeDirectory
{
    private (HttpStatusCode, JsonNode?) HandleNavigation(string method, Route route, IReadOnlyDictionary<string, string>? query, IReadOnlyDictionary<string, string>? headers, JsonNode? body, RetryHint hint, int pageSize, bool beta)
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
            return HandleReferenceNavigation(method, route, navPath, nav, query, body, hint, pageSize, beta);
        }

        // Child collection (federatedIdentityCredentials, appRoleAssignedTo, appRoleAssignments...).
        if (route.ChildSelector is null)
        {
            return method switch
            {
                "GET" => (HttpStatusCode.OK, Page(Project(Filter(Collection(navPath), query, hint), FakeDirectorySchema.ForCollection(navPath), query, hint), route.Root + "/" + entityId + "/" + nav, query, pageSize)),
                "POST" => (HttpStatusCode.Created, Create(navPath, PrepareChildCreate(entity, nav, navPath, body, hint), hint)),
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

    /// <summary>
    /// A reference navigation. Listing it models two real-tenant behaviours (probe h, GroupMembershipProbes): a type cast
    /// (<c>/groups/{id}/members/microsoft.graph.servicePrincipal</c>) lists only the objects of that type, and v1.0
    /// <c>GET /groups/{id}/members</c> omits service principal members (beta, the type cast and <c>$expand=members</c> list them). v1.0
    /// <c>GET /groups/{id}/owners</c> omits service principal owners the same way (Learn, group-list-owners: "service principals are not listed as group
    /// owners"); application and service principal owners are listed whole.
    /// </summary>
    private (HttpStatusCode, JsonNode?) HandleReferenceNavigation(string method, Route route, string navPath, string nav, IReadOnlyDictionary<string, string>? query, JsonNode? body, RetryHint hint, int pageSize, bool beta)
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

        string? cast = route.ChildSelector is { IsAlternateKey: false } selector && selector.Value.StartsWith("microsoft.graph.", StringComparison.OrdinalIgnoreCase) ? "#" + selector.Value : null;
        bool omitServicePrincipals = cast is null && !beta && (nav.Equals("members", StringComparison.OrdinalIgnoreCase) || nav.Equals("owners", StringComparison.OrdinalIgnoreCase)) && route.Root.Equals("groups", StringComparison.OrdinalIgnoreCase);
        var objects = new List<JsonObject>();
        foreach (var id in ReferencedIds(navPath, nav))
        {
            if (LocateById(id, out _) is { } referenced)
            {
                var typed = WithODataType(referenced);
                string? type = GetString(typed, "@odata.type");
                if ((cast is not null && !string.Equals(type, cast, StringComparison.OrdinalIgnoreCase))
                    || (omitServicePrincipals && string.Equals(type, "#microsoft.graph.servicePrincipal", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                objects.Add(typed);
            }
        }

        string listPath = route.Root + "/" + GetString(Locate("/" + route.Root, route.Selector!)!, "id") + "/" + nav + (cast is null ? string.Empty : "/" + cast[1..]);
        return (HttpStatusCode.OK, Page(ProjectMixed(Filter(objects, query, hint), query), listPath, query, pageSize));
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

    private static string ReferenceTarget(JsonNode? body, RetryHint hint)
    {
        string? odataId = (body as JsonObject)?["@odata.id"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        if (string.IsNullOrEmpty(odataId))
        {
            throw BadRequest("A $ref body needs '@odata.id'.", hint);
        }

        return odataId.TrimEnd('/')[(odataId.TrimEnd('/').LastIndexOf('/') + 1)..];
    }

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
}
