using CloudScribe.Application.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed class GoogleLongAudioRunSummaryTests
{
    [Fact]
    public void JobTotalsIncludeFailedAndReplacementHoldsButExcludeOtherRoots()
    {
        const string root = "root";
        var ledger = new GoogleLongAudioBudgetLedger(1, "scope",
        [new(Guid.NewGuid(), root, 12), new(Guid.NewGuid(), "child", 3, new(root, root, 0, 4, "v1", "hash")),
            new(Guid.NewGuid(), "replacement", 4, new(root, "child", 0, 4, "v1", "hash")), new(Guid.NewGuid(), "other", 100)]);
        var value = GoogleLongAudioRunSummary.FromLedger(ledger, root, null, 500, 50, 1000);
        Assert.Equal(3, value.RetainedAttempts);
        Assert.Equal(19, value.JobRetainedCommitmentsUsdCents);
        Assert.Equal(619, value.AllJobsRetainedCommitmentsUsdCents);
        Assert.Equal(669, value.CumulativeProvisionUsdCents);
        Assert.Null(value.Timing);
    }

    [Fact]
    public void DuplicateOrNegativeClaimsAreNotPresentedAsValidSpending()
    {
        var claim = new GoogleLongAudioBudgetClaim(Guid.NewGuid(), "root", 1);
        Assert.Throws<InvalidDataException>(() => GoogleLongAudioRunSummary.FromLedger(new(1, "scope", [claim, claim]), "root", null, 0, 0, 10));
        Assert.Throws<InvalidDataException>(() => GoogleLongAudioRunSummary.FromLedger(new(1, "scope", [claim with { MaximumUsdCents = -1 }]), "root", null, 0, 0, 10));
    }

    [Fact]
    public async Task RestartIncludesPauseInElapsedButNotActiveAndFreezesCompletion()
    {
        using var fixture = new TimingFixture();
        var start = DateTimeOffset.Parse("2026-11-07T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var first = await fixture.Store.BeginAsync(start, false, TestContext.Current.CancellationToken);
        await fixture.Store.EndAsync(first, start.AddMinutes(2), TimeSpan.FromMinutes(2), LongAudioWorkflowStage.Stopped, TestContext.Current.CancellationToken);
        var restarted = new GoogleLongAudioTimingStore(fixture.Path, "root");
        var second = await restarted.BeginAsync(start.AddHours(1), true, TestContext.Current.CancellationToken);
        var finished = await restarted.EndAsync(second, start.AddHours(1).AddMinutes(3), TimeSpan.FromMinutes(3), LongAudioWorkflowStage.Completed, TestContext.Current.CancellationToken);
        Assert.Equal(TimeSpan.FromMinutes(63), finished.ElapsedAt(start.AddDays(1)));
        Assert.Equal(300000, finished.RecordedActiveMilliseconds);
        Assert.False(finished.EarlierHistoryUnknown);
        Assert.False(finished.ActiveTimeIncomplete);
        Assert.Equal(finished, await restarted.BeginAsync(start.AddDays(1), true, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LegacyAndInterruptedRunsNeverClaimCompleteActiveTiming()
    {
        using var fixture = new TimingFixture();
        var start = DateTimeOffset.UtcNow;
        var first = await fixture.Store.BeginAsync(start, true, TestContext.Current.CancellationToken);
        Assert.True(first.EarlierHistoryUnknown);
        var restarted = await new GoogleLongAudioTimingStore(fixture.Path, "root").BeginAsync(start.AddMinutes(5), true, TestContext.Current.CancellationToken);
        Assert.True(restarted.ActiveTimeIncomplete);
        Assert.Equal(0, restarted.RecordedActiveMilliseconds);
        Assert.Equal(start, restarted.FirstObservedUtc);
    }

    [Fact]
    public async Task WrongBindingAndMalformedTimingAreRejected()
    {
        using var fixture = new TimingFixture();
        await fixture.Store.BeginAsync(DateTimeOffset.UtcNow, false, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() => new GoogleLongAudioTimingStore(fixture.Path, "other").ReadAsync(TestContext.Current.CancellationToken));
        await File.WriteAllTextAsync(fixture.Path, "{}", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Store.ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OptionalMetricsFailureDoesNotReplaceSuccessfulOutcomeOrOriginalError()
    {
        Exception? reported = null;
        bool saved = await GoogleLongAudioRunMetrics.TryRecordAsync(() => throw new IOException("synthetic metrics failure"), error => reported = error);
        Assert.False(saved); Assert.IsType<IOException>(reported);
        Assert.False(await GoogleLongAudioRunMetrics.TryRecordAsync(() => throw new IOException("write failed"), _ => throw new IOException("logger failed")));
        var original = new InvalidOperationException("synthetic original provider outcome");
        Task FailMetricsAsync() => throw new IOException("metrics failed");
        async Task FailAsync()
        {
            try { throw original; }
            finally { await GoogleLongAudioRunMetrics.TryRecordAsync(FailMetricsAsync, _ => { }).ConfigureAwait(false); }
        }
        Assert.Same(original, await Assert.ThrowsAsync<InvalidOperationException>(FailAsync));
    }

    private sealed class TimingFixture : IDisposable
    {
        private readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cloudscribe-timing-test-" + Guid.NewGuid().ToString("N"));
        internal string Path { get; }
        internal GoogleLongAudioTimingStore Store { get; }
        internal TimingFixture()
        {
            Directory.CreateDirectory(directory);
            Path = System.IO.Path.Combine(directory, "timing.json"); Store = new(Path, "root");
        }
        public void Dispose() => Directory.Delete(directory, recursive: true);
    }
}
