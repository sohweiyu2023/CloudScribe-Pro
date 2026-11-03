using System.Diagnostics;
using CloudScribe.Application.Logging;
using CloudScribe.Domain.Generation;
using Microsoft.Extensions.Logging;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class CompletionWorkflowGate
{
    private void RunObserved(CompletionGateConfiguration configuration, string command, Guid id, string identity,
        IReadOnlyDictionary<string, object?>? receipt)
    {
        long started = Stopwatch.GetTimestamp();
        try { _runner.Run(configuration, command, id, identity, receipt); }
        catch (Exception error) when (GoogleCatalogRefreshDiagnostics.IsNonFatal(error))
        {
            ObserveFailure(command, id, error, started);
            throw;
        }
    }

    private bool CheckSynthesisObserved(CompletionGateConfiguration configuration, Guid id, string payloadSha256, string? keySha256)
    {
        long started = Stopwatch.GetTimestamp();
        try
        {
            var decision = _runner.CheckSynthesis(configuration, id, payloadSha256);
            if (decision.RequireProtectedCapture && (keySha256 is null || keySha256.Length != 64
                || !string.Equals(keySha256, decision.IdempotencyKeySha256, StringComparison.Ordinal)))
                throw new CompletionGateException(CompletionGateFailure.PinMismatch, CompletionProtocolCode.DiagnosticOwnedClaim);
            return decision.RequireProtectedCapture;
        }
        catch (Exception error) when (GoogleCatalogRefreshDiagnostics.IsNonFatal(error))
        { ObserveFailure("check-diagnostic-dispatch", id, error, started); throw; }
    }

    private void ObserveFailure(string command, Guid id, Exception error, long started)
    {
        if (_logger is not null && CloudScribeLog.ObserveIsEnabled(_logger, LogLevel.Warning))
            CloudScribeLog.Observe(() => CloudScribeLog.CompletionGuardRejected(_logger, id, Operation(command),
                error is CompletionGateException gate ? (int)gate.Reason : (int)CompletionGateFailure.Unknown,
                error is CompletionGateException coded ? coded.Code : CompletionProtocolCode.Unknown,
                LocalRequestDiagnostics.Describe(error, DateTimeOffset.UtcNow).Reason,
                error is CompletionGateException located ? located.Component : CompletionProtocolFailureComponent.Unavailable,
                error is CompletionGateException numbered ? numbered.SourceLine : 0,
                error is CompletionGateException typed ? typed.FailureType : CompletionProtocolExceptionType.Other,
                checked((long)Stopwatch.GetElapsedTime(started).TotalMilliseconds)));
    }

    private static CompletionProtocolOperation Operation(string command) => command switch
    {
        "status" => CompletionProtocolOperation.Status,
        "claim-refresh" => CompletionProtocolOperation.RefreshClaim,
        "check" => CompletionProtocolOperation.AdmissionCheck,
        "complete" => CompletionProtocolOperation.Complete,
        "check-diagnostic-dispatch" => CompletionProtocolOperation.SynthesisDispatch,
        "check-diagnostic-admission" => CompletionProtocolOperation.AdmissionCheck,
        _ => CompletionProtocolOperation.Other,
    };
}
