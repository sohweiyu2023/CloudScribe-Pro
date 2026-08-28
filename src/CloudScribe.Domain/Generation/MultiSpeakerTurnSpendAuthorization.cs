namespace CloudScribe.Domain.Generation;

public sealed record MultiSpeakerTurnSpendAuthorization(
    string SpeakerRole,
    string RouteIdentity,
    string PricingProvenanceId,
    string Currency,
    int Scale,
    long MaximumScaledAmount)
{
    public MultiSpeakerTurnSpendAuthorization Validate()
    {
        ValidateFields(SpeakerRole, RouteIdentity, PricingProvenanceId, Currency, Scale, MaximumScaledAmount);
        return this;
    }

    public void EnsureAuthorized(SpeakerVoiceBinding binding, string currency, int scale, long projectedScaledAmount)
    {
        ArgumentNullException.ThrowIfNull(binding);
        binding.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        if (!string.Equals(SpeakerRole, binding.SpeakerRole, StringComparison.Ordinal) ||
            !string.Equals(RouteIdentity, binding.RouteIdentity, StringComparison.Ordinal) ||
            !string.Equals(PricingProvenanceId, binding.PricingProvenanceId, StringComparison.Ordinal))
            throw new InvalidOperationException("Multi-speaker spend authorization no longer matches the pinned provider route and pricing provenance.");
        if (!string.Equals(Currency, currency, StringComparison.OrdinalIgnoreCase) || Scale != scale)
            throw new InvalidOperationException("Projected multi-speaker spend uses a different provider-billed currency or scale.");
        if (projectedScaledAmount < 0 || projectedScaledAmount > MaximumScaledAmount)
            throw new InvalidOperationException("Projected multi-speaker turn spend exceeds its explicit authorization ceiling.");
    }

    private static void ValidateFields(
        string speakerRole,
        string routeIdentity,
        string pricingProvenanceId,
        string currency,
        int scale,
        long maximumScaledAmount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(speakerRole);
        ArgumentException.ThrowIfNullOrWhiteSpace(routeIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(pricingProvenanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        if (scale is < 0 or > 9) throw new ArgumentOutOfRangeException(nameof(scale));
        ArgumentOutOfRangeException.ThrowIfNegative(maximumScaledAmount);
    }
}
