namespace CloudScribe.Infrastructure.Generation;

public static partial class ChirpPolicyValidation
{
    private static void RequireRecoveryContinuation(ChirpArticleAdmissionPolicy p, DateTimeOffset now)
    {
        if (p.RecoveryContinuation is not { } c)
        {
            if (p.RecoveryLimitRevision is not null) throw new InvalidDataException("Recovery limit revision requires its exact stopped-job continuation.");
            return;
        }
        RequireContinuationDepth(p);
        var old = c.PreviousPolicy;
        var increase = RecoveryLimitIncrease(p, old);
        if (c.Version != 1 || c.JobId == Guid.Empty || c.FailedAuthorizationId == Guid.Empty || !IsHash(c.PreviousJobSha256)
            || old is null || p.SchemaVersion != 3 || old.SchemaVersion != 3
            || old.RecoveryContinuation is { } prior && prior.JobId != c.JobId
            || p.SentenceRecovery is null
            || !string.Equals(Hash(p.Articles), Hash(old.Articles), StringComparison.Ordinal)
            || !string.Equals(Hash(p.WholeArticleGeneration), Hash(old.WholeArticleGeneration), StringComparison.Ordinal)
            || !string.Equals(p.ProfileRootSha256, old.ProfileRootSha256, StringComparison.Ordinal)
            || !string.Equals(p.AccountId, old.AccountId, StringComparison.Ordinal) || !string.Equals(p.ProjectId, old.ProjectId, StringComparison.Ordinal)
            || string.Equals(p.SourceApprovalSha256, old.SourceApprovalSha256, StringComparison.Ordinal)
            || p.ExpiresAtUtc != old.ExpiresAtUtc || p.Budget.ProjectCeilingMinorUnits > old.Budget.ProjectCeilingMinorUnits + increase.Cost
            || p.Budget.AllocationCeilingMinorUnits > old.Budget.AllocationCeilingMinorUnits + increase.Cost
            || p.Budget.ProjectCommittedMinorUnits < old.Budget.ProjectCommittedMinorUnits
            || p.Budget.AllocationCommittedMinorUnits - old.Budget.AllocationCommittedMinorUnits
                != p.Budget.ProjectCommittedMinorUnits - old.Budget.ProjectCommittedMinorUnits
            || p.RecoveryLimitRevision is null && p.MaximumNewAttempts > old.MaximumNewAttempts
                - (p.PriorArticleAttemptRows.Count + (p.PriorSuccessorAttemptRows?.Count ?? 0)
                    - old.PriorArticleAttemptRows.Count - (old.PriorSuccessorAttemptRows?.Count ?? 0))
            || p.RecoveryLimitRevision is not null && p.MaximumNewAttempts != p.MaximumAdditionalReservationMinorUnits
            || p.PriorArticleAttemptRows.Count + (p.PriorSuccessorAttemptRows?.Count ?? 0)
                < old.PriorArticleAttemptRows.Count + (old.PriorSuccessorAttemptRows?.Count ?? 0)
            || p.MaximumAdditionalReservationMinorUnits > old.MaximumAdditionalReservationMinorUnits + increase.Cost
                - (p.Budget.ProjectCommittedMinorUnits - old.Budget.ProjectCommittedMinorUnits))
            throw new InvalidDataException("A repair continuation must retain the exact existing family, deadline and remaining allowance.");
        RequireValid(old, IsCompletionBound(p) && old.ExpiresAtUtc <= now ? old.ExpiresAtUtc.AddTicks(-1) : now);
    }

    private static void RequireContinuationDepth(ChirpArticleAdmissionPolicy policy)
    {
        HashSet<string> approvals = new(StringComparer.Ordinal);
        for (var current = policy; ;)
        {
            if (!approvals.Add(current.SourceApprovalSha256))
                throw new InvalidDataException("A repair continuation cannot reuse an earlier source approval.");
            if (current.RecoveryContinuation is not null && current.CompletionContinuity is not null)
                throw new InvalidDataException("A policy can contain only one typed continuity purpose.");
            if (PreviousContinuityPolicy(current) is not { } previous) return;

            current = previous;
        }
    }
}
