using System;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Engine.Providers;
using EArm.Graph;

namespace EArm.Engine.Planning;

/// <summary>
/// Builds what <c>tenant()</c> and <c>environment()</c> return: the tenant object from the organization that the preflight
/// read (<c>GET /organization?$select=id,displayName,verifiedDomains</c>, one call shared with the sign-in check) and the environment object from the
/// cloud's entry in the fixed allow-list (<see cref="GraphCloud"/>).
/// </summary>
public static class DeploymentEnvironment
{
    public const string DefaultCloudName = "AzureCloud";
    public const string PublicLoginEndpoint = "https://login.microsoftonline.com/";

    /// <summary><c>{ name, graph, authentication: { loginEndpoint } }</c> for the selected cloud.</summary>
    public static JsonObject Environment(GraphCloud cloud)
    {
        ArgumentNullException.ThrowIfNull(cloud);
        return new JsonObject
        {
            ["name"] = cloud.EnvironmentName,
            ["graph"] = cloud.GraphOrigin,
            ["authentication"] = new JsonObject { ["loginEndpoint"] = cloud.AuthorityHost.AbsoluteUri },
        };
    }

    /// <summary>
    /// <c>{ tenantId, defaultDomain, displayName }</c> of the signed-in tenant from the organization object the preflight read, or <c>null</c> when
    /// the identity may not read the organization (<c>tenant()</c> then stays a deploy-time unknown rather than failing the deployment).
    /// </summary>
    public static JsonObject? TenantFromOrganization(JsonObject? organization, string tenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        if (organization is null)
        {
            return null;
        }

        string? defaultDomain = null;
        if (JsonObjects.TryGetIgnoreCase(organization, "verifiedDomains", out var domainsNode) && domainsNode is JsonArray domains)
        {
            var domainObjects = domains.OfType<JsonObject>().ToList();
            var preferred = domainObjects.FirstOrDefault(d => JsonObjects.TryGetIgnoreCase(d, "isDefault", out var isDefault) && isDefault is JsonValue flag && flag.TryGetValue<bool>(out bool b) && b) ?? domainObjects.FirstOrDefault();
            defaultDomain = preferred is null ? null : JsonObjects.GetString(preferred, "name");
        }

        return new JsonObject
        {
            ["tenantId"] = JsonObjects.GetId(organization) ?? tenantId,
            ["defaultDomain"] = defaultDomain is null ? null : JsonValue.Create(defaultDomain),
            ["displayName"] = JsonObjects.GetString(organization, "displayName") is { } displayName ? JsonValue.Create(displayName) : null,
        };
    }
}