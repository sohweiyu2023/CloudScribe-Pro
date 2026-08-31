using CloudScribe.Application.Generation;

namespace CloudScribe.Infrastructure.Generation;

public sealed record VoiceLabCatalogAuthorizationEvidence(
    string ProviderId,
    string AccountId,
    string ProjectId,
    long AccountRevision,
    string CredentialReferenceId,
    string CapabilityEvidenceId,
    bool ProjectAuthorized,
    bool PrivateVoiceAccessAuthorized)
{
    public VoiceLabCatalogAuthorizationEvidence Validate(VoiceLabCatalogQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!string.Equals(ProviderId, query.ProviderId, StringComparison.Ordinal) ||
            !string.Equals(AccountId, query.AccountId, StringComparison.Ordinal) ||
            !string.Equals(ProjectId, query.ProjectId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Voice Lab catalog authorization evidence is bound to a different provider/account/project request.");
        }
        if (AccountRevision < 1)
            throw new InvalidOperationException("Voice Lab catalog authorization evidence requires a persisted account revision.");
        RequireCanonical(CredentialReferenceId, nameof(CredentialReferenceId));
        RequireCanonical(CapabilityEvidenceId, nameof(CapabilityEvidenceId));
        if (!ProjectAuthorized)
            throw new InvalidOperationException("Voice Lab catalog project authorization is no longer current.");
        if (query.IncludePrivateVoices && !PrivateVoiceAccessAuthorized)
            throw new InvalidOperationException("Voice Lab private voice access authorization is no longer current.");
        return this;
    }

    private static void RequireCanonical(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Contains('\r') || value.Contains('\n') || value.Contains('\0'))
        {
            throw new InvalidOperationException($"Voice Lab catalog evidence '{parameterName}' is not canonical.");
        }
    }
}
