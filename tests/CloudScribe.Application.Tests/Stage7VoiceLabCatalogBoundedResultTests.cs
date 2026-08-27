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
            privateVoiceAccessAuthorized: false,
            TestContext.Current.CancellationToken)).ConfigureAwait(true);
    }

    [Fact]
    public async Task Pre_cancelled_catalog_query_never_reaches_transport()
    {
        var transportCalls = 0;
        var service = new VoiceLabCatalogQueryService((_, _) =>
        {
            transportCalls++;
            return Task.FromResult<IReadOnlyList<VoiceLabCatalogSelection>>(Array.Empty<VoiceLabCatalogSelection>());
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.QueryAsync(
            new VoiceLabCatalogQuery("provider", "account", "project", null, null, false),
            accountAuthorized: true,
            projectAuthorized: true,
            privateVoiceAccessAuthorized: false,
            cancellation.Token)).ConfigureAwait(true);

        Assert.Equal(0, transportCalls);
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
