namespace CloudScribe.Infrastructure.Generation;

/// <summary>Ordered verified coverage and the exact stopped outcome. Completed means coverage, not final export.</summary>
public sealed record GoogleLongAudioRecoveryRun(bool CoverageComplete, IReadOnlyList<GoogleLongAudioRecoveryOutput> Outputs,
    GoogleLongAudioCheckpoint? StoppedOutcome);
