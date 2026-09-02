namespace CloudScribe.Providers.Abstractions;

public sealed record VoiceLabProviderAuditionRequest(
    string ProviderStableId,
    string AccountStableId,
    string ProjectStableId,
    string VoiceStableId,
    string VoiceFingerprint,
    string CapabilityEvidenceId,
    string CredentialReferenceId,
    string PricingEvidenceId,
    string SpendAuthorizationId,
    long AccountRevision,
    string OutputFormat,
    bool ForceFresh)
{
    public VoiceLabProviderAuditionRequest Validate()
    {
        RequireCanonical(ProviderStableId, nameof(ProviderStableId));
        RequireCanonical(AccountStableId, nameof(AccountStableId));
        RequireCanonical(ProjectStableId, nameof(ProjectStableId));
        RequireCanonical(VoiceStableId, nameof(VoiceStableId));
        RequireCanonical(VoiceFingerprint, nameof(VoiceFingerprint));
        RequireCanonical(CapabilityEvidenceId, nameof(CapabilityEvidenceId));
        RequireCanonical(CredentialReferenceId, nameof(CredentialReferenceId));
        RequireCanonical(PricingEvidenceId, nameof(PricingEvidenceId));
        RequireCanonical(SpendAuthorizationId, nameof(SpendAuthorizationId));
        RequireCanonical(OutputFormat, nameof(OutputFormat));
        if (AccountRevision < 1)
            throw new InvalidOperationException("Voice Lab provider audition request requires a positive account revision.");
        return this;
    }

    private static void RequireCanonical(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
            throw new InvalidOperationException($"Voice Lab provider audition '{parameterName}' must be canonical.");
        if (value.Contains('\r') || value.Contains('\n') || value.Contains('\0'))
            throw new InvalidOperationException($"Voice Lab provider audition '{parameterName}' contains forbidden control characters.");
    }
}
