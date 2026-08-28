using System.Globalization;
using CloudScribe.Domain.Generation;

namespace CloudScribe.Domain.Tests;

public sealed class Stage7VoiceLabCatalogTests
{
    [Fact]
    public void SearchExcludesStaleAndFiltersCapabilitiesDeterministically()
    {
        var now = DateTimeOffset.Parse("2026-08-23T00:00:00Z", CultureInfo.InvariantCulture);
        var entries = new[]
        {
            Entry("p2", "voice-b", "English B", now.AddHours(1), true, "marks"),
            Entry("p1", "voice-a", "English A", now.AddHours(1), true, "marks", "multi-speaker"),
            Entry("p1", "voice-old", "Old", now.AddMinutes(-1), true, "marks", "multi-speaker"),
        };
        var query = new VoiceLabQuery(
            null,
            null,
            "en-US",
            new HashSet<string>(StringComparer.Ordinal) { "multi-speaker" },
            true);

        var results = VoiceLabCatalog.Search(entries, query, now);

        var selected = Assert.Single(results);
        Assert.Equal("voice-a", selected.VoiceStableId);
    }

    [Fact]
    public void SearchRejectsDuplicateStableIdentity()
    {
        var now = DateTimeOffset.Parse("2026-08-23T00:00:00Z", CultureInfo.InvariantCulture);
        var duplicate = Entry("p1", "voice-a", "A", now.AddHours(1), true, "marks");

        Assert.Throws<InvalidOperationException>(() => VoiceLabCatalog.Search(
            new[] { duplicate, duplicate with { DisplayName = "Duplicate" } },
            new VoiceLabQuery(null, null, null, new HashSet<string>(StringComparer.Ordinal), false),
            now));
    }

    [Fact]
    public void SearchPreservesProviderAndCapabilityProvenance()
    {
        var now = DateTimeOffset.Parse("2026-08-23T00:00:00Z", CultureInfo.InvariantCulture);
        var entry = Entry("p1", "voice-a", "Narrator", now.AddHours(1), false, "marks");

        var result = Assert.Single(VoiceLabCatalog.Search(
            new[] { entry },
            new VoiceLabQuery("narr", "p1", null, new HashSet<string>(StringComparer.Ordinal), false),
            now));

        Assert.Equal("pricing-v1", result.PricingProvenanceId);
        Assert.Equal("cap-v1", result.CapabilityProvenanceId);
        Assert.Equal("p1/acct/voice-a", result.StableIdentity);
    }

    private static VoiceLabEntry Entry(
        string provider,
        string voice,
        string display,
        DateTimeOffset expires,
        bool audition,
        params string[] capabilities) =>
        new(
            provider,
            "acct",
            voice,
            display,
            "en-US",
            new HashSet<string>(capabilities, StringComparer.Ordinal),
            "pricing-v1",
            "cap-v1",
            DateTimeOffset.Parse("2026-08-22T23:00:00Z", CultureInfo.InvariantCulture),
            expires,
            audition);
}
