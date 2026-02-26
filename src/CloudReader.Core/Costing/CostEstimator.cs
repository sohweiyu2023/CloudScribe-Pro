using CloudReader.Core.Models;

namespace CloudReader.Core.Costing;

public sealed class CostEstimator
{
    public decimal Estimate(string tier, int newCharacters, int monthlyCharacters)
    {
        var (free, rate) = tier switch
        {
            "Standard" or "WaveNet" => (4_000_000, UsageRateCard.StandardWaveNetPerMillion),
            "Neural2" => (1_000_000, UsageRateCard.Neural2PerMillion),
            "Chirp3-HD" => (1_000_000, UsageRateCard.ChirpHdPerMillion),
            "Studio" => (1_000_000, UsageRateCard.StudioPerMillion),
            "InstantCustom" => (0, UsageRateCard.InstantCustomPerMillion),
            _ => (0, UsageRateCard.StandardWaveNetPerMillion)
        };

        var billable = Math.Max(0, monthlyCharacters + newCharacters - free);
        var billableBefore = Math.Max(0, monthlyCharacters - free);
        var delta = billable - billableBefore;
        return (delta / 1_000_000m) * rate;
    }
}
