namespace CloudScribe.Application.Generation;

/// <summary>Global immutable history once, with bounded indexed overlaps. Retained holds are never summed per overlap.</summary>
public sealed record ChirpPlanLineage(IReadOnlyList<ChirpHistoricalRange> History, IReadOnlyList<ChirpRangeLineage> Ranges);
