namespace CloudScribe.Domain.Generation;

public sealed record GenerationCacheTrustContext(
    string ProviderStableId,
    string AccountId,
    string ProjectId,
    string EndpointId,
    string RegionId,
    string OperationStableId,
    string ResolvedModelId,
    string VoiceStableId,
    string VoiceFingerprint,
    string SpeechPlanIdentity,
    string LanguageTag,
    string SynthesisControlsIdentity,
    string OutputFormat,
    string SampleFormatIdentity,
    string AdapterVersion,
    string CompilerVersion,
    string AstVersion,
    string NormalizationVersion,
    string PricingIdentity,
    string CapabilityIdentity,
    string GovernancePolicyIdentity,
    string ProviderFeatureIdentity,
    string AccountCapabilityIdentity)
{
    public GenerationCacheTrustContext Validate()
    {
        foreach (var value in Values())
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException("Every cache trust-namespace field must be explicit. Use a stable 'none' token when a field is not applicable.");
        }

        return this;
    }

    public IEnumerable<string> Values()
    {
        yield return ProviderStableId;
        yield return AccountId;
        yield return ProjectId;
        yield return EndpointId;
        yield return RegionId;
        yield return OperationStableId;
        yield return ResolvedModelId;
        yield return VoiceStableId;
        yield return VoiceFingerprint;
        yield return SpeechPlanIdentity;
        yield return LanguageTag;
        yield return SynthesisControlsIdentity;
        yield return OutputFormat;
        yield return SampleFormatIdentity;
        yield return AdapterVersion;
        yield return CompilerVersion;
        yield return AstVersion;
        yield return NormalizationVersion;
        yield return PricingIdentity;
        yield return CapabilityIdentity;
        yield return GovernancePolicyIdentity;
        yield return ProviderFeatureIdentity;
        yield return AccountCapabilityIdentity;
    }
}
