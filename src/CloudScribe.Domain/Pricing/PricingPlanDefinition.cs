namespace CloudScribe.Domain.Pricing;

public sealed record PricingPlanDefinition
{
    public PricingPlanDefinition(
        string stableId,
        IReadOnlyList<string> meterStableIds,
        string provenanceId)
    {
        StableId = PricingMeterDefinition.NormalizeStableToken(stableId, nameof(stableId));
        ArgumentNullException.ThrowIfNull(meterStableIds);
        if (meterStableIds.Count == 0)
        {
            throw new ArgumentException("A pricing plan requires at least one meter reference.", nameof(meterStableIds));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var copiedMeterStableIds = new string[meterStableIds.Count];
        for (int index = 0; index < meterStableIds.Count; index++)
        {
            string meterStableId = PricingMeterDefinition.NormalizeStableToken(
                meterStableIds[index],
                nameof(meterStableIds));
            if (!seen.Add(meterStableId))
            {
                throw new ArgumentException(
                    "Pricing plan meter references must be unique.",
                    nameof(meterStableIds));
            }

            copiedMeterStableIds[index] = meterStableId;
        }

        MeterStableIds = Array.AsReadOnly(copiedMeterStableIds);
        ProvenanceId = NormalizeProvenance(provenanceId);
    }

    public string StableId { get; }
    public IReadOnlyList<string> MeterStableIds { get; }
    public string ProvenanceId { get; }

    private static string NormalizeProvenance(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string normalized = value.Trim();
        if (normalized.Length > 160
            || normalized.Any(static character =>
                char.IsControl(character)
                || char.IsSurrogate(character)
                || char.GetUnicodeCategory(character) == System.Globalization.UnicodeCategory.Format))
        {
            throw new ArgumentException(
                "Pricing plan provenance is limited to 160 visible characters.",
                nameof(value));
        }

        return normalized;
    }
}
