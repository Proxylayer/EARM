using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph.Auth;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EArm.Graph;

/// <summary>What the preflight learned about the signed-in identity.</summary>
/// <param name="TenantId">The tenant id: the token's <c>tid</c> claim, else the <c>id</c> of <c>GET /organization</c>.</param>
/// <param name="Organization">The first element of <c>GET /organization?$select=id,displayName,verifiedDomains</c>, or null when the identity may not read it (the one call also feeds <c>tenant()</c>).</param>
/// <param name="PrincipalObjectId">The signed-in principal's object id (the <c>oid</c> claim): a user, or the service principal of an app-only token.</param>
/// <param name="PrincipalName">The user principal name, or the app id for an app-only token.</param>
/// <param name="PrincipalType"><c>user</c> or <c>application</c>.</param>
/// <param name="Cloud">The cloud the token was issued for.</param>
/// <param name="AuthMode">The configured authentication mode.</param>
public sealed record GraphPreflightResult(
    string TenantId,
    JsonObject? Organization,
    string? PrincipalObjectId,
    string? PrincipalName,
    string PrincipalType,
    string? AppId,
    GraphCloud Cloud,
    GraphAuthMode AuthMode)
{
    /// <summary>The environment name (<c>AzureCloud</c>, <c>AzureUSGovernment</c>, ...) that <c>environment().name</c> reports.</summary>
    public string CloudName => Cloud.EnvironmentName;

    /// <summary>The <c>id</c> of the organization, when it could be read.</summary>
    public string? OrganizationId => Organization?["id"] is JsonValue id && id.TryGetValue<string>(out var text) ? text : null;
}

/// <summary>
/// Verifies the sign-in before a deployment: acquires a Graph token (failing with the sign-in guidance, exit code 3 in the CLI), decodes its
/// claims for the tenant and the signed-in principal, and reads <c>GET /organization?$select=id,displayName,verifiedDomains</c> once to confirm that
/// Graph accepts the token and to fetch the tenant information that <c>tenant()</c> returns. App-only tokens have no <c>/me</c>, which is why the
/// principal comes from the token.
/// </summary>
public sealed class GraphPreflight
{
    private readonly IGraphTokenProvider _tokens;
    private readonly IGraphClient _graph;
    private readonly GraphCloud _cloud;
    private readonly GraphAuthMode _mode;
    private readonly ILogger _logger;
    private readonly TokenScopes _scopes;
    private readonly IConsentPrompter? _consentPrompter;

    /// <param name="scopes">The Graph token scopes to inspect: the same set the Graph client attaches. <c>null</c> means <paramref name="cloud"/>'s <c>.default</c>.</param>
    /// <param name="consentPrompter">
    /// Asked once for the explicit scopes the token's <c>scp</c> claim lacks (then the token is requested again); null fails with
    /// <see cref="ConsentRequiredException"/> guidance instead. Unused for <c>.default</c> and for app-only tokens.
    /// </param>
    public GraphPreflight(IGraphTokenProvider tokens, IGraphClient graph, GraphCloud cloud, GraphAuthMode mode, ILogger? logger = null, TokenScopes? scopes = null, IConsentPrompter? consentPrompter = null)
    {
        _consentPrompter = consentPrompter;
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(cloud);
        _tokens = tokens;
        _graph = graph;
        _cloud = cloud;
        _mode = mode;
        _logger = logger ?? NullLogger.Instance;
        _scopes = TokenScopeGuard.Resolve(scopes, cloud.GraphScopes, cloud, nameof(scopes));
    }

    /// <summary>The scopes of the Graph token the preflight inspects.</summary>
    public TokenScopes Scopes => _scopes;

