namespace CloudScribe.Infrastructure.Generation;

/// <summary>
/// Reconstructs only the exact Google capability subset that was already bound into a durable
/// spend-approved submission envelope, and only while the current persisted capability snapshot
/// still has the same provenance. This is deliberately narrower than a provider catalog snapshot:
/// it cannot introduce a new voice, encoding, account, credential or payload identity.
/// </summary>
public static class GoogleGenerationApprovedCapabilityProjection
{
    public static GoogleCapabilitySnapshot Create(
        GoogleGenerationProductionEvidence productionEvidence,
        GoogleGenerationSpendAuthorization spendAuthorization,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(productionEvidence);
        ArgumentNullException.ThrowIfNull(spendAuthorization);

        GoogleGenerationProductionEvidence current = productionEvidence.Validate(nowUtc);
        GoogleGenerationSubmissionEnvelope envelope = spendAuthorization.Envelope
            ?? throw new InvalidOperationException("Durable Google spend authorization has no submission envelope.");

        var account = current.Account.Reference;
        var capability = current.Capability;
        string credentialReferenceId = account.CredentialReference?.TargetName
            ?? throw new InvalidOperationException("Current Google provider account has no credential reference.");

        if (!string.Equals(envelope.AccountId, account.AccountId, StringComparison.Ordinal))
            throw new InvalidOperationException("Approved Google submission account differs from the current persisted account.");
        if (!string.Equals(envelope.CredentialReferenceId, credentialReferenceId, StringComparison.Ordinal))
            throw new InvalidOperationException("Approved Google submission credential differs from the current persisted credential binding.");
        if (!string.Equals(
                envelope.CapabilityProvenanceId,
                capability.Snapshot.ProvenanceId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Approved Google capability provenance differs from the current persisted capability evidence.");
        }

        // GoogleCapabilitySnapshot enforces a provider-safety floor of 256 bytes. The durable
        // envelope still pins the exact payload byte count and SHA-256 separately, so this floor
        // cannot authorize a different payload or widen the approved submission identity.
        int boundedPayloadLimit = Math.Max(256, envelope.CompiledPayloadBytes);

        return new GoogleCapabilitySnapshot(
            account.AccountId,
            capability.Snapshot.ProvenanceId,
            capability.Snapshot.CapturedAtUtc,
            capability.ExpiresAtUtc,
            new HashSet<string>(StringComparer.Ordinal) { envelope.VoiceName },
            new HashSet<string>(StringComparer.Ordinal) { envelope.AudioEncoding },
            boundedPayloadLimit).Validate(nowUtc);
    }
}
