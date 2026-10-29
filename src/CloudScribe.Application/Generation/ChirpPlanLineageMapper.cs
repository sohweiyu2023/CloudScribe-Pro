namespace CloudScribe.Application.Generation;

/// <summary>Maps every overlap, not merely the newest attempt or old ordinal. This structural check does not establish authority.</summary>
public static class ChirpPlanLineageMapper
{
    public static ChirpPlanLineage Map(string source, IReadOnlyList<ChirpNarrationRange> proposed,
        IReadOnlyList<ChirpHistoricalRange> independentlyResolvedHistory)
    {
        ArgumentNullException.ThrowIfNull(independentlyResolvedHistory);
        if (independentlyResolvedHistory.Count is < 1 or > ChirpNarrationPlanner.MaximumCoverageParts)
            throw new InvalidOperationException("Historical article coverage is absent or exceeds its bound.");
        _ = ChirpNarrationPlanner.SliceAll(source, proposed);
        _ = ChirpNarrationPlanner.SliceAll(source, independentlyResolvedHistory.Select(x =>
            new ChirpNarrationRange(x.StartScalar, x.EndScalarExclusive, x.TextSha256, null)).ToArray());
        RequireHistory(independentlyResolvedHistory);
        var history = independentlyResolvedHistory.Select(x => x with { Attempts = Array.AsReadOnly(x.Attempts.ToArray()) }).ToArray();
        List<ChirpRangeLineage> mapped = [];
        int cursor = 0;
        for (int i = 0; i < proposed.Count; i++)
        {
            var range = proposed[i];
            while (cursor < history.Length && history[cursor].EndScalarExclusive <= range.StartScalar) cursor++;
            List<int> overlapping = [];
            for (int j = cursor; j < history.Length && history[j].StartScalar < range.EndScalarExclusive; j++) overlapping.Add(j);
            if (overlapping.Count == 0) throw new InvalidOperationException("Proposed coverage has no verified historical source association.");
            mapped.Add(new(i + 1, overlapping.AsReadOnly()));
        }
        return new(Array.AsReadOnly(history), mapped.AsReadOnly());
    }

    /// <summary>Admission must compare against a separately obtained complete history; caller assertions are insufficient.</summary>
    public static void RequireExact(ChirpPlanLineage admitted, ChirpPlanLineage current)
    {
        ArgumentNullException.ThrowIfNull(admitted);
        ArgumentNullException.ThrowIfNull(current);
        if (admitted.Ranges.Count != current.Ranges.Count || admitted.Ranges.Count is < 1 or > ChirpNarrationPlanner.MaximumCoverageParts
            || admitted.History.Count != current.History.Count || admitted.History.Count is < 1 or > ChirpNarrationPlanner.MaximumCoverageParts)
            throw new InvalidOperationException("Historical coverage lineage changed.");
        for (int i = 0; i < current.History.Count; i++)
        {
            var a = admitted.History[i]; var c = current.History[i];
            if (a.DocumentId != c.DocumentId || a.RevisionId != c.RevisionId || a.StartScalar != c.StartScalar
                || a.EndScalarExclusive != c.EndScalarExclusive || !string.Equals(a.TextSha256, c.TextSha256, StringComparison.Ordinal)
                || !a.Attempts.SequenceEqual(c.Attempts))
                throw new InvalidOperationException("Historical native revision, coverage, attempts or reservations changed.");
        }
        for (int i = 0; i < current.Ranges.Count; i++)
            if (admitted.Ranges[i].CoverageOrdinal != current.Ranges[i].CoverageOrdinal
                || !admitted.Ranges[i].HistoricalRangeIndexes.SequenceEqual(current.Ranges[i].HistoricalRangeIndexes))
                throw new InvalidOperationException("Historical attempts or retained reservations changed; no revised request was authorized.");
    }

    private static void RequireHistory(IReadOnlyList<ChirpHistoricalRange> history)
    {
        if (history.Any(x => x.DocumentId == Guid.Empty || x.RevisionId == Guid.Empty || x.Attempts.Count > 10_000)
            || history.Select(x => x.DocumentId).Distinct().Count() != history.Count)
            throw new InvalidOperationException("Historical native document/revision identity is absent or ambiguous.");
        HashSet<string> keys = new(StringComparer.Ordinal);
        int count = 0;
        foreach (var attempt in history.SelectMany(x => x.Attempts))
        {
            if (++count > 10_000 || string.IsNullOrWhiteSpace(attempt.IdempotencyKey) || attempt.IdempotencyKey.Length > 512
                || !keys.Add(attempt.IdempotencyKey) || attempt.RetainedReservationMinorUnits < 0
                || attempt.StateSha256 is not { Length: 64 } || !attempt.StateSha256.All(x => x is >= '0' and <= '9' or >= 'a' and <= 'f'))
                throw new InvalidOperationException("Historical attempt identity, state or retained reservation is invalid or ambiguous.");
        }
    }
}
