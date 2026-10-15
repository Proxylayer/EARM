using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Engine.Edges;
using EArm.Engine.Providers;
using EArm.Expressions.Values;

namespace EArm.Providers;

/// <summary>
/// One declared app role assignment after shape validation, before any Graph lookup: each member is the plain
/// string the template gave, or <c>null</c> when absent; <see cref="UnknownSymbol"/> is set when a member is a
/// deploy-time placeholder (what-if).
/// </summary>
public sealed record DeclaredAppRoleAssignment(string? PrincipalId, string? ResourceId, string? ResourceAppId, string? AppRoleId, string? AppRole, string? UnknownSymbol)
{
    public bool HasUnknown => UnknownSymbol is not null;
}

/// <summary>
/// The pure rules of app role assignments, shared by the standalone
/// <c>Microsoft.Entra/appRoleAssignments</c> provider and the principal-side <c>appRoleAssignments</c> edge kind:
/// the declared shape (<c>principalId</c>, <c>resourceId | resourceAppId</c>, <c>appRoleId | appRole</c>), the
/// <c>allowedMemberTypes</c> / <c>isEnabled</c> checks against the resource's app role, and the composite identity
/// of an assignment. No Graph here.
/// </summary>
public static class AppRoleAssignmentsRules
{
    /// <summary>The app role id Graph uses for "default access" (an application without app roles).</summary>
    public const string DefaultAccessRoleId = "00000000-0000-0000-0000-000000000000";

    /// <summary>The property names an assignment accepts (canonical and sugar).</summary>
    public static readonly string[] AllowedProperties = ["principalId", "resourceId", "resourceAppId", "appRoleId", "appRole"];

    /// <summary>The three canonical key properties.</summary>
    public static readonly string[] KeyProperties = ["principalId", "resourceId", "appRoleId"];

    public static bool IsGuid(string? value)
    {
        return value is not null && Guid.TryParse(value, out _);
    }

    public static bool IsDefaultAccess(string appRoleId)
    {
        return JsonObjects.KeyEquals(appRoleId, DefaultAccessRoleId);
    }

    /// <summary>A GUID in canonical lower-case "D" form (other text trimmed).</summary>
    public static string NormalizeId(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return Guid.TryParse(id, out var guid) ? guid.ToString("D") : id.Trim();
    }

    /// <summary>
    /// Validates the declared shape of an assignment object. Returns <c>null</c> and an error message when the
    /// shape is wrong (unknown property, neither <c>resourceId</c> nor <c>resourceAppId</c>, neither <c>appRoleId</c>
    /// nor <c>appRole</c>, a non-string value). <paramref name="principalAllowed"/> is false for principal-side
    /// items, whose principal is the declaring resource.
    /// </summary>
    public static DeclaredAppRoleAssignment? Parse(JsonObject item, bool principalAllowed, out string? error)
    {
        ArgumentNullException.ThrowIfNull(item);
        error = null;
        string? unknownSymbol = null;
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in item)
        {
            string? canonical = AllowedProperties.FirstOrDefault(p => p.Equals(property.Key, StringComparison.OrdinalIgnoreCase));
            if (canonical is null)
            {
                error = $"'{property.Key}' is not a property of an app role assignment; expected principalId, resourceId or resourceAppId, and appRoleId or appRole.";
                return null;
            }

            if (canonical == "principalId" && !principalAllowed)
            {
                error = "principalId is implied by the declaring resource on a principal-side appRoleAssignments entry; remove it (or declare a Microsoft.Entra/appRoleAssignments resource).";
                return null;
            }

            if (JsonObjects.IsAbsent(property.Value))
            {
                continue;
            }

            if (UnknownJson.IsUnknown(property.Value, out var unknown))
            {
                unknownSymbol ??= unknown.Symbol;
                values[canonical] = null;
                continue;
            }

            if (!JsonValues.TryGetString(property.Value, out var text) || string.IsNullOrWhiteSpace(text))
            {
                error = $"{canonical} must be a non-empty string, got {JsonValues.Describe(property.Value)}.";
                return null;
            }

            values[canonical] = text.Trim();
        }

        bool hasResource = values.ContainsKey("resourceId") || values.ContainsKey("resourceAppId");
        bool hasRole = values.ContainsKey("appRoleId") || values.ContainsKey("appRole");
        if (!hasResource || !hasRole)
        {
            error = "an app role assignment needs the resource (resourceId: service principal object id, or resourceAppId: its application id) and the role (appRoleId, or appRole: the role's value).";
            return null;
        }

