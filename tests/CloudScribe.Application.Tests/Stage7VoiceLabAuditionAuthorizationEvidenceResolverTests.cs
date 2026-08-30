using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;

namespace CloudScribe.Application.Tests;

public sealed class Stage7VoiceLabAuditionAuthorizationEvidenceResolverTests
{
    [Fact]
    public async Task ExactCurrentEvidenceResolvesForExactRequest()
    {
        var request = CurrentRequest();
        var evidence = CurrentEvidence();
        VoiceLabAuditionRequest? resolvedRequest = null;
        var resolver = new VoiceLabAuditionAuthorizationEvidenceResolver((actual, _) =>
        {
            resolvedRequest = actual;
            return Task.FromResult<VoiceLabAuditionAuthorizationEvidence?>(evidence);
        });

        var result = await resolver.ResolveAsync(request, TestContext.Current.CancellationToken);

        Assert.Same(request, resolvedRequest);
        Assert.Equal(evidence, result);
    }

    [Fact]
    public async Task MissingCurrentEvidenceFailsClosed()
    {
        var resolver = new VoiceLabAuditionAuthorizationEvidenceResolver((_, _) =>
            Task.FromResult<VoiceLabAuditionAuthorizationEvidence?>(null));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.ResolveAsync(CurrentRequest(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ChangedCapabilityEvidenceFailsClosed()
    {
        var evidence = CurrentEvidence() with
        {
            Selection = CurrentSelection() with { CapabilityEvidenceId = "capability-b" }
        };
        var resolver = Resolver(evidence);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.ResolveAsync(CurrentRequest(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RevokedSpendFailsClosed()
    {
        var resolver = Resolver(CurrentEvidence() with { SpendApproved = false });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.ResolveAsync(CurrentRequest(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StalePricingFailsClosed()
    {
        var resolver = Resolver(CurrentEvidence() with { PricingCurrent = false });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.ResolveAsync(CurrentRequest(), TestContext.Current.CancellationToken));
    }

    private static VoiceLabAuditionAuthorizationEvidenceResolver Resolver(
        VoiceLabAuditionAuthorizationEvidence evidence) =>
        new((_, _) => Task.FromResult<VoiceLabAuditionAuthorizationEvidence?>(evidence));

    private static VoiceLabAuditionRequest CurrentRequest() => new(
        CurrentSelection(),
        CachePolicyEligible: false,
        ForceFresh: true,
        ExplicitSpendApproved: true,
        PricingCurrent: true,
        OutputFormat: "wav");

    private static VoiceLabAuditionAuthorizationEvidence CurrentEvidence() => new(
        CurrentSelection(),
        CredentialReferenceId: "credential-a",
        PricingEvidenceId: "pricing-a",
        SpendAuthorizationId: "spend-a",
        PricingCurrent: true,
        SpendApproved: true);

    private static VoiceLabCatalogSelection CurrentSelection() => new(
        VoiceStableId: "voice-a",
        ProviderStableId: "google-cloud-text-to-speech",
        AccountStableId: "account-a",
        ProjectStableId: "project-a",
        CapabilityEvidenceId: "capability-a",
        VoiceFingerprint: "fingerprint-a",
        CapabilityCurrent: true,
        VoiceEnabled: true,
        AccountProjectAuthorized: true);
}
