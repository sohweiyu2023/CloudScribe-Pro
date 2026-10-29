using CloudScribe.Application.Generation;

namespace CloudScribe.Application.Tests;

public sealed class ChirpPlanLineageMapperTests
{
    [Fact]
    public void RangeCrossingOldBoundaryRetainsAllRootsAndEveryRecoveryChild()
    {
        string source = string.Concat(Enumerable.Repeat("文，", 400));
        var history = History(source);
        var proposed = ChirpNarrationPlanner.Plan(source);
        var mapped = ChirpPlanLineageMapper.Map(source, proposed, history);
        var crossing = Assert.Single(mapped.Ranges, x => x.HistoricalRangeIndexes.Count == 2);
        Assert.Equal(3, crossing.HistoricalRangeIndexes.Sum(x => mapped.History[x].Attempts.Count));
        Assert.Equal(9, mapped.History.SelectMany(x => x.Attempts).Sum(x => x.RetainedReservationMinorUnits));
        ChirpPlanLineageMapper.RequireExact(mapped, ChirpPlanLineageMapper.Map(source, proposed, history));
        var omitted = mapped with { History = mapped.History.Select(x => x with { Attempts = x.Attempts.Take(1).ToArray() }).ToArray() };
        Assert.Throws<InvalidOperationException>(() => ChirpPlanLineageMapper.RequireExact(omitted, mapped));
        var changed = history.Select(x => x with { Attempts = x.Attempts.Select(y => y with { RetainedReservationMinorUnits = 0 }).ToArray() }).ToArray();
        Assert.Throws<InvalidOperationException>(() => ChirpPlanLineageMapper.RequireExact(mapped, ChirpPlanLineageMapper.Map(source, proposed, changed)));
        var newRevision = history.Select(x => x with { RevisionId = Guid.NewGuid() }).ToArray();
        Assert.Throws<InvalidOperationException>(() => ChirpPlanLineageMapper.RequireExact(mapped, ChirpPlanLineageMapper.Map(source, proposed, newRevision)));
        ChirpHistoricalRange[] shifted = [history[0] with { EndScalarExclusive = 402, TextSha256 = ArticleAudioManifest.HashText(source[..402]) },
            history[1] with { StartScalar = 402, TextSha256 = ArticleAudioManifest.HashText(source[402..]) }];
        Assert.Throws<InvalidOperationException>(() => ChirpPlanLineageMapper.RequireExact(mapped, ChirpPlanLineageMapper.Map(source, proposed, shifted)));
    }

    [Fact]
    public void MissingOldCoverageDuplicateAttemptAndStaleTextReject()
    {
        string source = string.Concat(Enumerable.Repeat("文，", 400));
        var history = History(source);
        var proposed = ChirpNarrationPlanner.Plan(source);
        Assert.Throws<InvalidOperationException>(() => ChirpPlanLineageMapper.Map(source, proposed, history[..1]));
        Assert.Throws<InvalidOperationException>(() => ChirpPlanLineageMapper.Map(source, proposed,
            [history[0], history[1] with { Attempts = history[0].Attempts }]));
        Assert.Throws<InvalidOperationException>(() => ChirpPlanLineageMapper.Map(source, proposed,
            [history[0] with { RevisionId = Guid.Empty }, history[1]]));
        Assert.Throws<InvalidOperationException>(() => ChirpPlanLineageMapper.Map(source.Replace('文', '字'), proposed, history));
    }

    [Fact]
    public void LargeHistoricalAttemptSetIsStoredOnceRatherThanMultipliedPerRequest()
    {
        string source = string.Concat(Enumerable.Repeat("文，", 4_000));
        var attempts = Enumerable.Range(0, 10_000).Select(x => new ChirpHistoricalAttempt("attempt-" + x.ToString(System.Globalization.CultureInfo.InvariantCulture),
            new string('a', 64), 1)).ToArray();
        var old = new ChirpHistoricalRange(Guid.NewGuid(), Guid.NewGuid(), 0, 8_000, ArticleAudioManifest.HashText(source), attempts);
        var plan = ChirpPlanLineageMapper.Map(source, ChirpNarrationPlanner.Plan(source), [old]);
        Assert.Equal(10_000, Assert.Single(plan.History).Attempts.Count);
        Assert.True(plan.Ranges.Count > 20);
        Assert.All(plan.Ranges, x => Assert.Equal(0, Assert.Single(x.HistoricalRangeIndexes)));
        attempts[0] = attempts[0] with { RetainedReservationMinorUnits = 0 };
        Assert.Equal(1, plan.History[0].Attempts[0].RetainedReservationMinorUnits);
    }

    private static ChirpHistoricalRange[] History(string source) =>
    [
        new(Guid.NewGuid(), Guid.NewGuid(), 0, 400, ArticleAudioManifest.HashText(source[..400]),
            [new("root-one", new string('a', 64), 3)]),
        new(Guid.NewGuid(), Guid.NewGuid(), 400, 800, ArticleAudioManifest.HashText(source[400..]),
            [new("root-two", new string('b', 64), 3), new("recovery-two", new string('c', 64), 3)]),
    ];
}
