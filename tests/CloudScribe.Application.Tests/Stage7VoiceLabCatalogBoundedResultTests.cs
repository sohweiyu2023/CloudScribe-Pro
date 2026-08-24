using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;

namespace CloudScribe.Application.Tests;

public sealed class Stage7VoiceLabCatalogBoundedResultTests
{
    [Fact]
    public async Task More_than_500_catalog_results_fail_closed_before_UI_or_audition()
    {
        var results = Enumerable.Range(0, 501)
            .Select(index => Selection($"voice-{index}"))
            .ToArray();
        var service = new VoiceLabCatalogQueryService((_, _) => Task.FromResult<IReadOnlyList<VoiceLabCatalogSelection>>(results));
        var query = new VoiceLabCatalogQuery("provider", "account", "project", null, null, false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.QueryAsync(
            query,
            accountAuthorized: true,
            projectAuthorized: true,
            privateVoiceAccessAuthorized: false));
    }

    private static VoiceLabCatalogSelection Selection(string voiceId) => new(
        VoiceStableId: voiceId,
        ProviderStableId: "provider",
        AccountStableId: "account",
        ProjectStableId: "project",
        CapabilityEvidenceId: "capability-current",
        VoiceFingerprint: $"fingerprint-{voiceId}",
        CapabilityCurrent: true,
        VoiceEnabled: true,
        AccountProjectAuthorized: true);
}
