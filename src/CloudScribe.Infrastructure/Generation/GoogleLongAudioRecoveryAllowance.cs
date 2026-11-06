namespace CloudScribe.Infrastructure.Generation;

/// <summary>Current trusted ledger/operation readiness. A proposal never supplies its own allowance.</summary>
public sealed record GoogleLongAudioRecoveryAllowance(long RemainingHoldCents, bool HasPendingOrUncertain, bool HasAcceptedParentOutput);
