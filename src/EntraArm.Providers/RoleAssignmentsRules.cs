using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using EntraArm.Core;
using EntraArm.Engine.Edges;
using EntraArm.Engine.Providers;
using EntraArm.Expressions.Values;

namespace EntraArm.Providers;

/// <summary>
/// One declared directory role assignment after shape validation, before any lookup: each member is the plain
/// string the template gave, or <c>null</c> when absent; <see cref="UnknownSymbol"/> is set when a member is a
/// deploy-time placeholder (what-if).
/// </summary>
public sealed record DeclaredRoleAssignment(string? RoleDefinitionId, string? Role, string? PrincipalId, string? DirectoryScopeId, string? AdministrativeUnit, string? UnknownSymbol)
{
    public bool HasUnknown => UnknownSymbol is not null;
}

/// <summary>
/// The pure rules of directory role assignments (plan sections 1.4 and 3.2), shared by the standalone
/// <c>Microsoft.Entra/roleAssignments</c> provider and the principal-side <c>directoryRoles</c> edge kind: the declared
/// shape (<c>roleDefinitionId | role</c>, <c>principalId</c>, <c>directoryScopeId | administrativeUnit</c>), the
/// canonical scope (<c>/</c>, <c>/administrativeUnits/{id}</c>, <c>/{appObjectId}</c>), the built-in role table, the
/// <c>{roleDefinitionId}@{directoryScopeId}</c> target key and the composite identity. No Graph here.
/// </summary>
public static class RoleAssignmentsRules
{
    /// <summary>The tenant-wide scope and the default.</summary>
    public const string RootScope = "/";

    /// <summary>The scope prefix of an administrative-unit-scoped assignment.</summary>
    public const string AdministrativeUnitScopePrefix = "/administrativeUnits/";

    /// <summary>The Global Administrator role template: assigning it needs Privileged Role Administrator (or Global Administrator) rights.</summary>
    public const string GlobalAdministratorTemplateId = "62e90394-69f5-4237-9190-012177145e10";

    /// <summary>The property names an assignment accepts (canonical and sugar).</summary>
    public static readonly string[] AllowedProperties = ["roleDefinitionId", "role", "principalId", "directoryScopeId", "administrativeUnit"];

    /// <summary>The three canonical key properties.</summary>
    public static readonly string[] KeyProperties = ["roleDefinitionId", "principalId", "directoryScopeId"];

    /// <summary>The Graph collection of role assignments.</summary>
    public const string AssignmentsPath = "/roleManagement/directory/roleAssignments";

    /// <summary>The Graph collection of role definitions.</summary>
    public const string DefinitionsPath = "/roleManagement/directory/roleDefinitions";

    /// <summary>The <c>@odata.type</c> Graph requires in a role assignment body.</summary>
    public const string AssignmentODataType = "#microsoft.graph.unifiedRoleAssignment";

    public static bool IsGuid(string? value)
    {
        return value is not null && Guid.TryParse(value, out _);
    }

    /// <summary>A GUID in canonical lower-case "D" form (other text trimmed).</summary>
    public static string NormalizeId(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return Guid.TryParse(id, out var guid) ? guid.ToString("D") : id.Trim();
    }

