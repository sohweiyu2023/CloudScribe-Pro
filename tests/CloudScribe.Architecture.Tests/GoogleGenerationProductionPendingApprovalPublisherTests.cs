using CloudScribe.App.Composition;
using CloudScribe.App.ViewModels;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Architecture.Tests;

public sealed class GoogleGenerationProductionPendingApprovalPublisherTests
{
    [Fact]
    public async Task PublishBindsExactCurrentCompileEvidenceIntoPendingApproval()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-09-04T03:00:00Z", null, System.Globalization.DateTimeStyles.RoundtripKind);
        var owner = new GoogleGenerationProductionPendingApprovalStateOwner();
        var publisher = new GoogleGenerationProductionPendingApprovalPublisher(owner);
        GoogleGenerationUiExecutionSnapshot snapshot = CreateSnapshot();

        GoogleGenerationProductionPendingApprovalStateOwner.PendingState published = publisher.Publish(
            CreateAccount(),
            CreateCapabilities(now),
            "pricing-1",
            7,
            snapshot,
            "USD",
            2,
            125,
            now);

        GoogleGenerationProductionPendingApprovalStateOwner.PendingState? current =
            await owner.ResolveCurrentAsync(CancellationToken.None);
        Assert.Same(published, current);
        Assert.Equal("account-1", published.Envelope.AccountId);
        Assert.Equal("credential-1", published.Envelope.CredentialReferenceId);
        Assert.Equal("capability-1", published.Envelope.CapabilityProvenanceId);
        Assert.Equal("pricing-1", published.Envelope.PricingProvenanceId);
        Assert.Equal(snapshot.ProviderRequest.CompiledPayload.Length, published.Envelope.CompiledPayloadBytes);
        Assert.Equal(125, published.CurrentEstimateMinorUnits);
    }

    [Fact]
    public async Task PublishWithStaleCapabilityEvidenceFailsWithoutPublishingPendingApproval()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-09-04T03:00:00Z", null, System.Globalization.DateTimeStyles.RoundtripKind);
        var owner = new GoogleGenerationProductionPendingApprovalStateOwner();
        var publisher = new GoogleGenerationProductionPendingApprovalPublisher(owner);
        GoogleCapabilitySnapshot stale = CreateCapabilities(now) with { ExpiresAtUtc = now };

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => publisher.Publish(
            CreateAccount(), stale, "pricing-1", 7, CreateSnapshot(), "USD", 2, 125, now));

        Assert.Contains("stale", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(await owner.ResolveCurrentAsync(CancellationToken.None));
    }

    [Fact]
    public async Task PublishWithAccountDriftFailsWithoutPublishingPendingApproval()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-09-04T03:00:00Z", null, System.Globalization.DateTimeStyles.RoundtripKind);
        var owner = new GoogleGenerationProductionPendingApprovalStateOwner();
        var publisher = new GoogleGenerationProductionPendingApprovalPublisher(owner);
        GoogleGenerationAccount drifted = CreateAccount() with { AccountId = "account-2" };

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => publisher.Publish(
            drifted, CreateCapabilities(now), "pricing-1", 7, CreateSnapshot(), "USD", 2, 125, now));

        Assert.Contains("identities do not match", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(await owner.ResolveCurrentAsync(CancellationToken.None));
    }

    private static GoogleGenerationAccount CreateAccount() =>
        new("account-1", "credential-1", new Uri("https://texttospeech.googleapis.com/"), "global");

    private static GoogleCapabilitySnapshot CreateCapabilities(DateTimeOffset now) =>
        new(
            "account-1",
            "capability-1",
            now.AddMinutes(-1),
            now.AddMinutes(10),
            new HashSet<string>(StringComparer.Ordinal) { "voice-1" },
            new HashSet<string>(StringComparer.Ordinal) { "MP3" },
            4096);

    private static GoogleGenerationUiExecutionSnapshot CreateSnapshot()
    {
        byte[] payload = "provider-payload"u8.ToArray();
        GenerationProviderRequest providerRequest = new(
            GoogleGenerationProvider.StableProviderId,
            GoogleGenerationProvider.SynthesizeOperationStableId,
            "account-1",
            "idempotency-1",
            payload,
            "MP3");
        GoogleGenerationPersistedQueueState queueState = new(
            "account-1",
            GoogleGenerationProvider.SynthesizeOperationStableId,
            "idempotency-1",
            false,
            null);
        return new GoogleGenerationUiExecutionSnapshot(
            new GoogleGenerationUiSelection("account-1", "project-1", "voice-1", "model-1", "capability-1", "MP3"),
            true,
            true,
            true,
            true,
            providerRequest,
            CreateTrustContext(),
            queueState,
            queueState,
            GoogleGenerationReconciliationResolutionEvidence.None,
            true,
            true,
            true,
            true);
    }

    private static GenerationCacheTrustContext CreateTrustContext() =>
        new(
            GoogleGenerationProvider.StableProviderId,
            "account-1",
            "project-1",
            "endpoint-1",
            "region-1",
            GoogleGenerationProvider.SynthesizeOperationStableId,
            "model-1",
            "voice-1",
            "voice-fingerprint-1",
            "speech-plan-1",
            "en-US",
            "controls-1",
            "MP3",
            "sample-format-1",
            "adapter-1",
            "compiler-1",
            "ast-1",
            "normalization-1",
            "pricing-1",
            "capability-1",
            "governance-1",
            "provider-feature-1",
            "account-capability-1");
}
