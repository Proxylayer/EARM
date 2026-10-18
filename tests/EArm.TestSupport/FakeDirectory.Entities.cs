using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using EArm.Graph;

namespace EArm.TestSupport;

/// <summary>The collection and entity routes of <see cref="FakeDirectory"/>: list, create (with Graph's required properties and duplicate keys), get, patch, delete, and <c>/me</c>.</summary>
public sealed partial class FakeDirectory
{
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

            foreach (var roles in new[] { RoleEligibility, RoleAssignmentSchedules })
            {
                if (roles.IsRequests(collectionPath))
                {
                    return method == "POST"
                        ? (HttpStatusCode.Created, roles.Submit(body, Collection(roles.SchedulesPath), LocatePrincipal, hint))
                        : throw MethodNotAllowed($"{method} is not supported on '{collectionPath}' by the fake.", hint);
                }

                if (roles.IsSchedules(collectionPath))
                {
                    if (method != "GET")
                    {
                        throw MethodNotAllowed($"{method} is not supported on '{collectionPath}': schedules change only through schedule requests.", hint);
                    }

                    roles.BeforeList(Collection(collectionPath), query, hint);
                }
            }

            foreach (var groups in new[] { GroupEligibility, GroupAssignmentSchedules })
            {
                if (groups.IsRequests(collectionPath))
                {
                    return method == "POST"
                        ? (HttpStatusCode.Created, groups.Submit(body, Collection(groups.SchedulesPath), LocatePrincipal, hint))
                        : throw MethodNotAllowed($"{method} is not supported on '{collectionPath}' by the fake.", hint);
                }

                if (groups.IsSchedules(collectionPath))
                {
                    if (method != "GET")
                    {
                        throw MethodNotAllowed($"{method} is not supported on '{collectionPath}': schedules change only through schedule requests.", hint);
                    }

                    groups.BeforeList(query, hint);
                }
            }

            if (method == "POST" && FakeAuthenticationMethodsPolicy.IsCollection(collectionPath))
            {
                throw MethodNotAllowed("The authentication method configurations are tenant singletons; they cannot be created.", hint);
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
                if (entity is not null && _staleAuthenticationMethodReads.TryGetValue(GetString(entity, "id") ?? string.Empty, out var stale) && FakeAuthenticationMethodsPolicy.IsCollection(collectionPath))
                {
                    if (--stale.ReadsLeft <= 0)
                    {
                        _staleAuthenticationMethodReads.Remove(GetString(entity, "id")!);
                    }

                    return (HttpStatusCode.OK, ProjectOne(stale.Before, FakeDirectorySchema.ForCollection(collectionPath), query, hint));
                }

                return entity is null ? throw NotFound(route.Path, hint) : (HttpStatusCode.OK, ProjectOne(entity, collectionPath.Equals(DeletedItemsPath, StringComparison.OrdinalIgnoreCase) ? FakeDirectorySchema.ForODataType(GetString(entity, "@odata.type")) : FakeDirectorySchema.ForCollection(collectionPath), query, hint));

            case "PATCH":
                if (entity is null)
                {
                    // Alternate-key upsert (applications, groups: Prefer: create-if-missing) or id upsert
                    // (authenticationContextClassReferences: Graph creates on PATCH /{id} without any header).
                    if (!FakeAuthenticationMethodsPolicy.IsCollection(collectionPath) && (PrefersCreateIfMissing(headers) || collectionPath.Equals(AuthenticationContextsPath, StringComparison.OrdinalIgnoreCase)))
                    {
                        var created = Create(collectionPath, Merge(new JsonObject(), body), hint, route.Selector);
                        return (HttpStatusCode.Created, created);
                    }

                    throw NotFound(route.Path, hint);
                }

                RejectBuiltInStrengthWrite(collectionPath, entity, hint);
                if (FakeAuthenticationMethodsPolicy.IsCollection(collectionPath))
                {
                    FakeAuthenticationMethodsPolicy.RequireODataType(entity, body, hint);
                    if (AuthenticationMethodStaleReads > 0)
                    {
                        _staleAuthenticationMethodReads[GetString(entity, "id")!] = new StaleRead((JsonObject)entity.DeepClone()) { ReadsLeft = AuthenticationMethodStaleReads };
                    }
                }

                Patch(entity, PrepareKeyCredentialsPatch(collectionPath, body, hint), collectionPath, hint);
                return (HttpStatusCode.NoContent, null);

            case "PUT":
                // Graph has no PUT on an entity (only PUT /users/{id}/manager/$ref and a few relationship links).
                throw MethodNotAllowed($"PUT is not supported on '{route.Path}'.", hint);

            case "DELETE":
                if (entity is null)
                {
                    throw NotFound(route.Path, hint);
                }

                if (FakeAuthenticationMethodsPolicy.IsCollection(collectionPath))
                {
                    // Graph's DELETE reverts the configuration to its default; it never removes it.
                    var configurations = Collection(collectionPath);
                    configurations[configurations.IndexOf(entity)] = FakeAuthenticationMethodsPolicy.ResetDefault(entity, hint);
                    return (HttpStatusCode.NoContent, null);
                }

                RejectBuiltInStrengthWrite(collectionPath, entity, hint);
                RejectConditionalAccessDelete(collectionPath, entity, hint);
                RejectRoleDefinitionDelete(collectionPath, entity, hint);
                RecordReferencesLingering(collectionPath, entity);
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
            entity["signInAudience"] ??= "AzureADMyOrg"; // Learn: the default value of signInAudience
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

        if (ConditionalAccessLagAttempts > 0
            && collectionPath.Equals(NamedLocationsPath, StringComparison.OrdinalIgnoreCase)
            && entity["isTrusted"] is JsonValue wasTrusted && wasTrusted.GetValueKind() == JsonValueKind.True
            && payload["isTrusted"] is JsonValue nowTrusted && nowTrusted.GetValueKind() == JsonValueKind.False)
        {
            // The unmark is stored at once, but the delete keeps being refused until it has propagated.
            _lingeringTrust[GetString(entity, "id")!] = ConditionalAccessLagAttempts;
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
}
