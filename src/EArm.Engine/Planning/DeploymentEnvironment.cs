using System;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Engine.Providers;
using EArm.Graph;

namespace EArm.Engine.Planning;

/// <summary>
/// Builds what <c>tenant()</c>, <c>deployer()</c> and <c>environment()</c> return: the tenant object from the organization that the preflight
/// read (<c>GET /organization?$select=id,displayName,verifiedDomains,countryLetterCode</c>, one call shared with the sign-in check), the deployer
/// from the signed-in principal of the preflight, and the environment object from the cloud's entry in the fixed allow-list (<see cref="GraphCloud"/>).
/// </summary>
public static class DeploymentEnvironment
{
    public const string DefaultCloudName = "AzureCloud";
    public const string PublicLoginEndpoint = "https://login.microsoftonline.com/";

    /// <summary>
    /// ARM's <c>environment()</c> fields that mean something for Microsoft Entra, for the selected cloud: <c>name</c>, <c>portal</c>,
    /// <c>resourceManager</c>, <c>authentication</c> (<c>loginEndpoint</c>, <c>audiences</c>, <c>tenant</c>, <c>identityProvider</c>) and
    /// <c>suffixes.keyvaultDns</c>, plus <c>graph</c>, which is the <b>Microsoft Graph</b> endpoint EARM talks to (in ARM it is the retired
    /// Azure AD Graph, <c>https://graph.windows.net/</c>). The US Government DoD cloud shares the US Government Azure Resource Manager endpoints.
    /// </summary>
    public static JsonObject Environment(GraphCloud cloud)
    {
        ArgumentNullException.ThrowIfNull(cloud);
        var arm = ArmEndpoints(cloud);
        return new JsonObject
        {
            ["name"] = cloud.EnvironmentName,
            ["graph"] = cloud.GraphOrigin,
            ["portal"] = arm.Portal,
            ["resourceManager"] = arm.ResourceManager,
            ["authentication"] = new JsonObject
            {
                ["loginEndpoint"] = cloud.AuthorityHost.AbsoluteUri,
                ["audiences"] = new JsonArray(arm.Audiences.Select(audience => (JsonNode?)JsonValue.Create(audience)).ToArray()),
                ["tenant"] = "common",
                ["identityProvider"] = "AAD",
            },
            ["suffixes"] = new JsonObject { ["keyvaultDns"] = "." + cloud.KeyVaultSuffix },
        };
    }

    /// <summary>
    /// <c>{ countryCode, displayName, id, tenantId, defaultDomain }</c> of the signed-in tenant from the organization object the preflight read:
    /// ARM's shape (<c>id</c> is <c>/tenants/&lt;tenantId&gt;</c>, <c>countryCode</c> the organization's <c>countryLetterCode</c>) plus EARM's
    /// <c>defaultDomain</c>. <c>null</c> when the identity may not read the organization (<c>tenant()</c> then stays a deploy-time unknown rather
    /// than failing the deployment).
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

        string id = JsonObjects.GetId(organization) ?? tenantId;
        return new JsonObject
        {
            ["countryCode"] = Text(JsonObjects.GetString(organization, "countryLetterCode")),
            ["displayName"] = Text(JsonObjects.GetString(organization, "displayName")),
            ["id"] = "/tenants/" + id,
            ["tenantId"] = id,
            ["defaultDomain"] = Text(defaultDomain),
        };
    }

    /// <summary>
    /// <c>{ objectId, tenantId, userPrincipalName }</c> of the principal that signed in (ARM's <c>deployer()</c>). For an app-only token (a service
    /// principal or managed identity) <c>userPrincipalName</c> is empty, as in ARM. <c>null</c> when the token carried no object id, which keeps
    /// <c>deployer()</c> a deploy-time unknown.
    /// </summary>
    public static JsonObject? Deployer(GraphPreflightResult preflight)
    {
        ArgumentNullException.ThrowIfNull(preflight);
        if (string.IsNullOrEmpty(preflight.PrincipalObjectId))
        {
            return null;
        }

        bool application = string.Equals(preflight.PrincipalType, "application", StringComparison.OrdinalIgnoreCase);
        return new JsonObject
        {
            ["objectId"] = preflight.PrincipalObjectId,
            ["tenantId"] = preflight.TenantId,
            ["userPrincipalName"] = application ? string.Empty : preflight.PrincipalName ?? string.Empty,
        };
    }

    private static JsonValue? Text(string? value)
    {
        return value is null ? null : JsonValue.Create(value);
    }

    private static (string Portal, string ResourceManager, string[] Audiences) ArmEndpoints(GraphCloud cloud)
    {
        return cloud.EnvironmentName switch
        {
            "AzureUSGovernment" or "AzureUSGovernmentDoD" => ("https://portal.azure.us", "https://management.usgovcloudapi.net/", ["https://management.core.usgovcloudapi.net/", "https://management.usgovcloudapi.net/"]),
            "AzureChinaCloud" => ("https://portal.azure.cn", "https://management.chinacloudapi.cn/", ["https://management.core.chinacloudapi.cn/", "https://management.chinacloudapi.cn/"]),
            _ => ("https://portal.azure.com", "https://management.azure.com/", ["https://management.core.windows.net/", "https://management.azure.com/"]),
        };
    }
}
