using CloudScribe.Domain.Generation;

namespace CloudScribe.Infrastructure.Generation;

public static class GoogleGenerationAdmissionPolicy
{
    public static GenerationCacheTrustContext Admit(
        GoogleGenerationAccount account,
        GoogleCapabilitySnapshot capability,
        GoogleSpeechCompilationOptions compilationOptions,
        string projectId,
        string resolvedModelId,
        string voiceFingerprint,
        string speechPlanIdentity,
        string synthesisControlsIdentity,
        string sampleFormatIdentity,
        string adapterVersion,
        string compilerVersion,
        string astVersion,
        string normalizationVersion,
        string pricingIdentity,
        string governancePolicyIdentity,
        string providerFeatureIdentity,
        string accountCapabilityIdentity,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentNullException.ThrowIfNull(compilationOptions);

        var trust = GoogleGenerationCacheTrustContextFactory.Create(
            account, capability, compilationOptions, projectId, resolvedModelId,
            voiceFingerprint, speechPlanIdentity, synthesisControlsIdentity,
            sampleFormatIdentity, adapterVersion, compilerVersion, astVersion,
            normalizationVersion, pricingIdentity, governancePolicyIdentity,
            providerFeatureIdentity, accountCapabilityIdentity, nowUtc);

        if (!string.Equals(trust.AccountId, account.AccountId, StringComparison.Ordinal) ||
            !string.Equals(trust.VoiceStableId, compilationOptions.VoiceName, StringComparison.Ordinal) ||
            !string.Equals(trust.OutputFormat, compilationOptions.AudioEncoding, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Google generation admission produced a trust context that does not match the authorized account/voice/output binding.");
        }

        return trust;
    }
}
