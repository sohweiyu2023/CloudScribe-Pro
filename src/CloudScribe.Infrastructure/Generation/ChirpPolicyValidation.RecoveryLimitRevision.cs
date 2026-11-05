namespace CloudScribe.Infrastructure.Generation;

public static partial class ChirpPolicyValidation
{
    private static (int Attempts, long Cost) RecoveryLimitIncrease(ChirpArticleAdmissionPolicy p, ChirpArticleAdmissionPolicy? old)
    {
        if (old?.SentenceRecovery is not { } previous || p.SentenceRecovery is not { } current)
            throw new InvalidDataException("The existing recovery grant must remain present.");
        if (p.RecoveryLimitRevision is not { } revision)
        {
            if (!string.Equals(Hash(previous), Hash(current), StringComparison.Ordinal))
                throw new InvalidDataException("Recovery limits cannot change without explicit reviewed revision.");
            return (0, 0);
        }
        if (revision.Version != 1 || !IsHash(revision.PreviousGrantSha256)
            || !string.Equals(revision.PreviousGrantSha256, Hash(previous), StringComparison.Ordinal)
            || revision.MaximumProjectMinorUnits != p.Budget.ProjectCeilingMinorUnits || revision.MaximumProjectMinorUnits > 917
            || current.GrantId != previous.GrantId || !string.Equals(current.PlannerVersion, previous.PlannerVersion, StringComparison.Ordinal)
            || !string.Equals(Hash(current.ReviewedHistoricalRejections), Hash(previous.ReviewedHistoricalRejections), StringComparison.Ordinal)
            || current.MaximumDepth < previous.MaximumDepth || current.MaximumChildAttemptsPerRange < previous.MaximumChildAttemptsPerRange
            || current.MaximumChildAttemptsPerJob < previous.MaximumChildAttemptsPerJob || current.MaximumChildAttemptsTotal < previous.MaximumChildAttemptsTotal
            || current.MaximumAdditionalMinorUnits < previous.MaximumAdditionalMinorUnits
            || p.Budget.ProjectCeilingMinorUnits - old.Budget.ProjectCeilingMinorUnits
                != p.Budget.AllocationCeilingMinorUnits - old.Budget.AllocationCeilingMinorUnits)
            throw new InvalidDataException("A limit revision must retain the same grant and all used evidence within the reviewed project provision.");
        return (current.MaximumChildAttemptsTotal - previous.MaximumChildAttemptsTotal,
            current.MaximumAdditionalMinorUnits - previous.MaximumAdditionalMinorUnits);
    }

    internal static int StructuralRecoveryDepth(ChirpArticleAdmissionPolicy policy) => checked(policy.Articles
        .SelectMany(x => x.Proposal.Parts).Max(x => x.Range.EndScalarExclusive - x.Range.StartScalar) - 1);

    internal static bool IsReviewedRecoveryGrant(ChirpArticleAdmissionPolicy policy, string hash)
    {
        var current = policy;
        HashSet<string> visited = new(StringComparer.Ordinal);
        while (true)
        {
            if (!visited.Add(current.SourceApprovalSha256)) throw new InvalidDataException("Continuity approval cycle detected.");
            if (current.SentenceRecovery?.GrantId == policy.SentenceRecovery?.GrantId
                && string.Equals(Hash(current.SentenceRecovery), hash, StringComparison.Ordinal)) return true;
            if (PreviousContinuityPolicy(current) is not { } previous) break;
            current = previous;
        }
        return false;
    }

    private static bool HasStructuralRecoveryGrant(ChirpArticleAdmissionPolicy policy)
    {
        var current = policy;
        HashSet<string> visited = new(StringComparer.Ordinal);
        while (true)
        {
            if (!visited.Add(current.SourceApprovalSha256)) throw new InvalidDataException("Continuity approval cycle detected.");
            if (current.RecoveryLimitRevision is not null && string.Equals(Hash(current.SentenceRecovery), Hash(policy.SentenceRecovery), StringComparison.Ordinal)) return true;
            if (current.CompletionContinuity is not null && string.Equals(Hash(current.SentenceRecovery), Hash(policy.SentenceRecovery), StringComparison.Ordinal)) return true;
            if (PreviousContinuityPolicy(current) is not { } previous) break;
            current = previous;
        }
        return false;
    }
}