        foreach (var (name, value) in values)
        {
            if (value is not null && name is "principalId" or "resourceId" or "resourceAppId" or "appRoleId" && !IsGuid(value))
            {
                error = $"{name} '{value}' is not a GUID.";
                return null;
            }
        }

        return new DeclaredAppRoleAssignment(
            values.GetValueOrDefault("principalId"),
            values.GetValueOrDefault("resourceId"),
            values.GetValueOrDefault("resourceAppId"),
            values.GetValueOrDefault("appRoleId"),
            values.GetValueOrDefault("appRole"),
            unknownSymbol);
    }

    // ---- roles ----

    /// <summary>The app role object with the given id among a principal's <c>appRoles</c>, or <c>null</c>.</summary>
    public static JsonObject? FindRole(JsonArray appRoles, string appRoleId)
    {
        ArgumentNullException.ThrowIfNull(appRoles);
        ArgumentNullException.ThrowIfNull(appRoleId);
        foreach (var item in appRoles)
        {
            if (item is JsonObject role && JsonObjects.KeyEquals(JsonObjects.GetId(role), appRoleId))
            {
                return role;
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves the declared role against the resource's app roles: <c>appRole</c> (value) → id, or <c>appRoleId</c>
    /// checked to exist (the default-access id is always accepted). Returns an error message, or <c>null</c> with the
    /// id and the role object (<c>null</c> for default access).
    /// </summary>
    public static string? ResolveRole(ServicePrincipalSummary resource, string? appRoleId, string? appRole, out string resolvedId, out JsonObject? role)
    {
        ArgumentNullException.ThrowIfNull(resource);
        resolvedId = string.Empty;
        role = null;

        if (appRole is not null)
        {
            string? byValue = resource.FindAppRoleId(appRole);
            if (byValue is null)
            {
                return $"service principal '{resource.DisplayName ?? resource.AppId}' ({resource.Id}) defines no app role with value '{appRole}'; defined values: {RoleValues(resource.AppRoles)}.";
            }

            if (appRoleId is not null && !JsonObjects.KeyEquals(appRoleId, byValue))
            {
                return $"appRole '{appRole}' resolves to app role {byValue}, which differs from the declared appRoleId {appRoleId}; declare one of them.";
            }

            resolvedId = NormalizeId(byValue);
            role = FindRole(resource.AppRoles, byValue);
            return null;
        }

        if (appRoleId is null)
        {
            return "an app role assignment needs appRoleId or appRole.";
        }

        if (IsDefaultAccess(appRoleId))
        {
            resolvedId = DefaultAccessRoleId;
            return null;
        }

        role = FindRole(resource.AppRoles, appRoleId);
        if (role is null)
        {
            return $"service principal '{resource.DisplayName ?? resource.AppId}' ({resource.Id}) has no app role with id {appRoleId}; defined values: {RoleValues(resource.AppRoles)}.";
        }

        resolvedId = NormalizeId(appRoleId);
        return null;
    }

    /// <summary>The <c>value</c>s of a role array, sorted, for messages; <c>(none)</c> when empty.</summary>
    public static string RoleValues(JsonArray appRoles)
    {
        ArgumentNullException.ThrowIfNull(appRoles);
        var values = appRoles.OfType<JsonObject>().Select(r => JsonObjects.GetString(r, "value")).Where(v => !string.IsNullOrEmpty(v)).Select(v => v!).Order(StringComparer.Ordinal).ToList();
        return values.Count == 0 ? "(none)" : string.Join(", ", values);
    }

    /// <summary>The <c>allowedMemberTypes</c> entries of a role (case preserved).</summary>
    public static IReadOnlyList<string> AllowedMemberTypes(JsonObject role)
    {
        ArgumentNullException.ThrowIfNull(role);
        if (!JsonObjects.TryGetIgnoreCase(role, "allowedMemberTypes", out var node) || node is not JsonArray array)
        {
            return [];
        }

        return array.Select(item => JsonValues.TryGetString(item, out var text) ? text : null).Where(t => t is not null).Select(t => t!).ToList();
    }

    /// <summary>True when the role restricts the kind of principal (only User, or only Application): the principal's kind must then be known.</summary>
    public static bool RestrictsMemberType(JsonObject role)
    {
        var allowed = AllowedMemberTypes(role);
        return allowed.Count > 0 && !(allowed.Contains("User", StringComparer.OrdinalIgnoreCase) && allowed.Contains("Application", StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The <c>allowedMemberTypes</c> value a principal counts as: users and groups are <c>User</c>, service
    /// principals are <c>Application</c>. Accepts a Graph collection (<c>users</c>, <c>groups</c>,
    /// <c>servicePrincipals</c>) or an <c>@odata.type</c>; <c>null</c> for anything else.
    /// </summary>
    public static string? MemberTypeOf(string? collectionOrODataType)
    {
        if (string.IsNullOrEmpty(collectionOrODataType))
        {
            return null;
        }

        string collection = collectionOrODataType.StartsWith('#') ? EdgeGraphContext.CollectionOfODataType(collectionOrODataType) ?? string.Empty : collectionOrODataType;
        return collection.ToLowerInvariant() switch
        {
            "users" or "groups" => "User",
            "serviceprincipals" => "Application",
            _ => null,
        };
    }

    /// <summary>The member type of a template resource type (<c>Microsoft.Entra/users</c> → <c>User</c>).</summary>
    public static string? MemberTypeOfResourceType(string resourceType)
    {
        ArgumentNullException.ThrowIfNull(resourceType);
        return MemberTypeOf(EdgeGraphContext.CollectionOf(resourceType));
    }

    /// <summary>
    /// Whether the role may be assigned: <c>null</c> when it may, else the reason (disabled role, or the principal's
    /// kind is not in <c>allowedMemberTypes</c>). <paramref name="memberType"/> <c>null</c> skips the member check.
    /// </summary>
    public static string? CheckAssignable(JsonObject role, string? memberType)
    {
        ArgumentNullException.ThrowIfNull(role);
        string label = $"app role '{JsonObjects.GetString(role, "value") ?? "?"}' ({JsonObjects.GetId(role) ?? "?"})";
        if (JsonObjects.TryGetIgnoreCase(role, "isEnabled", out var enabledNode) && JsonValues.TryGetBool(enabledNode, out bool enabled) && !enabled)
        {
            return $"{label} is disabled; enable it on the application before assigning it.";
        }

        if (memberType is null)
        {
            return null;
        }

        var allowed = AllowedMemberTypes(role);
        if (allowed.Count > 0 && !allowed.Contains(memberType, StringComparer.OrdinalIgnoreCase))
        {
            string principal = memberType.Equals("Application", StringComparison.OrdinalIgnoreCase) ? "a service principal (Application)" : "a user or group (User)";
            return $"{label} allows {string.Join(", ", allowed)} members; it cannot be assigned to {principal}.";
        }

        return null;
    }

    // ---- identity ----

    /// <summary>The target key of an assignment edge: <c>{resourceId}/{appRoleId}</c>, GUIDs lower-case.</summary>
    public static string TargetKey(string resourceId, string appRoleId)
    {
        ArgumentNullException.ThrowIfNull(resourceId);
        ArgumentNullException.ThrowIfNull(appRoleId);
        return NormalizeId(resourceId) + "/" + NormalizeId(appRoleId);
    }

    /// <summary>Splits a target key back into (resourceId, appRoleId); false for a plain id or malformed key.</summary>
    public static bool TryParseTargetKey(string? targetKey, out string resourceId, out string appRoleId)
    {
        resourceId = string.Empty;
        appRoleId = string.Empty;
        if (string.IsNullOrEmpty(targetKey))
        {
            return false;
        }

        int slash = targetKey.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || slash == targetKey.Length - 1)
        {
            return false;
        }

        string resource = targetKey[..slash];
        string role = targetKey[(slash + 1)..];
        if (!IsGuid(resource) || !IsGuid(role))
        {
            return false;
        }

        resourceId = NormalizeId(resource);
        appRoleId = NormalizeId(role);
        return true;
    }

    /// <summary>The de-duplication identity shared by the edge kind and the standalone resource.</summary>
    public static string Identity(string principalId, string resourceId, string appRoleId)
    {
        return $"{EdgeKind.AppRoleAssignment}|{NormalizeId(resourceId)}|{NormalizeId(principalId)}|{NormalizeId(appRoleId)}";
    }

    /// <summary>True when the live assignment object matches the composite (GUID-aware).</summary>
    public static bool Matches(JsonObject assignment, string principalId, string appRoleId)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        return JsonObjects.KeyEquals(JsonObjects.GetString(assignment, "principalId"), principalId)
            && JsonObjects.KeyEquals(JsonObjects.GetString(assignment, "appRoleId"), appRoleId);
    }
}
