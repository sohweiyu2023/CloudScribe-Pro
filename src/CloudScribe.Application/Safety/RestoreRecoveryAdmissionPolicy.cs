namespace CloudScribe.Application.Safety;

public sealed record RestoreRecoveryState(
    bool JournalAuthenticated,
    bool PlanIdentityMatches,
    bool StagingRootTrusted,
    bool DestinationRootTrusted,
    bool RollbackRequired,
    bool AlreadyRolledBack);

public static class RestoreRecoveryAdmissionPolicy
{
    public static string RequireRecoveryAction(RestoreRecoveryState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!state.JournalAuthenticated)
            throw new InvalidOperationException("Restore recovery journal is not authenticated.");
        if (!state.PlanIdentityMatches)
            throw new InvalidOperationException("Restore recovery journal does not match the verified restore plan.");
        if (!state.StagingRootTrusted || !state.DestinationRootTrusted)
            throw new InvalidOperationException("Restore recovery filesystem roots are not trusted.");
        if (state.AlreadyRolledBack && state.RollbackRequired)
            throw new InvalidOperationException("Restore recovery state is contradictory: already rolled back and rollback required.");
        if (state.AlreadyRolledBack)
            return "no-op-terminal-rolled-back";
        if (state.RollbackRequired)
            return "rollback-only";
        return "resume-verified-apply";
    }
}
