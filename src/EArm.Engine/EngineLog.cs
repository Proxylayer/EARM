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

    // ---- delete ----

    [LoggerMessage(EventId = 2042, Level = LogLevel.Information, Message = "Deleting {ResourceId} (Graph id {GraphId}) (deployment delete)")]
    public static partial void Deleting(ILogger logger, string resourceId, string graphId);

    [LoggerMessage(EventId = 2044, Level = LogLevel.Error, Message = "Deleting {ResourceId} failed: {Error}")]
    public static partial void DeleteFailed(ILogger logger, string resourceId, string error);

    [LoggerMessage(EventId = 2050, Level = LogLevel.Information, Message = "Parameter '{Parameter}' resolved from {Reference}")]
    public static partial void KeyVaultResolved(ILogger logger, string parameter, string reference);

    // ---- edge pass ----

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

    [LoggerMessage(EventId = 2067, Level = LogLevel.Warning, Message = "Deleting {ResourceId}: {Warning}")]
    public static partial void DeleteWarning(ILogger logger, string resourceId, string warning);

    // ---- safety review ----

    [LoggerMessage(EventId = 2070, Level = LogLevel.Warning, Message = "{ResourceId} was not deleted: {Reason}")]
    public static partial void DeleteRefused(ILogger logger, string resourceId, string reason);

    [LoggerMessage(EventId = 2073, Level = LogLevel.Warning, Message = "The create of {ResourceId} ended with an unknown outcome ({Error}); the object exists (Graph id {GraphId}), so the create is counted as done")]
    public static partial void CreateOutcomeUncertain(ILogger logger, string resourceId, string error, string graphId);

    [LoggerMessage(EventId = 2076, Level = LogLevel.Warning, Message = "{ResourceId} and {OtherResourceId} resolve to the same Graph object {GraphId}")]
    public static partial void DuplicateGraphObject(ILogger logger, string resourceId, string otherResourceId, string graphId);

    [LoggerMessage(EventId = 2078, Level = LogLevel.Information, Message = "The one-time secret of {ResourceId} was saved to the secure outputs file immediately")]
    public static partial void OneTimeSecretSaved(ILogger logger, string resourceId);

    [LoggerMessage(EventId = 2079, Level = LogLevel.Error, Message = "Saving the secure outputs file failed: {Error}")]
    public static partial void SecureOutputsSaveFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 2080, Level = LogLevel.Information, Message = "Not found yet after the create, reading {Key} again (waited {WaitedSeconds} s of {WindowSeconds} s for replication)")]
    public static partial void WaitingForReplication(ILogger logger, string key, double waitedSeconds, double windowSeconds);

    [LoggerMessage(EventId = 2081, Level = LogLevel.Information, Message = "Deleting {ResourceId} was refused ({Code}), most likely because an earlier change of this run has not reached Graph yet; deleting again (waited {WaitedSeconds} s of {WindowSeconds} s)")]
    public static partial void RetryingRefusedDelete(ILogger logger, string resourceId, string code, double waitedSeconds, double windowSeconds);

    [LoggerMessage(EventId = 2082, Level = LogLevel.Information, Message = "Nothing found for {Key}; reading it again before planning a create ({Attempt} of {ExtraReads}): the list can omit an existing object for a while")]
    public static partial void ConfirmingAbsence(ILogger logger, string key, int attempt, int extraReads);

    [LoggerMessage(EventId = 2083, Level = LogLevel.Warning, Message = "{Key} was found (Graph id {GraphId}) on extra read {Attempt} after an empty read; it is treated as existing, not created again")]
    public static partial void FoundOnReread(ILogger logger, string key, string graphId, int attempt);
}
