namespace CloudScribe.Infrastructure.Generation;

public static partial class ChirpPolicyValidation
{
    private static void RequireSentenceRecovery(ChirpArticleAdmissionPolicy policy)
    {
        if (policy.SentenceRecovery is not { } grant) return;
        bool structural = HasStructuralRecoveryGrant(policy);
        if (policy.SchemaVersion != 3 || policy.SuccessorGrant is not null || policy.WholeArticleGeneration is null
            || grant.Version != 1 || grant.GrantId == Guid.Empty
            || !string.Equals(grant.PlannerVersion, ChirpBoundedSentenceSplit.Version, StringComparison.Ordinal)
            || grant.MaximumDepth < 1 || grant.MaximumChildAttemptsPerRange < 2
            || grant.MaximumChildAttemptsPerJob < 2 || grant.MaximumChildAttemptsTotal < 2
            || grant.MaximumChildAttemptsPerRange > grant.MaximumChildAttemptsPerJob
            || grant.MaximumChildAttemptsPerJob > grant.MaximumChildAttemptsTotal
            || grant.MaximumAdditionalMinorUnits < 2
            || !structural && (grant.MaximumDepth > 2 || grant.MaximumChildAttemptsPerRange > 6 || grant.MaximumChildAttemptsPerJob > 32
                || grant.MaximumChildAttemptsTotal > 64 || grant.MaximumChildAttemptsTotal > policy.MaximumNewAttempts
                || grant.MaximumAdditionalMinorUnits > 64 || grant.MaximumAdditionalMinorUnits > policy.MaximumAdditionalReservationMinorUnits)
            || structural && (grant.MaximumDepth != StructuralRecoveryDepth(policy) || grant.MaximumChildAttemptsPerRange != grant.MaximumAdditionalMinorUnits
                || grant.MaximumChildAttemptsPerJob != grant.MaximumAdditionalMinorUnits || grant.MaximumChildAttemptsTotal != grant.MaximumAdditionalMinorUnits
                || grant.MaximumAdditionalMinorUnits > policy.Budget.ProjectCeilingMinorUnits)
            || grant.ReviewedHistoricalRejections is not { Count: <= 64 }
            || grant.ReviewedHistoricalRejections.Select(x => x.AuthorizationId).Distinct().Count() != grant.ReviewedHistoricalRejections.Count
            || grant.ReviewedHistoricalRejections.Select(x => x.ProtectedRecordId).Distinct().Count() != grant.ReviewedHistoricalRejections.Count
            || policy.Articles.Any(x => x.Proposal.InputFormat is not { Version: 1 }))
            throw new InvalidDataException("Bounded sentence recovery requires independently admitted exact whole-article permission.");
        foreach (var proof in grant.ReviewedHistoricalRejections)
            if (proof.AuthorizationId == Guid.Empty || proof.ProtectedRecordId == Guid.Empty || proof.PayloadBytes is < 1 or > 5000
                || new[] { proof.AttemptRowSha256, proof.QueueRowSha256, proof.PayloadSha256,
                    proof.ProtectedRecordSha256, proof.ReviewedEvidenceSha256 }.Any(x => !IsHash(x))
                || !policy.PriorArticleAttemptRows.Concat(policy.PriorSuccessorAttemptRows ?? []).Any(x => string.Equals(x.Sha256, proof.AttemptRowSha256, StringComparison.Ordinal))
                || !policy.Budget.QueueRows.Any(x => string.Equals(x.Sha256, proof.QueueRowSha256, StringComparison.Ordinal)))
                throw new InvalidDataException("Historical sentence rejection requires its separately reviewed exact protected-evidence pins.");
    }
}
