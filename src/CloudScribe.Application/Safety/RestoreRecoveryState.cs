namespace CloudScribe.Application.Safety;

public sealed record RestoreRecoveryState(
    bool JournalAuthenticated,
    bool PlanIdentityMatches,
    bool StagingRootTrusted,
    bool DestinationRootTrusted,
    bool RollbackRequired,
    bool AlreadyRolledBack);
