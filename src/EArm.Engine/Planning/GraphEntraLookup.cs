using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Az;
using EArm.Engine.Providers;
using EArm.Expressions.Evaluation;

namespace EArm.Engine.Planning;

/// <summary>
/// The engine's <see cref="IEntraLookup"/> over <see cref="IGraphClient"/> (WI-16): app roles and permission scopes
/// are read from the resource application's service principal
/// (<c>GET /servicePrincipals(appId='…')?$select=id,appId,appRoles,oauth2PermissionScopes</c>) or, when no principal
/// exists yet (an application created in this run), from the application object itself
/// (<c>GET /applications(appId='…')?$select=id,appId,appRoles,api</c>: the ids are the same); role definitions by
/// display name from <c>GET /roleManagement/directory/roleDefinitions?$filter=displayName eq '…'</c>. Every answer
/// is cached for the run; a failed read is not, so the next evaluation retries.
/// </summary>
public sealed class GraphEntraLookup : IEntraLookup
{
    private static readonly string[] PrincipalProperties = ["id", "appId", "appRoles", "oauth2PermissionScopes"];
    private static readonly string[] ApplicationProperties = ["id", "appId", "appRoles", "api"];
    private static readonly string[] RoleProperties = ["id", "displayName", "isBuiltIn"];

    private readonly IGraphClient _graph;
    private readonly RetryPolicy? _retryPolicy;
    private readonly ConcurrentDictionary<string, Lazy<Task<Permissions?>>> _permissions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Task<EntraLookupResult>>> _roles = new(StringComparer.OrdinalIgnoreCase);

    public GraphEntraLookup(IGraphClient graph, RetryPolicy? retryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        _graph = graph;
        _retryPolicy = retryPolicy;
    }

    public CancellationToken CancellationToken { get; set; }

    /// <summary>The roles and scopes of one resource application, and where they were read from.</summary>
    private sealed record Permissions(string Source, JsonArray AppRoles, JsonArray Scopes);

    public Task<EntraLookupResult> FindAppRoleIdAsync(string appId, string value)
    {
        return FindPermissionAsync(appId, value, appRoles: true);
    }

    public Task<EntraLookupResult> FindPermissionScopeIdAsync(string appId, string value)
    {
        return FindPermissionAsync(appId, value, appRoles: false);
    }

    public async Task<EntraLookupResult> FindRoleDefinitionIdAsync(string displayName)
    {
        ArgumentNullException.ThrowIfNull(displayName);
        var lazy = _roles.GetOrAdd(displayName, key => new Lazy<Task<EntraLookupResult>>(() => ReadRoleAsync(key)));
        try
        {
            return await lazy.Value.ConfigureAwait(false);
        }
        catch
        {
            _roles.TryRemove(displayName, out _);
            throw;
        }
    }

    private async Task<EntraLookupResult> FindPermissionAsync(string appId, string value, bool appRoles)
    {
        ArgumentNullException.ThrowIfNull(appId);
        ArgumentNullException.ThrowIfNull(value);
        if (!Guid.TryParse(appId, out _))
        {
            return new EntraLookupResult.NotFound($"'{appId}' is not an application id (GUID).");
        }

        var lazy = _permissions.GetOrAdd(appId, key => new Lazy<Task<Permissions?>>(() => ReadPermissionsAsync(key)));
        Permissions? permissions;
        try
        {
            permissions = await lazy.Value.ConfigureAwait(false);
        }
        catch
        {
            _permissions.TryRemove(appId, out _);
            throw;
        }

        if (permissions is null)
        {
            return new EntraLookupResult.NotFound($"no service principal or application with appId '{appId}' exists in the tenant.");
        }

        var members = appRoles ? permissions.AppRoles : permissions.Scopes;
        string kind = appRoles ? "app role" : "permission scope";
        if (FindMemberId(members, value) is { } id)
        {
            return new EntraLookupResult.Found(id);
        }

        var available = members.OfType<JsonObject>().Select(m => JsonObjects.GetString(m, "value")).Where(v => !string.IsNullOrEmpty(v)).ToList();
        string list = available.Count == 0 ? "it declares none" : $"available: {string.Join(", ", available)}";
        return new EntraLookupResult.NotFound($"the {permissions.Source} for appId '{appId}' declares no {kind} '{value}' ({list}).");
    }

    private async Task<Permissions?> ReadPermissionsAsync(string appId)
    {
        var principalRequest = new GraphRequest(GraphPath.AlternateKey("/servicePrincipals", "appId", appId)).WithSelect(PrincipalProperties);
        if (await GetAsync(principalRequest).ConfigureAwait(false) is JsonObject principal)
        {
            return new Permissions("service principal", ArrayOf(principal, "appRoles"), ArrayOf(principal, "oauth2PermissionScopes"));
        }

        var applicationRequest = new GraphRequest(GraphPath.AlternateKey("/applications", "appId", appId)).WithSelect(ApplicationProperties);
        if (await GetAsync(applicationRequest).ConfigureAwait(false) is JsonObject application)
        {
            var api = JsonObjects.TryGetIgnoreCase(application, "api", out var apiNode) ? apiNode as JsonObject : null;
            return new Permissions("application", ArrayOf(application, "appRoles"), ArrayOf(api, "oauth2PermissionScopes"));
        }

        return null;
    }

    private async Task<EntraLookupResult> ReadRoleAsync(string displayName)
    {
        string filter = "displayName eq '" + displayName.Replace("'", "''", StringComparison.Ordinal) + "'";
        var request = new GraphRequest("/roleManagement/directory/roleDefinitions").WithFilter(filter).WithSelect(RoleProperties);
        var ids = new List<string>();
        if (await GetAsync(request).ConfigureAwait(false) is JsonObject envelope && JsonObjects.TryGetIgnoreCase(envelope, "value", out var valueNode) && valueNode is JsonArray values)
        {
            foreach (var item in values.OfType<JsonObject>())
            {
                if (JsonObjects.GetId(item) is { } id)
                {
                    ids.Add(id);
                }
            }
        }

        return ids.Count switch
        {
            0 => new EntraLookupResult.NotFound($"no role definition named '{displayName}' exists in the tenant."),
            1 => new EntraLookupResult.Found(ids[0]),
            _ => new EntraLookupResult.NotFound($"{ids.Count} role definitions are named '{displayName}' ({string.Join(", ", ids)}); pass the id instead."),
        };
    }

    private Task<JsonNode?> GetAsync(GraphRequest request)
    {
        return _retryPolicy is null
            ? _graph.GetAsync(request, CancellationToken)
            : _retryPolicy.ExecuteAsync(ct => _graph.GetAsync(request, ct), RetryHint.Default, null, CancellationToken);
    }

    private static string? FindMemberId(JsonArray members, string value)
    {
        string? caseInsensitive = null;
        foreach (var member in members.OfType<JsonObject>())
        {
            string? memberValue = JsonObjects.GetString(member, "value");
            if (memberValue is null)
            {
                continue;
            }

            if (string.Equals(memberValue, value, StringComparison.Ordinal))
            {
                return JsonObjects.GetId(member);
            }

            if (caseInsensitive is null && string.Equals(memberValue, value, StringComparison.OrdinalIgnoreCase))
            {
                caseInsensitive = JsonObjects.GetId(member);
            }
        }

        return caseInsensitive;
    }

    private static JsonArray ArrayOf(JsonObject? obj, string property)
    {
        return JsonObjects.TryGetIgnoreCase(obj, property, out var node) && node is JsonArray array ? array : [];
    }
}
