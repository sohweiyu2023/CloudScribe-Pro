namespace CloudScribe.Infrastructure.Generation;

/// <summary>Content-free cost presentation. A review is not financial admission, compatibility evidence or permission to submit.</summary>
public sealed record GoogleLongAudioCostReview(string RequestBindingSha256, string VoiceName, string LanguageCode,
    int InputUtf8Bytes, int WireBytes, int ConservativeCharacterUnits, long EstimatedSynthesisUsdCents,
    long RetainedCommitmentsUsdCents, long TaxFxProvisionUsdCents, long TotalCapUsdCents,
    long ProjectedProvisionUsdCents, string PricingProvenance)
{
    public bool WithinProvidedCap => ProjectedProvisionUsdCents <= TotalCapUsdCents;
    public bool IsAlreadyCommitted { get; init; }
    public long AdditionalCommitmentUsdCents { get; init; }

    public static GoogleLongAudioCostReview Prepare(GoogleLongAudioRequest request, decimal usdPerMillionCharacters,
        long retainedCommitmentsUsdCents, long taxFxProvisionUsdCents, long totalCapUsdCents, string pricingProvenance)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(pricingProvenance);
        ArgumentOutOfRangeException.ThrowIfNegative(usdPerMillionCharacters);
        ArgumentOutOfRangeException.ThrowIfNegative(retainedCommitmentsUsdCents);
        ArgumentOutOfRangeException.ThrowIfNegative(taxFxProvisionUsdCents);
        ArgumentOutOfRangeException.ThrowIfNegative(totalCapUsdCents);
        // Conservative local cent provision; this is not a Google minimum charge or invoice prediction.
        long estimate = checked((long)decimal.Ceiling(request.ConservativeCharacterUnits * usdPerMillionCharacters / 10_000m));
        long total = checked(retainedCommitmentsUsdCents + taxFxProvisionUsdCents + estimate);
        return new(request.BindingSha256, request.VoiceName, request.LanguageCode, request.InputUtf8Bytes, request.PayloadBytes,
            request.ConservativeCharacterUnits, estimate, retainedCommitmentsUsdCents, taxFxProvisionUsdCents, totalCapUsdCents,
            total, pricingProvenance)
        { AdditionalCommitmentUsdCents = estimate };
    }
}
