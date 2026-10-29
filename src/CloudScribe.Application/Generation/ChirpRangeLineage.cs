namespace CloudScribe.Application.Generation;

public sealed record ChirpRangeLineage(int CoverageOrdinal, IReadOnlyList<int> HistoricalRangeIndexes);
