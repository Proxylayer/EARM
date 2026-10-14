using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EntraArm.Az;
using EntraArm.Engine.Providers;
using Microsoft.Extensions.Logging;

namespace EntraArm.Engine.Planning;

/// <summary>
/// Builds what <c>tenant()</c> and <c>environment()</c> return (plan section 1.5) from the preflight result:
/// the tenant object from <c>GET /organization?$select=id,displayName,verifiedDomains</c> and the environment
/// object from constants of the public cloud (the Graph client only targets <c>graph.microsoft.com</c>, so a
/// sovereign-cloud <c>az cloud show</c> would describe endpoints EntraARM cannot use), named after the Azure CLI
/// cloud of the session.
/// </summary>
public static class DeploymentEnvironment
{
    public const string DefaultCloudName = "AzureCloud";
    public const string PublicLoginEndpoint = "https://login.microsoftonline.com/";

    /// <summary><c>{ name, graph, authentication: { loginEndpoint } }</c>.</summary>
    public static JsonObject Environment(string? cloudName)
    {
        return new JsonObject
        {
            ["name"] = string.IsNullOrWhiteSpace(cloudName) ? DefaultCloudName : cloudName,
            ["graph"] = GraphApiExtensions.Host,
            ["authentication"] = new JsonObject { ["loginEndpoint"] = PublicLoginEndpoint },
        };
    }

    /// <summary>
    /// <c>{ tenantId, defaultDomain, displayName }</c> of the signed-in tenant, or <c>null</c> when the organization
    /// cannot be read (logged; <c>tenant()</c> then stays a deploy-time unknown rather than failing the deployment).
    /// </summary>
    public static async Task<JsonObject?> ReadTenantAsync(IGraphClient graph, string tenantId, ILogger logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(logger);

        JsonNode? response;
        try
        {
            response = await graph.GetAsync(new GraphRequest("/organization").WithSelect("id", "displayName", "verifiedDomains"), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is GraphException or AzCliException or ToolchainException)
        {
            EngineLog.TenantInfoUnavailable(logger, ex.Message);
            return null;
        }

        if (response is not JsonObject envelope || !JsonObjects.TryGetIgnoreCase(envelope, "value", out var valueNode) || valueNode is not JsonArray values || values.FirstOrDefault() is not JsonObject organization)
        {
            EngineLog.TenantInfoUnavailable(logger, "GET /organization returned no organization");
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
