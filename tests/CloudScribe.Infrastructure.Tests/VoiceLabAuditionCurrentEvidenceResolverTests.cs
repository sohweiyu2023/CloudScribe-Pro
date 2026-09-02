using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed class VoiceLabAuditionCurrentEvidenceResolverTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 3, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ResolveAsyncReturnsCurrentEvidenceWhenAllPersistedBindingsStillMatch()
    {
        VoiceLabCatalogSelection selection = CreateSelection();
        VoiceLabAuditionPersistedAuthorization persisted = CreatePersisted(selection);
        VoiceLabProjectAuthorizationEvidence project = CreateProjectAuthorization(selection);
        var resolver = new VoiceLabAuditionCurrentEvidenceResolver(
            new AuditionStore(persisted),
            new ProjectStore(project),
            new FixedTimeProvider(Now));
        VoiceLabAuditionRequest request = CreateRequest(selection);

        VoiceLabAuditionAuthorizationEvidence? evidence = await resolver.ResolveAsync(
            request,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.NotNull(evidence);
        Assert.Equal(selection, evidence.Selection);
        Assert.Equal("credential.current", evidence.CredentialReferenceId);
        Assert.Equal("pricing.current", evidence.PricingEvidenceId);
        Assert.Equal("spend-approved-1", evidence.SpendAuthorizationId);
        Assert.Equal(7, evidence.AccountRevision);
        Assert.True(evidence.PricingCurrent);
        Assert.True(evidence.SpendApproved);
    }

    [Fact]
    public async Task ResolveAsyncRejectsProjectRevisionDriftAfterSpendAuthorization()
    {
        VoiceLabCatalogSelection selection = CreateSelection();
        VoiceLabAuditionPersistedAuthorization persisted = CreatePersisted(selection);
        VoiceLabProjectAuthorizationEvidence project = CreateProjectAuthorization(selection) with { AccountRevision = 8 };
        var resolver = new VoiceLabAuditionCurrentEvidenceResolver(
            new AuditionStore(persisted),
            new ProjectStore(project),
            new FixedTimeProvider(Now));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync(
            CreateRequest(selection),
            TestContext.Current.CancellationToken)).ConfigureAwait(true);

        Assert.Contains("revision changed", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveAsyncRejectsCapabilityDriftAfterSpendAuthorization()
    {
        VoiceLabCatalogSelection selection = CreateSelection();
        VoiceLabAuditionPersistedAuthorization persisted = CreatePersisted(selection);
        VoiceLabProjectAuthorizationEvidence project = CreateProjectAuthorization(selection) with
        {
            CapabilityEvidenceId = Guid.NewGuid().ToString("D"),
        };
        var resolver = new VoiceLabAuditionCurrentEvidenceResolver(
            new AuditionStore(persisted),
            new ProjectStore(project),
            new FixedTimeProvider(Now));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync(
            CreateRequest(selection),
            TestContext.Current.CancellationToken)).ConfigureAwait(true);

        Assert.Contains("capability binding changed", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static VoiceLabCatalogSelection CreateSelection() => new(
        "voice-1",
        "google",
        "primary",
        "project-1",
        "11111111-1111-1111-1111-111111111111",
        "voice-fingerprint-1",
        CapabilityCurrent: true,
        VoiceEnabled: true,
        AccountProjectAuthorized: true);

    private static VoiceLabAuditionPersistedAuthorization CreatePersisted(VoiceLabCatalogSelection selection) => new(
        selection,
        "credential.current",
        "pricing.current",
        "spend-approved-1",
        AccountRevision: 7,
        CapturedAtUtc: Now.AddMinutes(-5),
        ExpiresAtUtc: Now.AddMinutes(30));

    private static VoiceLabProjectAuthorizationEvidence CreateProjectAuthorization(VoiceLabCatalogSelection selection) => new(
        selection.ProviderStableId,
        selection.AccountStableId,
        selection.ProjectStableId,
        AccountRevision: 7,
        "credential.current",
        selection.CapabilityEvidenceId,
        ProjectAuthorized: true,
        PrivateVoiceAccessAuthorized: false,
        CapturedAtUtc: Now.AddMinutes(-10),
        ExpiresAtUtc: Now.AddHours(1));

    private static VoiceLabAuditionRequest CreateRequest(VoiceLabCatalogSelection selection) => new(
        selection,
        CachePolicyEligible: false,
        ForceFresh: true,
        ExplicitSpendApproved: true,
        PricingCurrent: true,
        OutputFormat: "wav");

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class AuditionStore(VoiceLabAuditionPersistedAuthorization? persisted) : IVoiceLabAuditionAuthorizationStore
    {
        public Task<VoiceLabAuditionPersistedAuthorization?> LoadCurrentAsync(
            VoiceLabCatalogSelection selection,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(persisted is not null && persisted.Selection == selection ? persisted : null);
        }

        public Task SaveVerifiedAsync(
            VoiceLabAuditionPersistedAuthorization authorization,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class ProjectStore(VoiceLabProjectAuthorizationEvidence? evidence) : IVoiceLabProjectAuthorizationStore
    {
        public Task<VoiceLabProjectAuthorizationEvidence?> LoadCurrentAsync(
            string providerId,
            string accountId,
            string projectId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool matches = evidence is not null &&
                string.Equals(evidence.ProviderId, providerId, StringComparison.Ordinal) &&
                string.Equals(evidence.AccountId, accountId, StringComparison.Ordinal) &&
                string.Equals(evidence.ProjectId, projectId, StringComparison.Ordinal);
            return Task.FromResult(matches ? evidence : null);
        }

        public Task SaveVerifiedAsync(
            VoiceLabProjectAuthorizationEvidence authorization,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
