namespace CloudScribe.Infrastructure.Generation;

public static class ChirpReservationResumeValidation
{
    public static void RequireValid(ChirpReservationResumePolicy value, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.SchemaVersion != 1 || value.MappingId == Guid.Empty || value.AuthorizationId == Guid.Empty
            || value.CatalogSnapshotId == Guid.Empty || value.ExpiresAtUtc.Offset != TimeSpan.Zero || value.CatalogExpiresAtUtc.Offset != TimeSpan.Zero
            || value.OriginalApprovedAtUtc.Offset != TimeSpan.Zero || value.OriginalApprovedAtUtc > now
            || value.ExpiresAtUtc <= now || value.CatalogExpiresAtUtc <= now || value.ExpiresAtUtc > value.CatalogExpiresAtUtc
            || value.MaximumExistingDispatches != 1 || value.MaximumNewReservations != 0 || value.MaximumAdditionalReservationMinorUnits != 0
            || value.PriorMappingSha256 is not null && !ChirpPolicyValidation.IsHash(value.PriorMappingSha256)
            || value.HistoricalPolicy is null || value.OriginalEnvelope is null || value.ExecutionEnvelope is null
            || new[] { value.AppSha256, value.InfrastructureSha256, value.ProfileRootSha256, value.SourceApprovalSha256,
                value.OriginalAttemptRowSha256, value.CleanQueueRowSha256, value.FinancialSha256, value.ProviderEvidenceSha256,
                value.SelectionSha256, value.RuntimeEvidenceSha256, value.RequestIdentitySha256 }.Any(x => !ChirpPolicyValidation.IsHash(x)))
            throw new InvalidDataException("Exact independently admitted resume scope is absent, invalid or expired.");
        ChirpPolicyValidation.RequireValid(value.HistoricalPolicy, value.OriginalApprovedAtUtc);
        var original = value.OriginalEnvelope;
        var execution = value.ExecutionEnvelope;
        if (original.ChirpAuthorizationId != value.AuthorizationId || original.RecoveryAuthorizationId is not null
            || execution.ChirpAuthorizationId != value.AuthorizationId || execution.RecoveryAuthorizationId is not null
            || (execution with { RequestRevision = original.RequestRevision }) != original
            || original.RequestRevision < 0 || execution.RequestRevision < 0 || original.CompiledPayloadBytes is < 1 or > 5000
            || !ChirpPolicyValidation.IsHash(original.CompiledPayloadSha256)
            || string.Equals(value.SourceApprovalSha256, value.HistoricalPolicy.SourceApprovalSha256, StringComparison.Ordinal)
            || !string.Equals(value.ProfileRootSha256, value.HistoricalPolicy.ProfileRootSha256, StringComparison.Ordinal)
            || !string.Equals(original.AudioEncoding, "MP3", StringComparison.Ordinal)
            || !string.Equals(original.AccountId, value.HistoricalPolicy.AccountId, StringComparison.Ordinal))
            throw new InvalidDataException("Resume authority cannot change the immutable account, payload or non-catalog request fields.");
        if (!value.HistoricalPolicy.Articles.Any(article => string.Equals(article.Proposal.VoiceName, original.VoiceName, StringComparison.Ordinal)
            && article.Proposal.Parts.Any(part => string.Equals(part.PayloadSha256, original.CompiledPayloadSha256, StringComparison.Ordinal)
                && part.PayloadBytes == original.CompiledPayloadBytes && article.AllowedSegments.Any(allowed => allowed.CoverageOrdinal == part.CoverageOrdinal))))
            throw new InvalidDataException("The retained request is outside the original exact article scope.");
    }
}
