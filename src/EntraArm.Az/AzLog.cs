using System;
using Microsoft.Extensions.Logging;

namespace EntraArm.Az;

/// <summary>
/// Source-generated log messages for the Az transport. Bodies are never logged; url parameters, headers and stderr
/// pass through <see cref="SecretRedactor"/> before reaching these methods.
/// </summary>
internal static partial class AzLog
{
    [LoggerMessage(EventId = 1000, Level = LogLevel.Debug, Message = "az {Command}: {Method} {Url} urlParameters={UrlParameters} headers={Headers} body={HasBody}")]
    public static partial void AzRequestStarting(ILogger logger, string command, string? method, string? url, string urlParameters, string headers, bool hasBody);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Debug, Message = "az argv: {Argv}")]
    public static partial void AzArgv(ILogger logger, string argv);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Debug, Message = "az {Command} exited {ExitCode} after {ElapsedMs} ms (stdout {StdoutLength} chars, stderr {StderrLength} chars, timedOut={TimedOut})")]
    public static partial void AzRequestCompleted(ILogger logger, string command, int exitCode, long elapsedMs, int stdoutLength, int stderrLength, bool timedOut);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Trace, Message = "az stderr: {StandardError}")]
    public static partial void AzStandardError(ILogger logger, string standardError);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Warning, Message = "az {Command} exceeded the {TimeoutSeconds} s timeout; the pwsh process tree was killed")]
    public static partial void AzTimedOut(ILogger logger, string command, double timeoutSeconds);

    [LoggerMessage(EventId = 1010, Level = LogLevel.Debug, Message = "pwsh located at {Path} (version {Version})")]
    public static partial void PwshLocated(ILogger logger, string path, Version version);

    [LoggerMessage(EventId = 1011, Level = LogLevel.Debug, Message = "Invoke-AzRest.ps1 extracted to {Path} (sha256 {Sha256})")]
    public static partial void ScriptExtracted(ILogger logger, string path, string sha256);

    [LoggerMessage(EventId = 1020, Level = LogLevel.Information, Message = "Retrying ({Reason}) in {DelayMs} ms, attempt {Attempt}: {Error}")]
    public static partial void Retrying(ILogger logger, string reason, double delayMs, int attempt, string error);

    [LoggerMessage(EventId = 1021, Level = LogLevel.Information, Message = "Refreshing the Azure CLI access token after a 401 response")]
    public static partial void RefreshingToken(ILogger logger);

    [LoggerMessage(EventId = 1030, Level = LogLevel.Debug, Message = "Graph {Method} {Url} -> {Status}")]
    public static partial void GraphResponse(ILogger logger, string method, string url, int status);

    [LoggerMessage(EventId = 1031, Level = LogLevel.Debug, Message = "Graph $batch: {Count} sub-requests in {Chunks} chunk(s); {Failed} failed and will be retried individually")]
    public static partial void BatchSummary(ILogger logger, int count, int chunks, int failed);

    [LoggerMessage(EventId = 1040, Level = LogLevel.Information, Message = "Azure CLI {AzVersion}; account {User} ({UserType}) in tenant {TenantId}, cloud {Cloud}")]
    public static partial void PreflightAccount(ILogger logger, string? azVersion, string? user, string? userType, string tenantId, string? cloud);

    [LoggerMessage(EventId = 1041, Level = LogLevel.Warning, Message = "Graph organization id {OrganizationId} differs from az account tenantId {TenantId}")]
    public static partial void PreflightTenantMismatch(ILogger logger, string organizationId, string tenantId);
}
