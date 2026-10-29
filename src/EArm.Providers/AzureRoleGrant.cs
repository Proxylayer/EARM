using System;
using System.Text.Json.Nodes;
using EArm.Core.Validation;
using EArm.Engine.Providers;

namespace EArm.Providers;

/// <summary>
/// What names an Azure role eligibility or role assignment: the Azure scope (<see cref="AzureScopeRules.NormalizeScope"/>), the role definition id as ARM
/// names it at that scope (<see cref="AzureScopeRules.QualifiedRoleDefinitionId"/>) and the principal's object id (a lower-case GUID).
/// </summary>
public sealed record AzureRoleGrant(string Scope, string RoleDefinitionId, string PrincipalId)
{
    /// <summary>The grant as messages name it.</summary>
    public string Describe()
    {
        return $"Azure role {RoleDefinitionId} of principal {PrincipalId} at {Scope}";
    }

    /// <summary>
    /// True when a listed ARM resource (an eligibility schedule or a role assignment) is this grant: its <c>properties.scope</c> IS the scope (not a parent
    /// or a child), and its role (compared by GUID, whatever the qualification) and principal are the grant's.
    /// </summary>
    public bool Matches(JsonObject resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        var properties = PropertiesOf(resource);
        return AzureScopeRules.ScopeEquals(JsonObjects.GetString(properties, "scope"), Scope)
            && AzureScopeRules.RoleDefinitionGuid(JsonObjects.GetString(properties, "roleDefinitionId")) is { } role
            && role == AzureScopeRules.RoleDefinitionGuid(RoleDefinitionId)
            && AzureScopeRules.NormalizePrincipalId(JsonObjects.GetString(properties, "principalId")) == PrincipalId;
    }

    /// <summary>The <c>properties</c> object of an ARM resource, or an empty object.</summary>
    public static JsonObject PropertiesOf(JsonObject resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return JsonObjects.TryGetIgnoreCase(resource, "properties", out var node) && node is JsonObject properties ? properties : new JsonObject();
    }

    /// <summary>
    /// The resource path of a listed resource named <paramref name="resourceId"/>, rebuilt as <c>{collection}/{name}</c> when the id is in
    /// <paramref name="collection"/> (case-insensitive, one segment, a path ARM can be sent); <c>null</c> otherwise, so a response never steers a write elsewhere.
    /// </summary>
    public static string? PathIn(string collection, string? resourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        if (resourceId is null)
        {
            return null;
        }

        string prefix = collection + "/";
        string id = resourceId.Trim();
        if (!id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string name = id[prefix.Length..];
        string path = prefix + name;
        return name.Length > 0 && !name.Contains('/', StringComparison.Ordinal) && EArm.Graph.ArmClient.IsValidResourcePath(path) ? path : null;
    }
}
