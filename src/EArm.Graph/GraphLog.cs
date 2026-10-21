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

    [LoggerMessage(EventId = 1024, Level = LogLevel.Information, Message = "PIM for Groups answered {Method} {PathAndQuery} with 403 UnauthorizedAccessException: treated as replication lag (the group or principal is new to PIM); the request is repeated on the replication schedule")]
    public static partial void PimGroupForbiddenAsReplication(ILogger logger, string method, string pathAndQuery);

    [LoggerMessage(EventId = 1025, Level = LogLevel.Debug, Message = "PIM for Groups answered {Method} {PathAndQuery} with 403 UnauthorizedAccessException (not repeated: no replication hint); a schedule list read treats it as a group PIM has not onboarded yet")]
    public static partial void PimGroupForbidden(ILogger logger, string method, string pathAndQuery);

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

    [LoggerMessage(EventId = 1046, Level = LogLevel.Information, Message = "Additional consent is needed for {Scopes}; starting the interactive consent prompt")]
    public static partial void ConsentPrompting(ILogger logger, string scopes);

    [LoggerMessage(EventId = 1047, Level = LogLevel.Warning, Message = "The stored sign-in of {Account} uses the Microsoft Azure CLI app ({ClientId}), which is pre-authorized for a fixed set of Microsoft Graph permissions only and cannot consent to the delegated scopes EARM requests. Run 'earm auth login' again to switch to the default sign-in app, Microsoft Graph Command Line Tools ({DefaultClientId}).")]
    public static partial void LegacyClientRecord(ILogger logger, string? account, string clientId, string defaultClientId);

    [LoggerMessage(EventId = 1060, Level = LogLevel.Debug, Message = "Graph request scheduler: a request to {Area} waits {DelayMs:F0} ms ({Reason})")]
    public static partial void SchedulerDelay(ILogger logger, string area, double delayMs, string reason);

    [LoggerMessage(EventId = 1061, Level = LogLevel.Information, Message = "Pacing Microsoft Graph requests to {Area} to stay within Microsoft's published limits: {Count} request(s) waited {TotalSeconds:F1} s in total since the last report")]
    public static partial void SchedulerPacing(ILogger logger, string area, int count, double totalSeconds);

    [LoggerMessage(EventId = 1062, Level = LogLevel.Information, Message = "Microsoft Graph throttled a request to {Area} (HTTP 429, throttle scope {Scope}); every request to that area waits {PauseSeconds:F1} s")]
    public static partial void SchedulerPaused(ILogger logger, string area, string scope, double pauseSeconds);

    [LoggerMessage(EventId = 1063, Level = LogLevel.Information, Message = "The tenant has {Users} users (tenant size {Tier}): the directory budget is {ResourceUnits} resource units per 10 s")]
    public static partial void SchedulerTier(ILogger logger, long users, string tier, int resourceUnits);

    [LoggerMessage(EventId = 1064, Level = LogLevel.Information, Message = "The tenant size could not be read ({Reason}); the directory budget stays at the smallest tier, {ResourceUnits} resource units per 10 s (reading GET /users/$count needs User.ReadBasic.All or more)")]
    public static partial void SchedulerTierUnknown(ILogger logger, string reason, int resourceUnits);

    [LoggerMessage(EventId = 1065, Level = LogLevel.Information, Message = "Microsoft Graph reports this app's directory usage at {Percentage} of its limit (throttle scope {Scope})")]
    public static partial void SchedulerNearLimit(ILogger logger, double percentage, string scope);

    [LoggerMessage(EventId = 1050, Level = LogLevel.Debug, Message = "Acquired a token for {Scope} (expires {ExpiresOn:u}) from {Credential}")]
    public static partial void TokenAcquired(ILogger logger, string scope, DateTimeOffset expiresOn, string credential);
}
