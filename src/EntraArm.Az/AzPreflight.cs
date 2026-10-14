using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace EntraArm.Az;

/// <summary>What the preflight learned about the Azure CLI session.</summary>
/// <param name="TenantId"><c>tenantId</c> from <c>az account show</c>.</param>
/// <param name="OrganizationId">The id returned by <c>GET /v1.0/organization</c> (confirms the Graph token works).</param>
public sealed record AzPreflightResult(
    string TenantId,
    string OrganizationId,
    string? UserName,
    string? UserType,
    string? AzVersion,
    string? CloudName,
    string? SubscriptionId,
    string? SubscriptionName);

/// <summary>
/// Verifies the toolchain before a deployment: <c>az version</c> (logged), <c>az account show</c> (tenant and
/// user), and <c>GET /v1.0/organization?$select=id</c> (Graph token). A missing session surfaces as
/// <see cref="AzCliException"/> with <see cref="AzCliFailure.NotLoggedIn"/> and the <c>az login --allow-no-subscriptions</c> hint.
/// </summary>
public sealed class AzPreflight
{
    private readonly IAzRunner _runner;
    private readonly IGraphClient _graph;
    private readonly ILogger<AzPreflight> _logger;

    public AzPreflight(IAzRunner runner, IGraphClient graph, ILogger<AzPreflight> logger)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<AzPreflightResult> RunAsync(CancellationToken cancellationToken)
    {
        var azVersion = await ReadAzVersionAsync(cancellationToken).ConfigureAwait(false);
        var account = await ReadAccountAsync(cancellationToken).ConfigureAwait(false);

        var tenantId = ReadString(account, "tenantId") ?? ReadString(account, "homeTenantId");
        if (string.IsNullOrEmpty(tenantId))
        {
            throw new AzCliException(AzCliFailure.NotLoggedIn, $"az account show returned no tenantId. {AzCliException.LoginHint}");
        }

        var user = account["user"] as JsonObject;
        var userName = user is null ? null : ReadString(user, "name");
        var userType = user is null ? null : ReadString(user, "type");
        var cloud = ReadString(account, "environmentName");
        var subscriptionId = ReadString(account, "id");
        var subscriptionName = ReadString(account, "name");

        AzLog.PreflightAccount(_logger, azVersion, userName, userType, tenantId, cloud);

        var organizationId = await ReadOrganizationIdAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(organizationId, tenantId, StringComparison.OrdinalIgnoreCase))
        {
            AzLog.PreflightTenantMismatch(_logger, organizationId, tenantId);
        }

        return new AzPreflightResult(tenantId, organizationId, userName, userType, azVersion, cloud, subscriptionId, subscriptionName);
    }

    private async Task<string?> ReadAzVersionAsync(CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(AzRequest.ForCommand("version"), cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw AzCliException.FromResult(result, "az version");
        }

        var json = TryParseObject(result.StandardOutput);
        return json is null ? null : ReadString(json, "azure-cli");
    }

    private async Task<JsonObject> ReadAccountAsync(CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(AzRequest.ForCommand("account", "show"), cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            var failure = AzCliException.FromResult(result, "az account show");
            if (failure.Failure == AzCliFailure.Unknown)
            {
                throw new AzCliException(AzCliFailure.NotLoggedIn, $"az account show failed (exit {result.ExitCode}); no Azure CLI session. {AzCliException.LoginHint}", result.ExitCode, result.StandardError);
            }

            throw failure;
        }

        return TryParseObject(result.StandardOutput)
            ?? throw new AzCliException(AzCliFailure.Unknown, "az account show returned no JSON object.", result.ExitCode, result.StandardError);
    }

    private async Task<string> ReadOrganizationIdAsync(CancellationToken cancellationToken)
    {
        JsonNode? organization;
        try
        {
            organization = await _graph.GetAsync(new GraphRequest("/organization").WithSelect("id"), cancellationToken).ConfigureAwait(false);
        }
        catch (GraphException ex) when (ex.IsUnauthorized)
        {
            throw new AzCliException(AzCliFailure.NotLoggedIn, $"Microsoft Graph rejected the Azure CLI token ({ex.Code}). {AzCliException.LoginHint}", innerException: ex);
        }

        if (organization is JsonObject envelope && envelope["value"] is JsonArray values && values.Count > 0 && values[0] is JsonObject first)
        {
            var id = ReadString(first, "id");
            if (!string.IsNullOrEmpty(id))
            {
                return id;
            }
        }

        throw new AzCliException(AzCliFailure.Unknown, "GET /organization returned no organization id; the Graph token may lack Directory.Read.All / Organization.Read.All.");
    }

    private static JsonObject? TryParseObject(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonObject obj, string name)
    {
        return obj[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }
}
