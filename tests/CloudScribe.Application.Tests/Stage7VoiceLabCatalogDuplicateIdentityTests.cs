using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;

namespace CloudScribe.Application.Tests;

public sealed class Stage7VoiceLabCatalogDuplicateIdentityTests
{
    [Fact]
    public async Task Duplicate_voice_identity_fails_closed_before_UI_or_audition()
    {
        var duplicated = new[]
        {
            Selection("voice-1", "fingerprint-a"),
            Selection("voice-1", "fingerprint-b")
        };

        var service = new VoiceLabCatalogQueryService((_, _) => Task.FromResult<IReadOnlyList<VoiceLabCatalogSelection>>(duplicated));
        var query = new VoiceLabCatalogQuery("provider", "account", "project", null, null, false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.QueryAsync(
            query,
            accountAuthorized: true,
            projectAuthorized: true,
            privateVoiceAccessAuthorized: false,
            TestContext.Current.CancellationToken)).ConfigureAwait(true);
    }

    private static VoiceLabCatalogSelection Selection(string voiceId, string fingerprint) => new(
        VoiceStableId: voiceId,
        ProviderStableId: "provider",
        AccountStableId: "account",
        ProjectStableId: "project",
        CapabilityEvidenceId: "capability-current",
        VoiceFingerprint: fingerprint,
        CapabilityCurrent: true,
        VoiceEnabled: true,
        AccountProjectAuthorized: true);
}
