using System;
using Microsoft.Extensions.Logging;

namespace EArm.Graph;

/// <summary>
/// Source-generated log messages of the Graph transport. Tokens, Authorization headers and request bodies are never logged;
/// the path and query reaching these methods have already passed <see cref="GraphUrl.RedactForLog"/>.
/// </summary>
internal static partial class GraphLog
{
    [LoggerMessage(EventId = 1000, Level = LogLevel.Debug, Message = "Graph {Method} {PathAndQuery} -> {Status} in {ElapsedMs} ms (request-id {RequestId}, client-request-id {ClientRequestId})")]
    public static partial void GraphResponse(ILogger logger, string method, string pathAndQuery, int status, long elapsedMs, string? requestId, string clientRequestId);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Debug, Message = "Graph {Method} {PathAndQuery} failed after {ElapsedMs} ms: {Failure} (client-request-id {ClientRequestId})")]
    public static partial void GraphTransportFailed(ILogger logger, string method, string pathAndQuery, long elapsedMs, string failure, string clientRequestId);

    [LoggerMessage(EventId = 1020, Level = LogLevel.Information, Message = "Retrying ({Reason}) in {DelayMs} ms, attempt {Attempt}: {Error}")]
    public static partial void Retrying(ILogger logger, string reason, double delayMs, int attempt, string error);

    [LoggerMessage(EventId = 1022, Level = LogLevel.Warning, Message = "The service asked for a Retry-After of {RequestedSeconds} s; waiting {AppliedSeconds} s instead (the per-attempt cap)")]
    public static partial void RetryAfterClamped(ILogger logger, double requestedSeconds, double appliedSeconds);

    [LoggerMessage(EventId = 1023, Level = LogLevel.Warning, Message = "Giving up retrying: the call has already taken {ElapsedSeconds:F0} s of its {BudgetSeconds:F0} s retry budget")]
    public static partial void RetryBudgetExhausted(ILogger logger, double elapsedSeconds, double budgetSeconds);

    [LoggerMessage(EventId = 1021, Level = LogLevel.Information, Message = "Refreshing the access token after a 401 response")]
    public static partial void RefreshingToken(ILogger logger);

    [LoggerMessage(EventId = 1031, Level = LogLevel.Debug, Message = "Graph $batch: {Count} sub-requests in {Chunks} chunk(s); {Failed} failed and will be retried individually")]
    public static partial void BatchSummary(ILogger logger, int count, int chunks, int failed);

    [LoggerMessage(EventId = 1040, Level = LogLevel.Information, Message = "Signed in to tenant {TenantId} as {Principal} ({PrincipalType}), cloud {Cloud}, credential {Credential}")]
    public static partial void PreflightAccount(ILogger logger, string tenantId, string? principal, string? principalType, string cloud, string credential);

    [LoggerMessage(EventId = 1041, Level = LogLevel.Warning, Message = "Graph organization id {OrganizationId} differs from the token's tenant id {TenantId}")]
    public static partial void PreflightTenantMismatch(ILogger logger, string organizationId, string tenantId);

    [LoggerMessage(EventId = 1042, Level = LogLevel.Warning, Message = "The identity may not read /organization ({Reason}); tenant() stays unknown. Grant Organization.Read.All or Directory.Read.All to enable it.")]
    public static partial void PreflightOrganizationUnreadable(ILogger logger, string reason);

    [LoggerMessage(EventId = 1043, Level = LogLevel.Information, Message = "Authenticated as {Principal} ({PrincipalType}); tenant {TenantId}, object id {ObjectId}, app id {AppId}, credential {Credential}")]
    public static partial void IdentityUsed(ILogger logger, string? principal, string principalType, string? tenantId, string? objectId, string? appId, string credential);

    [LoggerMessage(EventId = 1044, Level = LogLevel.Information, Message = "Authenticated with credential {Credential}; the access token carries no readable identity claims")]
    public static partial void IdentityUnreadable(ILogger logger, string credential);

    [LoggerMessage(EventId = 1045, Level = LogLevel.Information, Message = "Ignoring the stored interactive sign-in: it belongs to {Authority} but the cloud is {Cloud}; using the default credential chain")]
    public static partial void AuthRecordIgnored(ILogger logger, string authority, string cloud);

    [LoggerMessage(EventId = 1050, Level = LogLevel.Debug, Message = "Acquired a token for {Scope} (expires {ExpiresOn:u}) from {Credential}")]
    public static partial void TokenAcquired(ILogger logger, string scope, DateTimeOffset expiresOn, string credential);
}
