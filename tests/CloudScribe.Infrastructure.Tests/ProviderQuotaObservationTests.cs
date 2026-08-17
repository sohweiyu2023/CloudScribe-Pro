using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Tests;

public sealed class ProviderQuotaObservationTests
{
    [Fact]
    public void ObservedQuotaPreservesAccountScopeUnitAndProvenanceWithoutGuessingCatalogTaxonomy()
    {
        ProviderAccountReference account = new("fake", "account-1", "Account 1", null, "default", "global");
        ProviderQuotaObservation observation = new(
            account, "provider-live-limit", "account", "requests", ProviderQuotaObservationState.Observed,
            4, 10, new DateTimeOffset(2026, 8, 17, 5, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 17, 5, 5, 0, TimeSpan.Zero),
            "response-header:x-quota", "Provider-reported account observation.");

        Assert.Equal(4, observation.ObservedValue);
        Assert.Equal(10, observation.LimitValue);
        Assert.Equal("response-header:x-quota", observation.ProvenanceId);
        Assert.False(observation.IsStale(new DateTimeOffset(2026, 8, 17, 5, 4, 0, TimeSpan.Zero)));
        Assert.True(observation.IsStale(new DateTimeOffset(2026, 8, 17, 5, 5, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void UnknownQuotaCannotPretendToCarryValues()
    {
        ProviderAccountReference account = new("fake", "account-1", "Account 1", null);
        Assert.Throws<ArgumentException>(() => new ProviderQuotaObservation(
            account, "limit", "account", "requests", ProviderQuotaObservationState.Unknown,
            1, null, DateTimeOffset.UnixEpoch, null, "unknown", "Not observed."));
    }

    [Fact]
    public void ObservationRejectsNonUtcOrNegativeEvidence()
    {
        ProviderAccountReference account = new("fake", "account-1", "Account 1", null);
        Assert.Throws<ArgumentException>(() => new ProviderQuotaObservation(
            account, "limit", "account", "requests", ProviderQuotaObservationState.Observed,
            1, 2, new DateTimeOffset(2026, 8, 17, 5, 0, 0, TimeSpan.FromHours(8)), null, "test", "Observed."));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProviderQuotaObservation(
            account, "limit", "account", "requests", ProviderQuotaObservationState.Observed,
            -1, 2, DateTimeOffset.UnixEpoch, null, "test", "Observed."));
    }

    [Fact]
    public void ProviderQuotaSourceIsAnExplicitOptionalCapabilityBoundary()
    {
        Assert.Single(typeof(IProviderQuotaSource).GetMethods());
        Assert.Equal("GetQuotaObservationsAsync", typeof(IProviderQuotaSource).GetMethods()[0].Name);
        Assert.False(typeof(IProviderAdapter).IsAssignableFrom(typeof(IProviderQuotaSource)));
    }

    [Fact]
    public void ConflictingObservationRequiresMeasuredEvidenceButRemainsExplicitlyConflicting()
    {
        ProviderAccountReference account = new("fake", "account-1", "Account 1", null);
        ProviderQuotaObservation observation = new(
            account, "limit", "account", "requests", ProviderQuotaObservationState.Conflicting,
            8, 10, DateTimeOffset.UnixEpoch, null, "console-vs-header", "Console and response header disagree.");
        Assert.Equal(ProviderQuotaObservationState.Conflicting, observation.State);
        Assert.Equal("Console and response header disagree.", observation.Reason);
    }
}
