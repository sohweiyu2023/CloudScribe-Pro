using CloudScribe.Application.Generation;
using CloudScribe.Application.Providers;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class GoogleTextToSpeechCatalogBootstrapService
{
    private async Task<GoogleTextToSpeechCatalogBootstrapResult> PersistConfiguredEvidenceAsync(
        ProviderAccountSnapshot account, GoogleServiceAccountCredentialIdentity identity, GoogleVoiceCatalogSnapshot observed,
        GoogleSynthesisCapabilityEvidence probe, Guid refreshId, bool retainCurrentEvidence, CancellationToken token)
    {
        if (retainCurrentEvidence) completionGate?.RequireOutsideRun();
        var retained = retainCurrentEvidence
            ? await RetainCurrentOpeningEvidenceAsync(account, identity, observed, probe, token).ConfigureAwait(false) : null;
        // Recheck even when mismatched evidence made retention exit early.
        if (retainCurrentEvidence) completionGate?.RequireOutsideRun();
        return retained ?? await PersistObservedEvidenceAsync(account, identity, observed, probe, refreshId, token).ConfigureAwait(false);
    }

    private async Task<GoogleTextToSpeechCatalogBootstrapResult?> RetainCurrentOpeningEvidenceAsync(
        ProviderAccountSnapshot account, GoogleServiceAccountCredentialIdentity identity,
        GoogleVoiceCatalogSnapshot observed, GoogleSynthesisCapabilityEvidence probe, CancellationToken token)
    {
        var current = await _capabilities.GetLatestAsync(GoogleGenerationProvider.StableProviderId, account.Reference.AccountId, token).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        if (current is null || current.IsStale(now) || current.Snapshot.CapturedAtUtc > now
            || !current.Snapshot.GetCapability(VoiceCatalogCapabilityId).IsUsable
            || !string.Equals(current.Snapshot.ProvenanceId, BuildCombinedCapabilityProvenance(observed.ProvenanceId, probe.ProvenanceId), StringComparison.Ordinal)) return null;
        try { new GoogleGenerationProductionEvidence(account, current).Validate(now); }
        catch (InvalidOperationException) { return null; }

        var query = new VoiceLabCatalogQuery(GoogleGenerationProvider.StableProviderId, account.Reference.AccountId, identity.ProjectId, null, null, false);
        var resolver = new VoiceLabCatalogCurrentEvidenceResolver(_accounts, _capabilities, _projectAuthorizations, _timeProvider);
        var authorization = await resolver.ResolveAsync(query, token).ConfigureAwait(false);
        if (authorization is null || authorization.AccountRevision != account.Revision
            || !string.Equals(authorization.CapabilityEvidenceId, current.Id.ToString("D"), StringComparison.Ordinal)) return null;
        var project = await _projectAuthorizations.LoadCurrentAsync(query.ProviderId, query.AccountId, query.ProjectId, token).ConfigureAwait(false);
        if (project is null || project.CapturedAtUtc != current.Snapshot.CapturedAtUtc || project.ExpiresAtUtc != current.ExpiresAtUtc
            || !string.Equals(project.ProviderId, query.ProviderId, StringComparison.Ordinal)
            || !string.Equals(project.AccountId, query.AccountId, StringComparison.Ordinal)
            || !string.Equals(project.ProjectId, query.ProjectId, StringComparison.Ordinal)) return null;
        foreach (var voice in observed.Voices)
        {
            token.ThrowIfCancellationRequested();
            var evidence = await _generationProjectAuthorizations.LoadCurrentAsync(query.AccountId, query.ProjectId, voice.Name, token).ConfigureAwait(false);
            if (!MatchesOpeningVoice(evidence, account, identity.ProjectId, voice.Name, current, now)) return null;
        }
        // All reads belong to one unchanged current snapshot. Never renew its original validity window.
        token.ThrowIfCancellationRequested();
        completionGate?.RequireOutsideRun();
        var latest = await _capabilities.GetLatestAsync(query.ProviderId, query.AccountId, token).ConfigureAwait(false);
        var latestAccount = await _accounts.FindAsync(query.ProviderId, query.AccountId, token).ConfigureAwait(false);
        if (latest?.Id != current.Id || !Equals(latestAccount, account))
            throw new InvalidOperationException("The Google connection changed while validating retained catalog evidence; no evidence was admitted.");
        if (current.IsStale(_timeProvider.GetUtcNow())) return null;
        if (!Equals(authorization, await resolver.ResolveAsync(query, token).ConfigureAwait(false))) return null;
        return new(account, current, identity.ProjectId, observed.ProvenanceId, observed.Voices, current.ExpiresAtUtc)
        { RetainedCurrentEvidence = true };
    }

    private static bool MatchesOpeningVoice(GoogleGenerationProjectAuthorizationEvidence? evidence,
        ProviderAccountSnapshot account, string projectId, string voiceName, StoredProviderCapabilitySnapshot current, DateTimeOffset now)
    {
        if (evidence is null) return false;
        try { evidence.Validate(now); }
        catch (InvalidOperationException) { return false; }
        return evidence.IsCurrent(now) && evidence.CapturedAtUtc == current.Snapshot.CapturedAtUtc && evidence.ExpiresAtUtc == current.ExpiresAtUtc
            && string.Equals(evidence.AccountId, account.Reference.AccountId, StringComparison.Ordinal)
            && string.Equals(evidence.ProjectId, projectId, StringComparison.Ordinal)
            && string.Equals(evidence.ModelId, voiceName, StringComparison.Ordinal)
            && string.Equals(evidence.CredentialReferenceId, account.Reference.CredentialReference?.TargetName, StringComparison.Ordinal)
            && string.Equals(evidence.CapabilityProvenanceId, current.Snapshot.ProvenanceId, StringComparison.Ordinal)
            && string.Equals(evidence.EndpointId, account.Reference.EndpointId, StringComparison.Ordinal)
            && string.Equals(evidence.RegionId, account.Reference.RegionId, StringComparison.Ordinal)
            && string.Equals(evidence.EndpointOrigin, account.Reference.EndpointOrigin?.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal);
    }
}
