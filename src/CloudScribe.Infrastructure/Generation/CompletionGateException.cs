using CloudScribe.Domain.Generation;

namespace CloudScribe.Infrastructure.Generation;

public sealed class CompletionGateException(CompletionGateFailure reason, CompletionProtocolCode code = CompletionProtocolCode.Unknown,
    CompletionProtocolExceptionType failureType = CompletionProtocolExceptionType.Other,
    CompletionProtocolFailureComponent component = CompletionProtocolFailureComponent.Unavailable, int sourceLine = 0)
    : InvalidOperationException($"Completion workflow stopped: {reason}/{code}; {component}:{sourceLine} ({failureType}). Reconcile the private register; no automatic retry.")
{
    public CompletionGateFailure Reason { get; } = reason;
    public CompletionProtocolCode Code { get; } = code;
    public CompletionProtocolExceptionType FailureType { get; } = failureType;
    public CompletionProtocolFailureComponent Component { get; } = component;
    public int SourceLine { get; } = sourceLine is >= 1 and <= 10000 ? sourceLine : 0;
}
