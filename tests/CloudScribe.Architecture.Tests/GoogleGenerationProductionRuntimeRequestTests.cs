using CloudScribe.App.Composition;
using CloudScribe.App.ViewModels;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Architecture.Tests;

public sealed class GoogleGenerationProductionRuntimeRequestTests
{
    [Fact]
    public void ValidateAcceptsExactEnvelopeBoundRequest()
    {
        GoogleGenerationProductionRuntimeRequest request = CreateRequest();

        Assert.Same(request, request.Validate());
    }

    [Fact]
    public void ValidateRejectsPricingProvenanceDrift()
    {
        GoogleGenerationProductionRuntimeRequest request = CreateRequest() with
        {
            PricingProvenanceId = "pricing-drifted",
        };

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(request.Validate);
        Assert.Contains("pricing provenance", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateRejectsRequestRevisionDrift()
    {
        GoogleGenerationProductionRuntimeRequest request = CreateRequest() with
        {
            RequestRevision = 8,
        };

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(request.Validate);
        Assert.Contains("revision", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateRejectsCompiledPayloadDrift()
    {
        GoogleGenerationProductionRuntimeRequest request = CreateRequest();
        GenerationProviderRequest driftedProviderRequest = new(
            GoogleGenerationProvider.StableProviderId,
            GoogleGenerationProvider.SynthesizeOperationStableId,
            "account-1",
            "idempotency-1",
            "different-payload"u8.ToArray(),
            "MP3");

        request = request with
        {
            Snapshot = request.Snapshot with { ProviderRequest = driftedProviderRequest },
        };

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(request.Validate);
        Assert.Contains("compiled payload", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static GoogleGenerationProductionRuntimeRequest CreateRequest()
    {
        byte[] payload = "provider-payload"u8.ToArray();
        GoogleGenerationSubmissionEnvelope envelope = new(
            "account-1",
            "credential-1",
            "capability-1",
            "pricing-1",
            7,
            "voice-1",
            "MP3",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)).ToLowerInvariant(),
            payload.Length);
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
        GenerationCacheTrustContext trust = new(
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
        GoogleGenerationUiExecutionSnapshot snapshot = new(
            new GoogleGenerationUiSelection("account-1", "project-1", "voice-1", "model-1", "capability-1", "MP3"),
            true,
            true,
            true,
            true,
            providerRequest,
            trust,
            queueState,
            queueState,
            GoogleGenerationReconciliationResolutionEvidence.None,
            true,
            true,
            true,
            true);

        return new GoogleGenerationProductionRuntimeRequest(
            "account-1",
            envelope,
            "pricing-1",
            7,
            "USD",
            2,
            123,
            snapshot);
    }
}
