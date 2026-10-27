using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Theory]
    [InlineData("accepted")]
    [InlineData("lost-claim-ack")]
    [InlineData("lost-outcome-ack")]
    [InlineData("invalid-admission")]
    public async Task RecoveryUsesOrdinaryDurableQueueAndNeverDispatchesTwice(string scenario)
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await RecoveryEvidenceAsync(f);
        var (admission, budget) = await CreateInstalledBudgetAsync(f, e);
        var service = new GoogleExplicitRecoveryService(f, TimeProvider.System, budget, admission);
        var preview = await service.PrepareAsync(e.Intent, 3, TestContext.Current.CancellationToken);
        var approved = await service.ApproveAsync(Confirmation(preview), TestContext.Current.CancellationToken);
        service.DiscardUnapprovedPreview(preview.PreviewId); // Closing review never revokes committed reservation.
        var envelope = e.Intent.Envelope with { RecoveryAuthorizationId = approved.AuthorizationId };
        int fakeSubmissions = 0;
        var persisted = new GoogleGenerationPersistedQueueStateStore(f);
        var queue = new GoogleGenerationQueueCoordinator((_, _, _) =>
        {
            fakeSubmissions++;
            return Task.FromResult(new GenerationProviderResponse(SubmissionDisposition.Accepted, null, new byte[] { 1, 2, 3 },
                "audio/mpeg", null, "google-accepted"));
        });
        var bound = new GoogleGenerationBoundQueueCoordinator(queue, persisted.LoadAsync, persisted.SaveAsync,
            async (claim, token) =>
            {
                bool owned = await service.TryClaimAuthorizedAsync(envelope, e.Intent.ProjectId, "USD", 2, 3, claim, token).ConfigureAwait(false);
                if (owned && string.Equals(scenario, "lost-claim-ack", StringComparison.Ordinal)) throw new IOException("Synthetic lost local acknowledgement.");
                return owned;
            }, async (outcome, token) =>
            {
                await persisted.SaveOutcomeAsync(outcome, token).ConfigureAwait(false);
                if (string.Equals(scenario, "lost-outcome-ack", StringComparison.Ordinal)) throw new IOException("Synthetic lost local outcome acknowledgement.");
            });
        var request = new GenerationProviderRequest(GoogleGenerationProvider.StableProviderId, GoogleGenerationProvider.SynthesizeOperationStableId,
            envelope.AccountId, approved.IdempotencyKey, e.Intent.CompiledPayload, "MP3");
        var trust = RecoveryTrust(e);
        Assert.Equal(0, fakeSubmissions); // Preparation and approval have no provider dependency.
        if (string.Equals(scenario, "accepted", StringComparison.Ordinal))
            Assert.NotNull((await bound.ProcessDurableAsync(request, trust, true, true, true, true, TestContext.Current.CancellationToken)).Response);
        else if (string.Equals(scenario, "invalid-admission", StringComparison.Ordinal))
            await Assert.ThrowsAsync<InvalidOperationException>(() => bound.ProcessDurableAsync(request, trust, false, true, true, true, TestContext.Current.CancellationToken));
        else
            await Assert.ThrowsAsync<IOException>(() => bound.ProcessDurableAsync(request, trust, true, true, true, true, TestContext.Current.CancellationToken));
        int expected = string.Equals(scenario, "accepted", StringComparison.Ordinal) || string.Equals(scenario, "lost-outcome-ack", StringComparison.Ordinal) ? 1 : 0;
        Assert.Equal(expected, fakeSubmissions);
        if (!string.Equals(scenario, "invalid-admission", StringComparison.Ordinal))
            await Assert.ThrowsAsync<InvalidOperationException>(() => bound.ProcessDurableAsync(request, trust, true, true, true, true, TestContext.Current.CancellationToken));
        Assert.Equal(expected, fakeSubmissions);
        await AssertConcreteBudgetAsync(f, e, budget, 253, 67);
        Assert.Equal(e.Previous, await persisted.LoadAsync(e.Previous.AccountId, e.Previous.OperationStableId, e.Previous.IdempotencyKey, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DiscardedUnapprovedPreviewsDoNotExhaustReviewSlotsOrReleaseApproval()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await RecoveryEvidenceAsync(f);
        var (admission, budget) = await CreateInstalledBudgetAsync(f, e);
        var service = new GoogleExplicitRecoveryService(f, TimeProvider.System, budget, admission);
        for (int i = 0; i < 40; i++)
        {
            var review = await service.PrepareAsync(e.Intent, 3, TestContext.Current.CancellationToken);
            service.DiscardUnapprovedPreview(review.PreviewId);
        }
        var final = await service.PrepareAsync(e.Intent, 3, TestContext.Current.CancellationToken);
        var approved = await service.ApproveAsync(Confirmation(final), TestContext.Current.CancellationToken);
        service.DiscardUnapprovedPreview(final.PreviewId);
        Assert.Equal(approved, await service.FindApprovedAsync(final.ReviewSha256, TestContext.Current.CancellationToken));
        await AssertConcreteBudgetAsync(f, e, budget, 253, 67);
    }

    private static GenerationCacheTrustContext RecoveryTrust(RecoveryEvidence e) => new(GoogleGenerationProvider.StableProviderId,
        e.Intent.Envelope.AccountId, e.Intent.ProjectId, "synthetic-endpoint", "global", GoogleGenerationProvider.SynthesizeOperationStableId,
        "synthetic-model", e.Intent.Envelope.VoiceName, "synthetic-fingerprint", "synthetic-plan", "en-US", "synthetic-controls", "MP3",
        "synthetic-sample", "synthetic-adapter", "synthetic-compiler", "synthetic-ast", "synthetic-normalization",
        e.Intent.Envelope.PricingProvenanceId, e.Intent.Envelope.CapabilityProvenanceId, "synthetic-governance", "synthetic-feature", "synthetic-account-capability");
}
