using System;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Engine.Providers;
using EArm.Expressions.Values;
using GraphApi = EArm.Graph.GraphApi;

namespace EArm.Providers;

/// <summary>
/// What a caller resolving ids against a service principal needs: its Graph id and the mirrored
/// <c>appRoles</c> / <c>oauth2PermissionScopes</c> (copies; members are objects with <c>id</c>, <c>value</c>, <c>isEnabled</c>...).
/// </summary>
public sealed record ServicePrincipalSummary(string Id, string AppId, string? DisplayName, JsonArray AppRoles, JsonArray Oauth2PermissionScopes)
{
    /// <summary>The id of the app role whose <c>value</c> equals <paramref name="value"/> (ordinal), or <c>null</c>.</summary>
    public string? FindAppRoleId(string value)
    {
        return FindMemberId(AppRoles, value);
    }

    /// <summary>The id of the permission scope whose <c>value</c> equals <paramref name="value"/> (ordinal), or <c>null</c>.</summary>
    public string? FindPermissionScopeId(string value)
    {
        return FindMemberId(Oauth2PermissionScopes, value);
    }

    private static string? FindMemberId(JsonArray members, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        foreach (var item in members)
        {
            if (item is JsonObject member && string.Equals(JsonObjects.GetString(member, "value"), value, StringComparison.Ordinal))
            {
                return JsonObjects.GetId(member);
            }
        }

        return null;
    }
}

/// <summary>
/// Reads a service principal by application id for callers outside the provider: <c>appRole()</c> /
/// <c>permissionScope()</c>, app role assignments and permission grants, and existing-resource
/// lookups. One request: <c>GET /servicePrincipals(appId='…')?$select=id,appId,displayName,appRoles,oauth2PermissionScopes</c>.
/// Never cached: a principal's roles and scopes mirror its application's and lag an application PATCH by seconds, so
/// resolve ids right before you need them, after the application's own apply completed. Pass
/// <see cref="RetryHint.ExpectReplicationLag"/> when the application (or principal) was created in this run; the 404
/// is then thrown for the retry policy instead of meaning "no such principal".
/// </summary>
public static class ServicePrincipalLookup
{
    /// <summary>The properties <see cref="FindByAppIdAsync"/> selects.</summary>
    public static readonly string[] SelectedProperties = ["id", "appId", "displayName", "appRoles", "oauth2PermissionScopes"];

    /// <summary>The principal for <paramref name="appId"/>, or <c>null</c> when none exists (404 under the default hint).</summary>
    public static async Task<ServicePrincipalSummary?> FindByAppIdAsync(IGraphClient graph, string appId, GraphApi api = GraphApi.V1, RetryHint hint = RetryHint.Default, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        if (!Guid.TryParse(appId, out _))
        {
            throw new ArgumentException($"'{appId}' is not an application id (GUID).", nameof(appId));
        }

        string path = GraphPath.AlternateKey("/servicePrincipals", "appId", appId);
        var request = new GraphRequest(path, api, hint: hint).WithSelect(SelectedProperties);
        return await graph.GetAsync(request, cancellationToken).ConfigureAwait(false) is JsonObject live ? Summarize(live) : null;
    }

    /// <summary>The summary of a live service principal object (missing arrays read as empty).</summary>
    public static ServicePrincipalSummary Summarize(JsonObject live)
    {
        ArgumentNullException.ThrowIfNull(live);
        string id = JsonObjects.GetId(live) ?? throw new ArgumentException("The service principal object has no 'id'.", nameof(live));
        string appId = JsonObjects.GetString(live, "appId") ?? throw new ArgumentException("The service principal object has no 'appId'.", nameof(live));
        return new ServicePrincipalSummary(id, appId, JsonObjects.GetString(live, "displayName"), ArrayOf(live, "appRoles"), ArrayOf(live, "oauth2PermissionScopes"));
    }

    private static JsonArray ArrayOf(JsonObject live, string property)
    {
        return JsonObjects.TryGetIgnoreCase(live, property, out var node) && node is JsonArray array
            ? (JsonArray)JsonValues.Clone(array)!
            : [];
    }
}
