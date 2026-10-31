using CloudScribe.Application.Providers;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class GoogleTextToSpeechCatalogBootstrapService
{
    private async Task<GoogleTextToSpeechCatalogBootstrapResult?> RetainCurrentResumeEvidenceAsync(
        ProviderAccountSnapshot account, GoogleServiceAccountCredentialIdentity identity, GoogleVoiceCatalogSnapshot observed,
        GoogleSynthesisCapabilityEvidence probe,
        CancellationToken token)
    {
        // Reopening Create MP3 still performs real authenticated catalog/probe
        // verification. Do NOT silently replace an unexpired independently admitted
        // snapshot merely because the UI reopened. Never extend its old lifetime.
        if (completionGate?.IsRequired != true || resumeAdmission is null) return null;
        var policy = await resumeAdmission.LoadVerifiedAsync(token).ConfigureAwait(false);
        var current = await _capabilities.GetLatestAsync(GoogleGenerationProvider.StableProviderId, account.Reference.AccountId, token).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        if (policy is null || current is null || current.IsStale(now) || policy.ExpiresAtUtc <= now
            || policy.CatalogExpiresAtUtc <= now || policy.CatalogSnapshotId != current.Id
            || policy.CatalogExpiresAtUtc != current.ExpiresAtUtc) return null;
        string observedProvenance = BuildCombinedCapabilityProvenance(observed.ProvenanceId, probe.ProvenanceId);
        if (!string.Equals(current.Snapshot.ProvenanceId, observedProvenance, StringComparison.Ordinal)
            || policy.ExecutionEnvelope is null
            || !string.Equals(policy.ExecutionEnvelope.CapabilityProvenanceId, observedProvenance, StringComparison.Ordinal)) return null;
        new GoogleGenerationProductionEvidence(account, current).Validate(now);
        return new(account, current, identity.ProjectId, observed.ProvenanceId, observed.Voices, current.ExpiresAtUtc);
    }
}
