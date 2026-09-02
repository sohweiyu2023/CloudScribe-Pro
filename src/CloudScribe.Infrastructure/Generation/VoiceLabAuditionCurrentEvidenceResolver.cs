using CloudScribe.Application.Generation;

namespace CloudScribe.Infrastructure.Generation;

public sealed class VoiceLabAuditionCurrentEvidenceResolver(
    IVoiceLabAuditionAuthorizationStore auditionAuthorizations,
    IVoiceLabProjectAuthorizationStore projectAuthorizations,
    TimeProvider timeProvider)
{
    public async Task<VoiceLabAuditionAuthorizationEvidence?> ResolveAsync(
        VoiceLabAuditionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Selection.Validate();

        VoiceLabAuditionPersistedAuthorization? persisted = await auditionAuthorizations
            .LoadCurrentAsync(request.Selection, cancellationToken)
            .ConfigureAwait(false);
        if (persisted is null)
            return null;

        VoiceLabProjectAuthorizationEvidence? project = await projectAuthorizations
            .LoadCurrentAsync(
                request.Selection.ProviderStableId,
                request.Selection.AccountStableId,
                request.Selection.ProjectStableId,
                cancellationToken)
            .ConfigureAwait(false);
        if (project is null)
            throw new InvalidOperationException("Voice Lab audition project authorization is unavailable.");

        DateTimeOffset nowUtc = timeProvider.GetUtcNow().ToUniversalTime();
        if (!project.IsCurrent(nowUtc))
            throw new InvalidOperationException("Voice Lab audition project authorization is no longer current.");
        if (project.AccountRevision != persisted.AccountRevision)
            throw new InvalidOperationException("Voice Lab audition project/account revision changed after spend authorization.");
        if (!string.Equals(project.CredentialReferenceId, persisted.CredentialReferenceId, StringComparison.Ordinal))
            throw new InvalidOperationException("Voice Lab audition credential binding changed after spend authorization.");
        if (!string.Equals(project.CapabilityEvidenceId, request.Selection.CapabilityEvidenceId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Voice Lab audition capability binding changed after spend authorization.");

        return persisted.ToCurrentEvidence(nowUtc);
    }
}
