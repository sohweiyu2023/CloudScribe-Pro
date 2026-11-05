namespace CloudScribe.Infrastructure.Generation;

public static partial class ChirpPolicyValidation
{
    internal static ChirpArticleAdmissionPolicy? PreviousContinuityPolicy(ChirpArticleAdmissionPolicy policy) =>
        policy.RecoveryContinuation?.PreviousPolicy ?? policy.CompletionContinuity?.PreviousPolicy;

    public static bool IsCompletionBound(ChirpArticleAdmissionPolicy policy)
    {
        HashSet<string> visited = new(StringComparer.Ordinal);
        while (true)
        {
            if (!visited.Add(policy.SourceApprovalSha256)) throw new InvalidDataException("Continuity approval cycle detected.");
            if (policy.CompletionContinuity is not null) return true;
            if (PreviousContinuityPolicy(policy) is not { } previous) return false;
            policy = previous;
        }
    }

    internal static bool IsJobExpired(ChirpWholeArticleJob job, DateTimeOffset now) => !job.CompletionBound && now >= job.ExpiresAtUtc;

    private static void RequireCompletionContinuity(ChirpArticleAdmissionPolicy p, DateTimeOffset now)
    {
        if (p.CompletionContinuity is not { } c) return;
        var old = c.PreviousPolicy;
        RequireContinuationDepth(p);
        if (c.Version != 1 || c.JobId == Guid.Empty || !IsHash(c.PreviousJobSha256) || old is null
            || p.RecoveryContinuation is not null || p.RecoveryLimitRevision is not null || p.SchemaVersion != 3 || old.SchemaVersion != 3
            || p.ExpiresAtUtc != old.ExpiresAtUtc || string.Equals(p.SourceApprovalSha256, old.SourceApprovalSha256, StringComparison.Ordinal)
            || !string.Equals(p.ProfileRootSha256, old.ProfileRootSha256, StringComparison.Ordinal)
            || !string.Equals(p.AccountId, old.AccountId, StringComparison.Ordinal) || !string.Equals(p.ProjectId, old.ProjectId, StringComparison.Ordinal)
            || !string.Equals(Hash(p.Articles), Hash(old.Articles), StringComparison.Ordinal)
            || !string.Equals(Hash(p.WholeArticleGeneration), Hash(old.WholeArticleGeneration), StringComparison.Ordinal)
            || p.Budget.ProjectCeilingMinorUnits > old.Budget.ProjectCeilingMinorUnits || p.Budget.AllocationCeilingMinorUnits > old.Budget.AllocationCeilingMinorUnits
            || p.Budget.ProjectCommittedMinorUnits < old.Budget.ProjectCommittedMinorUnits
            || p.Budget.ProjectCommittedMinorUnits - old.Budget.ProjectCommittedMinorUnits != p.Budget.AllocationCommittedMinorUnits - old.Budget.AllocationCommittedMinorUnits
            || p.MaximumAdditionalReservationMinorUnits > old.MaximumAdditionalReservationMinorUnits - (p.Budget.ProjectCommittedMinorUnits - old.Budget.ProjectCommittedMinorUnits)
            || p.PriorArticleAttemptRows.Count + (p.PriorSuccessorAttemptRows?.Count ?? 0) < old.PriorArticleAttemptRows.Count + (old.PriorSuccessorAttemptRows?.Count ?? 0)
            || p.MaximumNewAttempts > old.MaximumNewAttempts - (p.PriorArticleAttemptRows.Count + (p.PriorSuccessorAttemptRows?.Count ?? 0)
                - old.PriorArticleAttemptRows.Count - (old.PriorSuccessorAttemptRows?.Count ?? 0)))
            throw new InvalidDataException("Completion continuity cannot change source, historical grouping, grant identity or retained financial limits.");
        RequireValid(old, old.ExpiresAtUtc <= now ? old.ExpiresAtUtc.AddTicks(-1) : now);
        RequireCompletionRecoveryBound(p, old);
    }

    private static void RequireCompletionRecoveryBound(ChirpArticleAdmissionPolicy p, ChirpArticleAdmissionPolicy old)
    {
        if (p.SentenceRecovery is null && old.SentenceRecovery is null) return;
        var grant = p.SentenceRecovery; var previous = old.SentenceRecovery;
        long cap = p.Budget.ProjectCeilingMinorUnits;
        if (grant is null || previous is null || grant.GrantId != previous.GrantId || grant.Version != previous.Version
            || !string.Equals(grant.PlannerVersion, previous.PlannerVersion, StringComparison.Ordinal)
            || !string.Equals(Hash(grant.ReviewedHistoricalRejections), Hash(previous.ReviewedHistoricalRejections), StringComparison.Ordinal)
            || grant.MaximumDepth != StructuralRecoveryDepth(p) || grant.MaximumAdditionalMinorUnits != cap
            || grant.MaximumChildAttemptsPerRange != cap || grant.MaximumChildAttemptsPerJob != cap || grant.MaximumChildAttemptsTotal != cap)
            throw new InvalidDataException("Completion recovery must derive its redundant counts and source depth from the retained overall financial provision.");
    }
}