    /// <summary>
    /// Validates the declared shape of an assignment object. Returns <c>null</c> and an error message when the
    /// shape is wrong (unknown property, neither <c>roleDefinitionId</c> nor <c>role</c>, a non-string value, a
    /// scope that does not start with <c>/</c>). <paramref name="principalAllowed"/> is false for principal-side
    /// items, whose principal is the declaring resource.
    /// </summary>
    public static DeclaredRoleAssignment? Parse(JsonObject item, bool principalAllowed, out string? error)
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
                error = $"'{property.Key}' is not a property of a directory role assignment; expected roleDefinitionId or role, {(principalAllowed ? "principalId, " : string.Empty)}and directoryScopeId or administrativeUnit.";
                return null;
            }

            if (canonical == "principalId" && !principalAllowed)
            {
                error = "principalId is implied by the declaring resource on a principal-side directoryRoles entry; remove it (or declare a Microsoft.Entra/roleAssignments resource).";
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

        if (!values.ContainsKey("roleDefinitionId") && !values.ContainsKey("role"))
        {
            error = "a directory role assignment needs the role: roleDefinitionId (a role template id, for example roleTemplate('Global Reader')) or role (a template id, a built-in display name, a custom role's display name or id).";
            return null;
        }

        if (values.TryGetValue("roleDefinitionId", out var roleDefinitionId) && roleDefinitionId is not null && !IsGuid(roleDefinitionId))
        {
            error = $"roleDefinitionId '{roleDefinitionId}' is not a GUID; use roleTemplate('<display name>') or declare role instead.";
            return null;
        }

        if (values.TryGetValue("principalId", out var principalId) && principalId is not null && !IsGuid(principalId))
        {
            error = $"principalId '{principalId}' is not a GUID.";
            return null;
        }

        if (values.TryGetValue("directoryScopeId", out var scope) && scope is not null && !scope.StartsWith('/'))
        {
            error = $"directoryScopeId '{scope}' must start with '/': '/' (tenant), '/administrativeUnits/{{id}}' or '/{{applicationObjectId}}'.";
            return null;
        }

        return new DeclaredRoleAssignment(
            values.GetValueOrDefault("roleDefinitionId"),
            values.GetValueOrDefault("role"),
            values.GetValueOrDefault("principalId"),
            values.GetValueOrDefault("directoryScopeId"),
            values.GetValueOrDefault("administrativeUnit"),
            unknownSymbol);
    }

    // ---- roles ----

    /// <summary>
    /// Resolves a <c>role</c> value without Graph: a GUID (template id, custom role id) or a built-in display name
    /// from <see cref="WellKnownIds.RoleTemplates"/>. <c>null</c> when the value needs a tenant lookup (a custom role
    /// name).
    /// </summary>
    public static string? ResolveRoleOffline(string role)
    {
        ArgumentNullException.ThrowIfNull(role);
        if (IsGuid(role))
        {
            return NormalizeId(role);
        }

        return WellKnownIds.TryGetRoleTemplateId(role, out var templateId) ? NormalizeId(templateId) : null;
    }

    /// <summary>True when the role definition id is the Global Administrator template.</summary>
    public static bool IsGlobalAdministrator(string? roleDefinitionId)
    {
        return JsonObjects.KeyEquals(roleDefinitionId, GlobalAdministratorTemplateId);
    }

    /// <summary>The what-if reason attached to a Global Administrator assignment.</summary>
    public const string GlobalAdministratorReason = "assigning Global Administrator requires the caller to hold Privileged Role Administrator (or Global Administrator); RoleManagement.ReadWrite.Directory alone is refused for this role";

    // ---- scopes ----

    /// <summary>The scope of an administrative unit: <c>/administrativeUnits/{id}</c>.</summary>
    public static string AdministrativeUnitScope(string administrativeUnitId)
    {
        ArgumentNullException.ThrowIfNull(administrativeUnitId);
        return AdministrativeUnitScopePrefix + NormalizeId(administrativeUnitId);
    }

    /// <summary>
    /// The canonical form of a directory scope: <c>/</c>, <c>/administrativeUnits/{lower-case id}</c> or
    /// <c>/{lower-case object id}</c>; anything else is trimmed and kept (Graph decides).
    /// </summary>
    public static string NormalizeScope(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return RootScope;
        }

        string trimmed = scope.Trim();
        if (trimmed == RootScope)
        {
            return RootScope;
        }

        if (TryGetAdministrativeUnitId(trimmed, out var unitId))
        {
            return AdministrativeUnitScope(unitId);
        }

        if (trimmed.Length > 1 && trimmed[0] == '/' && IsGuid(trimmed[1..]))
        {
            return "/" + NormalizeId(trimmed[1..]);
        }

        return trimmed;
    }

    /// <summary>True when the scope is an administrative unit scope; <paramref name="administrativeUnitId"/> is the unit's id.</summary>
    public static bool TryGetAdministrativeUnitId(string? scope, out string administrativeUnitId)
    {
        administrativeUnitId = string.Empty;
        if (scope is null || !scope.StartsWith(AdministrativeUnitScopePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string id = scope[AdministrativeUnitScopePrefix.Length..].Trim('/');
        if (!IsGuid(id))
        {
            return false;
        }

        administrativeUnitId = NormalizeId(id);
        return true;
    }

    public static bool ScopeEquals(string? left, string? right)
    {
        return string.Equals(NormalizeScope(left), NormalizeScope(right), StringComparison.OrdinalIgnoreCase);
    }

    // ---- identity ----

    /// <summary>The target key of an assignment edge: <c>{roleDefinitionId}@{directoryScopeId}</c>, canonical form.</summary>
    public static string TargetKey(string roleDefinitionId, string? directoryScopeId)
    {
        ArgumentNullException.ThrowIfNull(roleDefinitionId);
        return NormalizeId(roleDefinitionId) + "@" + NormalizeScope(directoryScopeId);
    }

    /// <summary>Splits a target key back into (roleDefinitionId, directoryScopeId); false for a plain id or malformed key.</summary>
    public static bool TryParseTargetKey(string? targetKey, out string roleDefinitionId, out string directoryScopeId)
    {
        roleDefinitionId = string.Empty;
        directoryScopeId = string.Empty;
        if (string.IsNullOrEmpty(targetKey))
        {
            return false;
        }

        int at = targetKey.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0 || at == targetKey.Length - 1)
        {
            return false;
        }

        string role = targetKey[..at];
        string scope = targetKey[(at + 1)..];
        if (!IsGuid(role) || !scope.StartsWith('/'))
        {
            return false;
        }

        roleDefinitionId = NormalizeId(role);
        directoryScopeId = NormalizeScope(scope);
        return true;
    }

    /// <summary>The de-duplication identity shared by the edge kind and the standalone resource.</summary>
    public static string Identity(string principalId, string roleDefinitionId, string? directoryScopeId)
    {
        return $"{EdgeKind.DirectoryRole}|{NormalizeId(roleDefinitionId)}|{NormalizeId(principalId)}|{NormalizeScope(directoryScopeId).ToLowerInvariant()}";
    }

    /// <summary>True when the live assignment object matches the composite (GUID-aware, scope canonicalised).</summary>
    public static bool Matches(JsonObject assignment, string roleDefinitionId, string? directoryScopeId)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        return JsonObjects.KeyEquals(JsonObjects.GetString(assignment, "roleDefinitionId"), roleDefinitionId)
            && ScopeEquals(JsonObjects.GetString(assignment, "directoryScopeId"), directoryScopeId);
    }

    /// <summary>The request body of <c>POST /roleManagement/directory/roleAssignments</c>.</summary>
    public static JsonObject AssignmentBody(string roleDefinitionId, string principalId, string? directoryScopeId)
    {
        ArgumentNullException.ThrowIfNull(roleDefinitionId);
        ArgumentNullException.ThrowIfNull(principalId);
        return new JsonObject
        {
            ["@odata.type"] = AssignmentODataType,
            ["roleDefinitionId"] = NormalizeId(roleDefinitionId),
            ["principalId"] = NormalizeId(principalId),
            ["directoryScopeId"] = NormalizeScope(directoryScopeId),
        };
    }

    /// <summary>The extras recorded for a live assignment (<c>id</c>, <c>roleDefinitionId</c>, <c>directoryScopeId</c>).</summary>
    public static JsonObject LiveExtra(JsonObject assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        var extra = new JsonObject();
        if (JsonObjects.GetId(assignment) is { } id)
        {
            extra["id"] = id;
        }

        if (JsonObjects.GetString(assignment, "roleDefinitionId") is { } roleDefinitionId)
        {
            extra["roleDefinitionId"] = NormalizeId(roleDefinitionId);
        }

        extra["directoryScopeId"] = NormalizeScope(JsonObjects.GetString(assignment, "directoryScopeId"));
        return extra;
    }

    /// <summary>The kind of principal a Graph collection holds; <c>null</c> for anything but users, groups and service principals.</summary>
    public static string? PrincipalNoun(string? collection)
    {
        return collection?.ToLowerInvariant() switch
        {
            "users" => "user",
            "groups" => "group",
            "serviceprincipals" => "service principal",
            _ => null,
        };
    }
}
