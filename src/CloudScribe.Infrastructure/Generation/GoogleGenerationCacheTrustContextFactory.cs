using CloudScribe.Domain.Generation;

namespace CloudScribe.Infrastructure.Generation;

public static class GoogleGenerationCacheTrustContextFactory
{
    public static GenerationCacheTrustContext Create(
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
        account.Validate();
        capability.Validate(nowUtc);
        compilationOptions.Validate();

        if (capability.IsStale(nowUtc))
        {
            throw new InvalidOperationException("Stale Google capability evidence cannot authorize cache reuse.");
        }
        if (!string.Equals(account.AccountId, capability.AccountId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Google account and capability identities must match for cache reuse.");
        }
        capability.RequireSupported(
            compilationOptions.VoiceName,
            compilationOptions.AudioEncoding,
            compiledPayloadBytes: 256,
            nowUtc);

        return new GenerationCacheTrustContext(
            ProviderStableId: "google-cloud-text-to-speech",
            AccountId: account.AccountId,
            ProjectId: Require(projectId, nameof(projectId)),
            EndpointId: account.Endpoint.GetLeftPart(UriPartial.Authority),
            RegionId: account.Region,
            OperationStableId: "synthesize-speech",
            ResolvedModelId: Require(resolvedModelId, nameof(resolvedModelId)),
            VoiceStableId: compilationOptions.VoiceName,
            VoiceFingerprint: Require(voiceFingerprint, nameof(voiceFingerprint)),
            SpeechPlanIdentity: Require(speechPlanIdentity, nameof(speechPlanIdentity)),
            LanguageTag: compilationOptions.LanguageCode,
            SynthesisControlsIdentity: Require(synthesisControlsIdentity, nameof(synthesisControlsIdentity)),
            OutputFormat: compilationOptions.AudioEncoding,
            SampleFormatIdentity: Require(sampleFormatIdentity, nameof(sampleFormatIdentity)),
            AdapterVersion: Require(adapterVersion, nameof(adapterVersion)),
            CompilerVersion: Require(compilerVersion, nameof(compilerVersion)),
            AstVersion: Require(astVersion, nameof(astVersion)),
            NormalizationVersion: Require(normalizationVersion, nameof(normalizationVersion)),
            PricingIdentity: Require(pricingIdentity, nameof(pricingIdentity)),
            CapabilityIdentity: capability.ProvenanceId,
            GovernancePolicyIdentity: Require(governancePolicyIdentity, nameof(governancePolicyIdentity)),
            ProviderFeatureIdentity: Require(providerFeatureIdentity, nameof(providerFeatureIdentity)),
            AccountCapabilityIdentity: Require(accountCapabilityIdentity, nameof(accountCapabilityIdentity)))
            .Validate();
    }

    private static string Require(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value;
    }
}
