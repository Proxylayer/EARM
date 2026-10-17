using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using EArm.Graph;

namespace EArm.TestSupport;

/// <summary>Directory role assignments and definitions, app role assignments and OAuth2 permission grants in <see cref="FakeDirectory"/>.</summary>
public sealed partial class FakeDirectory
{
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
}
