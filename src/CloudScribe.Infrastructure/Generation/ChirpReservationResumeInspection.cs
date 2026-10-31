namespace CloudScribe.Infrastructure.Generation;

/// <summary>Local review evidence only; neither inspection nor its hashes admit a policy.</summary>
public sealed record ChirpReservationResumeInspection(Guid AuthorizationId, string OriginalAttemptRowSha256,
    string CleanQueueRowSha256, string FinancialSha256, string ProviderEvidenceSha256,
    GoogleGenerationSubmissionEnvelope OriginalEnvelope, DateTimeOffset OriginalApprovedAtUtc,
    long ReservedMinorUnits, long ProjectCommittedMinorUnits)
{
    public bool GrantsAuthority { get; }
}
