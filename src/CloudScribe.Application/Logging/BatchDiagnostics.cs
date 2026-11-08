using System.Data.Common;
using System.Text.Json;
using CloudScribe.Application.Batch;
using Microsoft.Extensions.Logging;

namespace CloudScribe.Application.Logging;

/// <summary>Advisory, content-free batch observations. Never inspect exception messages, paths or arbitrary type names.</summary>
public static class BatchDiagnostics
{
    public static void Source(ILogger? logger, Guid? batchId, Guid itemId, BatchSourceOutcome outcome)
    {
        if (logger is not null) CloudScribeLog.Observe(() => CloudScribeLog.BatchSource(logger, batchId, itemId, outcome));
    }

    public static bool NonFatal(Exception error) => error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException;

    public static void Failure(ILogger? logger, Guid? batchId, Guid? itemId, BatchDiagnosticOperation operation, Exception error)
    {
        if (logger is null) return;
        CloudScribeLog.Observe(() => CloudScribeLog.BatchLocalFailure(logger, batchId, itemId, operation, Classify(error), error.HResult));
    }

    public static void Action(ILogger? logger, Guid? batchId, Guid? itemId, BatchDiagnosticOperation operation, BatchDiagnosticPhase phase)
    {
        if (logger is not null) CloudScribeLog.Observe(() => CloudScribeLog.BatchAction(logger, batchId, itemId, operation, phase));
    }

    public static void Store(ILogger? logger, Guid batchId, BatchDiagnosticOperation operation, BatchStoreObservation observation, long? revision = null)
    {
        if (logger is not null) CloudScribeLog.Observe(() => CloudScribeLog.BatchStore(logger, batchId, operation, observation, revision));
    }

    private static BatchDiagnosticFailure Classify(Exception error) => error switch
    {
        BatchConcurrencyException => BatchDiagnosticFailure.Concurrency,
        BatchAlreadyRunningException => BatchDiagnosticFailure.AlreadyRunning,
        BatchSettingsChangedException => BatchDiagnosticFailure.SettingsChanged,
        BatchNotAuthorizedException => BatchDiagnosticFailure.NotAuthorized,
        OperationCanceledException => BatchDiagnosticFailure.Cancelled,
        TimeoutException => BatchDiagnosticFailure.Timeout,
        UnauthorizedAccessException or System.Security.SecurityException => BatchDiagnosticFailure.AccessDenied,
        FileNotFoundException or DirectoryNotFoundException => BatchDiagnosticFailure.MissingFile,
        DbException => BatchDiagnosticFailure.Database,
        JsonException => BatchDiagnosticFailure.MalformedData,
        InvalidDataException => BatchDiagnosticFailure.InvalidData,
        IOException => BatchDiagnosticFailure.Storage,
        HttpRequestException => BatchDiagnosticFailure.Network,
        InvalidOperationException or ArgumentException or NotSupportedException => BatchDiagnosticFailure.Validation,
        _ => BatchDiagnosticFailure.Unknown,
    };
}
