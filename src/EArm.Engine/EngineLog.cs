using Microsoft.Extensions.Logging;

namespace EArm.Engine;

/// <summary>Source-generated log messages of the engine. Resource ids and Graph ids are logged; properties never are.</summary>
internal static partial class EngineLog
{
    [LoggerMessage(EventId = 2000, Level = LogLevel.Debug, Message = "Expanded {DeclarationCount} declaration(s) into {InstanceCount} resource instance(s)")]
    public static partial void Expanded(ILogger logger, int declarationCount, int instanceCount);

    [LoggerMessage(EventId = 2001, Level = LogLevel.Debug, Message = "Dependency graph: {EdgeCount} edge(s), {BatchCount} batch(es)")]
    public static partial void GraphBuilt(ILogger logger, int edgeCount, int batchCount);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Debug, Message = "Batch {Batch}: {Resources}")]
    public static partial void BatchContents(ILogger logger, int batch, string resources);

    [LoggerMessage(EventId = 2003, Level = LogLevel.Debug, Message = "Symbolic name of {PlannedId} resolved to {ResourceId}")]
    public static partial void SymbolicNameResolved(ILogger logger, string plannedId, string resourceId);

    [LoggerMessage(EventId = 2010, Level = LogLevel.Debug, Message = "Planned {ResourceId}: {ChangeType} ({Mode}), {DeltaCount} delta(s)")]
    public static partial void Planned(ILogger logger, string resourceId, string changeType, string mode, int deltaCount);

    [LoggerMessage(EventId = 2011, Level = LogLevel.Warning, Message = "Reading {ResourceId} failed: {Error}")]
    public static partial void ReadFailed(ILogger logger, string resourceId, string error);

    [LoggerMessage(EventId = 2012, Level = LogLevel.Warning, Message = "Existing-resource lookup {ResourceId} (apiVersion {ApiVersion}) could not be resolved: {Reason}")]
    public static partial void ExistingLookupUnresolved(ILogger logger, string resourceId, string apiVersion, string reason);

    [LoggerMessage(EventId = 2013, Level = LogLevel.Warning, Message = "The organization could not be read; tenant() stays a deploy-time unknown: {Reason}")]
    public static partial void TenantInfoUnavailable(ILogger logger, string reason);

    [LoggerMessage(EventId = 2020, Level = LogLevel.Information, Message = "Applying {ResourceId}: {ChangeType} ({Mode})")]
    public static partial void Applying(ILogger logger, string resourceId, string changeType, string mode);

    [LoggerMessage(EventId = 2021, Level = LogLevel.Information, Message = "Applied {ResourceId}: {Outcome} (Graph id {GraphId}) in {ElapsedMs} ms")]
    public static partial void Applied(ILogger logger, string resourceId, string outcome, string graphId, long elapsedMs);

    [LoggerMessage(EventId = 2022, Level = LogLevel.Error, Message = "Applying {ResourceId} failed: {Error}")]
    public static partial void ApplyFailed(ILogger logger, string resourceId, string error);

    [LoggerMessage(EventId = 2023, Level = LogLevel.Warning, Message = "Skipping {ResourceId}: {Reason}")]
    public static partial void Skipped(ILogger logger, string resourceId, string reason);

    [LoggerMessage(EventId = 2024, Level = LogLevel.Information, Message = "Create of {ResourceId} reported 'already exists'; adopting the existing object")]
    public static partial void AdoptingExisting(ILogger logger, string resourceId);

    [LoggerMessage(EventId = 2025, Level = LogLevel.Warning, Message = "Stopping after batch {Batch}: {FailedCount} resource(s) failed (use --continue-on-error to keep deploying independent resources)")]
    public static partial void StoppingAfterBatch(ILogger logger, int batch, int failedCount);

    [LoggerMessage(EventId = 2030, Level = LogLevel.Information, Message = "Deployment {DeploymentName} {Status}: {Succeeded} succeeded, {Failed} failed, {Skipped} skipped, {Ignored} ignored in {ElapsedMs} ms")]
    public static partial void Completed(ILogger logger, string deploymentName, string status, int succeeded, int failed, int skipped, int ignored, long elapsedMs);

    [LoggerMessage(EventId = 2031, Level = LogLevel.Information, Message = "Secure outputs written to {Path}")]
    public static partial void SecureOutputsWritten(ILogger logger, string path);

    // ---- state (WI-06) ----

    [LoggerMessage(EventId = 2040, Level = LogLevel.Warning, Message = "Drift: the state pins {ResourceId} to Graph id {StateGraphId} but the live object was {Outcome}")]
    public static partial void StateDrift(ILogger logger, string resourceId, string stateGraphId, string outcome);

    [LoggerMessage(EventId = 2041, Level = LogLevel.Warning, Message = "{ResourceId} is recorded in the state but no longer declared in the template; detached from the state (use --mode Complete to delete it)")]
    public static partial void ResourceDetached(ILogger logger, string resourceId);

    [LoggerMessage(EventId = 2042, Level = LogLevel.Information, Message = "Deleting {ResourceId} (Graph id {GraphId}): not declared in the template (Complete mode)")]
    public static partial void DeletingOrphan(ILogger logger, string resourceId, string graphId);

    [LoggerMessage(EventId = 2043, Level = LogLevel.Information, Message = "{ResourceId} was already gone from the tenant; removed from the state")]
    public static partial void OrphanAlreadyGone(ILogger logger, string resourceId);

    [LoggerMessage(EventId = 2044, Level = LogLevel.Error, Message = "Deleting {ResourceId} failed: {Error}")]
    public static partial void DeleteFailed(ILogger logger, string resourceId, string error);

    [LoggerMessage(EventId = 2045, Level = LogLevel.Warning, Message = "The mode pass cannot decide about state entries of type {Type}: a resource of that type still has a deploy-time name ({PlannedId}); those entries are kept")]
    public static partial void ModePassUndecided(ILogger logger, string type, string plannedId);

    [LoggerMessage(EventId = 2046, Level = LogLevel.Debug, Message = "State saved to {Path} ({ResourceCount} resource(s))")]
    public static partial void StateSaved(ILogger logger, string path, int resourceCount);

    [LoggerMessage(EventId = 2047, Level = LogLevel.Warning, Message = "Taking over the stale state lock {Path} held by {Holder} (older than {StaleAfter})")]
    public static partial void StaleLockTakenOver(ILogger logger, string path, string holder, string staleAfter);

    [LoggerMessage(EventId = 2048, Level = LogLevel.Information, Message = "Waiting for the state lock {Path} held by {Holder}")]
    public static partial void WaitingForLock(ILogger logger, string path, string holder);

    [LoggerMessage(EventId = 2049, Level = LogLevel.Warning, Message = "The state lock {Path} could not be deleted on release ({Error}); it stays until it is removed with 'state unlock' or taken over as stale (older than {StaleAfter})")]
    public static partial void StateLockReleaseFailed(ILogger logger, string path, string error, string staleAfter);

    [LoggerMessage(EventId = 2050, Level = LogLevel.Information, Message = "Parameter '{Parameter}' resolved from {Reference}")]
    public static partial void KeyVaultResolved(ILogger logger, string parameter, string reference);

    // ---- edge pass (WI-09) ----

    [LoggerMessage(EventId = 2060, Level = LogLevel.Information, Message = "Edge pass: {RowCount} resource(s) declare relationships, {OperationCount} edge operation(s) planned")]
    public static partial void EdgePassStarting(ILogger logger, int rowCount, int operationCount);

    [LoggerMessage(EventId = 2061, Level = LogLevel.Warning, Message = "Edge pass skipped: an earlier batch failed; relationships were not reconciled")]
    public static partial void EdgePassSkipped(ILogger logger);

    [LoggerMessage(EventId = 2062, Level = LogLevel.Information, Message = "Edge {Operation}: {Outcome}")]
    public static partial void EdgeApplied(ILogger logger, string operation, string outcome);

    [LoggerMessage(EventId = 2063, Level = LogLevel.Error, Message = "Edge {Operation} failed: {Error}")]
    public static partial void EdgeApplyFailed(ILogger logger, string operation, string error);

    [LoggerMessage(EventId = 2064, Level = LogLevel.Error, Message = "Relationships of {ResourceId} not applied: {Error}")]
    public static partial void EdgeValidationFailed(ILogger logger, string resourceId, string error);

    [LoggerMessage(EventId = 2065, Level = LogLevel.Debug, Message = "Recorded edge of {ResourceId} skipped: {Reason}")]
    public static partial void RecordedEdgeSkipped(ILogger logger, string resourceId, string reason);

    [LoggerMessage(EventId = 2066, Level = LogLevel.Information, Message = "Removed {Count} recorded edge(s) of {ResourceId} before deleting it")]
    public static partial void RecordedEdgesRemoved(ILogger logger, string resourceId, int count);

    [LoggerMessage(EventId = 2067, Level = LogLevel.Warning, Message = "Deleting {ResourceId}: {Warning}")]
    public static partial void DeleteWarning(ILogger logger, string resourceId, string warning);
}
