namespace CloudScribe.Application.Generation;

public sealed record MultiSpeakerTurnExecutionOutcome(
    int TurnIndex,
    bool Succeeded,
    bool RequiresReconciliation,
    string DiagnosticCode);
