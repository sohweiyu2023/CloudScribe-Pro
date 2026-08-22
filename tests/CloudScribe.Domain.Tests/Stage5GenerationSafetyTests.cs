using CloudScribe.Domain.Generation;

namespace CloudScribe.Domain.Tests;

public sealed class Stage5GenerationSafetyTests
{
    [Fact]
    public void SpendAuthorizationRequiresExactCurrencyScaleRevisionAndProvenance()
    {
        var collectionId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var authorization = new GenerationSpendAuthorization(
            collectionId,
            new AuthorizedSpendCeiling("USD", 10_000, 2),
            new Dictionary<Guid, AuthorizedSpendCeiling>
            {
                [itemId] = new("USD", 4_000, 2),
            },
            "pricing-v1",
            7);

        authorization.Validate();

        Assert.True(authorization.AllowsCollectionSpend(new AuthorizedSpendCeiling("USD", 9_999, 2), 7, "pricing-v1"));
        Assert.False(authorization.AllowsCollectionSpend(new AuthorizedSpendCeiling("USD", 10_001, 2), 7, "pricing-v1"));
        Assert.False(authorization.AllowsCollectionSpend(new AuthorizedSpendCeiling("USD", 1, 2), 8, "pricing-v1"));
        Assert.False(authorization.AllowsCollectionSpend(new AuthorizedSpendCeiling("USD", 1, 2), 7, "pricing-v2"));
        Assert.False(authorization.AllowsCollectionSpend(new AuthorizedSpendCeiling("EUR", 1, 2), 7, "pricing-v1"));
    }

    [Fact]
    public void CircuitBreakerIsScopedAndUsesMonotonicCooldown()
    {
        var key = new GenerationCircuitBreakerKey("provider", "account", "endpoint", "region", "operation").Validate();
        Assert.Equal("account", key.AccountId);

        var time = new ManualTimeProvider();
        var breaker = new GenerationCircuitBreaker(2, TimeSpan.FromSeconds(30), time);
        breaker.RecordFailure();
        Assert.True(breaker.MayAttempt());
        breaker.RecordFailure();
        Assert.False(breaker.MayAttempt());

        time.Advance(TimeSpan.FromSeconds(31));
        Assert.True(breaker.MayAttempt());
    }

    [Fact]
    public void OutputWithAnySafetyDefectIsQuarantined()
    {
        var accepted = OutputQualityAssessment.Evaluate(true, true, true);
        var quarantined = OutputQualityAssessment.Evaluate(true, false, false);

        Assert.Equal(OutputQualityDisposition.Accepted, accepted.Disposition);
        Assert.Equal(OutputQualityDisposition.Quarantined, quarantined.Disposition);
        Assert.Contains("quality.duration.out-of-range", quarantined.DiagnosticCodes);
        Assert.Contains("quality.timing-marks.missing", quarantined.DiagnosticCodes);
    }

    [Fact]
    public void TimedTextRequiresOrderedUniqueNonOverlappingCuesWithProvenance()
    {
        var track = new TimedTextTrack(
        [
            new TimedTextCue(2, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), "second", "provider-request-1"),
            new TimedTextCue(1, TimeSpan.Zero, TimeSpan.FromSeconds(1), "first", "provider-request-1"),
        ]);

        Assert.Equal(1, track.Cues[0].Sequence);
        Assert.Equal(2, track.Cues[1].Sequence);

        Assert.Throws<ArgumentException>(() => new TimedTextTrack(
        [
            new TimedTextCue(1, TimeSpan.Zero, TimeSpan.FromSeconds(2), "a", "p"),
            new TimedTextCue(2, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), "b", "p"),
        ]));
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => 1_000_000;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan duration)
        {
            _timestamp = checked(_timestamp + (long)(duration.TotalSeconds * TimestampFrequency));
        }
    }
}
