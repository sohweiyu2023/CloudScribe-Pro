using CloudScribe.Application.Batch;
using Microsoft.Extensions.Logging;

namespace CloudScribe.Application.Logging;

// Batch events carry identifiers, counts, timings, amounts and fixed codes only: never file names, titles, text or paths.
public static partial class CloudScribeLog
{
    [LoggerMessage(EventId = 1640, Level = LogLevel.Information, Message = "Batch {BatchId} created with {Included} included files ({Excluded} excluded).")]
    public static partial void BatchCreated(ILogger logger, Guid batchId, int included, int excluded);

    [LoggerMessage(EventId = 1641, Level = LogLevel.Information, Message = "Batch {BatchId} run started: {Included} included files, {Completed} already complete.")]
    public static partial void BatchRunStarted(ILogger logger, Guid batchId, int included, int completed);

    [LoggerMessage(EventId = 1642, Level = LogLevel.Information, Message = "Batch {BatchId} file {Order} started (continuing a saved job: {ContinuingJob}).")]
    public static partial void BatchItemStarted(ILogger logger, Guid batchId, int order, bool continuingJob);

    [LoggerMessage(EventId = 1643, Level = LogLevel.Information, Message = "Batch {BatchId} file {Order} completed in {ActiveMilliseconds} ms active time; MP3 {OutputBytes} bytes verified.")]
    public static partial void BatchItemCompleted(ILogger logger, Guid batchId, int order, long activeMilliseconds, long outputBytes);

    [LoggerMessage(EventId = 1644, Level = LogLevel.Warning, Message = "Batch {BatchId} file {Order} stopped as {State} ({ReasonCode}).")]
    public static partial void BatchItemStopped(ILogger logger, Guid batchId, int order, BatchItemState state, string reasonCode);

    [LoggerMessage(EventId = 1645, Level = LogLevel.Information, Message = "Batch {BatchId} summary: {State}; {Completed} completed, {Failed} failed, {Uncertain} uncertain of {Included}; active {ActiveMilliseconds} ms; estimated maximum {EstimatedMinorUnits} minor units; reserved {ReservedMinorUnits} minor units (estimates and holds, not a provider bill); reason {ReasonCode}.")]
    public static partial void BatchRunSummary(ILogger logger, Guid batchId, BatchQueueState state, int completed, int failed, int uncertain,
        int included, long activeMilliseconds, long estimatedMinorUnits, long reservedMinorUnits, string reasonCode);

    [LoggerMessage(EventId = 1646, Level = LogLevel.Information, Message = "Batch {BatchId} item {ItemId} at order {Order} is linked to saved job {JobId}. Identity only; no provider acceptance, retry authority or billing outcome is implied.")]
    public static partial void BatchItemJobLinked(ILogger logger, Guid batchId, Guid itemId, int order, Guid jobId);
    [LoggerMessage(EventId = 1647, Level = LogLevel.Warning, Message = "Batch {BatchId} item {ItemId} local operation {Operation} failed; category {FailureCategory}; numeric error {LocalHResult}. No exception text retained. Inspect saved state; this event grants no retry or spending authority.")]
    public static partial void BatchLocalFailure(ILogger logger, Guid? batchId, Guid? itemId, BatchDiagnosticOperation operation,
        BatchDiagnosticFailure failureCategory, int localHResult);

    [LoggerMessage(EventId = 1648, Level = LogLevel.Information, Message = "Batch {BatchId} store operation {Operation}: {Observation}; revision {Revision}. No file names or paths retained.")]
    public static partial void BatchStore(ILogger logger, Guid batchId, BatchDiagnosticOperation operation, BatchStoreObservation observation, long? revision);

    [LoggerMessage(EventId = 1649, Level = LogLevel.Information, Message = "Batch {BatchId} item {ItemId} user action {Operation}: {Phase}. Returned means the handler returned, not approval, provider acceptance or retry authority.")]
    public static partial void BatchAction(ILogger logger, Guid? batchId, Guid? itemId, BatchDiagnosticOperation operation, BatchDiagnosticPhase phase);

    [LoggerMessage(EventId = 1650, Level = LogLevel.Information, Message = "Batch {BatchId} cost denomination: currency {CurrencyCode}, decimal scale {CurrencyScale}. Amounts in summary are minor units, estimates and retained holds, not actual provider charges.")]
    public static partial void BatchCostDenomination(ILogger logger, Guid batchId, string currencyCode, int currencyScale);
}