    /// <exception cref="GraphTransportException">No token (<see cref="GraphTransportFailure.NotAuthenticated"/>), Graph rejected it, or the network failed.</exception>
    /// <exception cref="GraphException">Graph answered the organization read with an error other than 401 / 403.</exception>
    public async Task<GraphPreflightResult> RunAsync(CancellationToken cancellationToken)
    {
        var token = await _tokens.GetTokenAsync(_scopes, cancellationToken).ConfigureAwait(false);
        var hasClaims = TokenClaims.TryDecode(token.Value, out var claims);
        if (hasClaims)
        {
            claims = await EnsureScopesAsync(claims, cancellationToken).ConfigureAwait(false);
        }

        JsonObject? organization = null;
        try
        {
            var response = await _graph.GetAsync(new GraphRequest("/organization").WithSelect("id", "displayName", "verifiedDomains"), cancellationToken).ConfigureAwait(false);
            if (response is JsonObject envelope && envelope["value"] is JsonArray values && values.Count > 0 && values[0] is JsonObject first)
            {
                organization = first;
            }
        }
        catch (GraphException ex) when (ex.IsUnauthorized)
        {
            _tokens.Invalidate(_scopes);
            throw new GraphTransportException(
                GraphTransportFailure.NotAuthenticated,
                $"Microsoft Graph rejected the access token ({ex.Code}). Check that the identity belongs to the intended tenant and cloud ({_cloud.Key}), then sign in again. Request id {ex.RequestId ?? "n/a"}.",
                ex);
        }
        catch (GraphException ex) when (ex.IsForbidden)
        {
            GraphLog.PreflightOrganizationUnreadable(_logger, ex.Code);
        }

        var organizationId = organization?["id"] is JsonValue idValue && idValue.TryGetValue<string>(out var idText) ? idText : null;
        var tenantId = hasClaims && !string.IsNullOrEmpty(claims.TenantId) ? claims.TenantId : organizationId;
        if (string.IsNullOrEmpty(tenantId))
        {
            throw new GraphTransportException(
                GraphTransportFailure.InvalidResponse,
                "The tenant of the signed-in identity could not be determined: the access token carries no tenant id and GET /organization returned no organization. Grant the identity Organization.Read.All (or Directory.Read.All).");
        }

        if (organizationId is not null && !string.Equals(organizationId, tenantId, StringComparison.OrdinalIgnoreCase))
        {
            GraphLog.PreflightTenantMismatch(_logger, organizationId, tenantId);
        }

        var result = new GraphPreflightResult(
            tenantId,
            organization,
            hasClaims ? claims.ObjectId : null,
            hasClaims ? claims.PrincipalName : null,
            hasClaims ? claims.PrincipalType : "user",
            hasClaims ? claims.AppId : null,
            _cloud,
            _mode);
        GraphLog.PreflightAccount(_logger, tenantId, result.PrincipalName, result.PrincipalType, _cloud.Key, GraphAuthOptions.ModeName(_mode));
        return result;
    }

    /// <summary>The explicit scopes of <see cref="Scopes"/> that <paramref name="claims"/>' <c>scp</c> lacks (case-insensitive); empty for <c>.default</c> and app-only tokens.</summary>
    public IReadOnlyList<string> MissingScopes(TokenClaims claims)
    {
        ArgumentNullException.ThrowIfNull(claims);
        if (_scopes.IsDefault || claims.IsApplication)
        {
            return [];
        }

        var granted = new HashSet<string>(claims.Scopes, StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        foreach (var name in _scopes.Names)
        {
            if (!granted.Contains(name))
            {
                missing.Add(name);
            }
        }

        return missing;
    }

    /// <summary>
    /// The <c>scp</c> check, before anything is read or written: every requested delegated scope must be in the token. Missing scopes are prompted for once
    /// (when a prompter is available) and the token requested again; otherwise, or when they are still missing, the run stops with the consent guidance.
    /// </summary>
    private async Task<TokenClaims> EnsureScopesAsync(TokenClaims claims, CancellationToken cancellationToken)
    {
        var missing = MissingScopes(claims);
        if (missing.Count == 0)
        {
            return claims;
        }

        var consent = new ConsentRequiredException(TokenScopes.Explicit(_scopes.Resource, missing), claims.PrincipalName, claims.AppId, innerException: null, ConsentGap.ScopesMissingFromToken);
        if (_consentPrompter is null)
        {
            throw ScopesMissing(consent, "The access token lacks requested delegated scopes (scp: " + Describe(claims.Scopes) + ").");
        }

        try
        {
            await _consentPrompter.ConsentAsync(consent.Scopes, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var detail = GraphAuthGuidance.CredentialLines(ex);
            throw ScopesMissing(new ConsentRequiredException(consent.Scopes, consent.Account, consent.ClientId, ex, consent.Gap), "Consent for " + consent.Scopes + " was not completed: " + (detail.Count > 0 ? detail[0] : ex.GetType().Name));
        }

        _tokens.Invalidate(_scopes);
        var token = await _tokens.GetTokenAsync(_scopes, cancellationToken).ConfigureAwait(false);
        if (!TokenClaims.TryDecode(token.Value, out var renewed))
        {
            return claims;
        }

        var stillMissing = MissingScopes(renewed);
        if (stillMissing.Count > 0)
        {
            throw ScopesMissing(
                new ConsentRequiredException(TokenScopes.Explicit(_scopes.Resource, stillMissing), renewed.PrincipalName, renewed.AppId, innerException: null, ConsentGap.ScopesMissingFromToken),
                "Consent was completed, but the access token still lacks requested delegated scopes (scp: " + Describe(renewed.Scopes) + ").");
        }

        return renewed;
    }

    private static string Describe(IReadOnlyList<string> scopes)
    {
        return scopes.Count == 0 ? "none" : string.Join(' ', scopes);
    }

    private static GraphTransportException ScopesMissing(ConsentRequiredException consent, string prefix)
    {
        return new GraphTransportException(GraphTransportFailure.NotAuthenticated, prefix + Environment.NewLine + GraphAuthGuidance.ConsentNeeded(consent), consent);
    }
}
