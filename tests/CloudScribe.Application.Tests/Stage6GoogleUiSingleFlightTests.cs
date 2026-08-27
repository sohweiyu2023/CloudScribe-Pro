using System.Text;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Application.Tests;

public sealed class Stage6GoogleUiSingleFlightTests
{
    [Fact]
    public async Task ConcurrentUiGenerationIsRejectedBeforeSecondSubmit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var firstSubmitEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstSubmit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var submitCalls = 0;
        var queue = new GoogleGenerationQueueCoordinator(async (_, _, submitCancellationToken) =>
        {
            Interlocked.Increment(ref submitCalls);
            firstSubmitEntered.TrySetResult();
            await releaseFirstSubmit.Task.WaitAsync(submitCancellationToken).ConfigureAwait(false);
            return new GenerationProviderResponse(
                SubmissionDisposition.Accepted,
                "provider-job-1",
                new byte[] { 1 },
                "audio/mpeg",
                null,
                "google.accepted");
        });
        var coordinator = new GoogleGenerationUiQueueCoordinator(new GoogleGenerationBoundQueueCoordinator(queue));

        var selection = new GoogleGenerationUiSelection(
            "account-1", "project-1", "voice-1", "model-1", "capability-1", "mp3");
        var request = new GenerationProviderRequest(
            "google-cloud-text-to-speech",
            "synthesize-speech",
            "account-1",
            "job-1",
            Encoding.UTF8.GetBytes("payload"),
            "mp3");
        var trust = new GenerationCacheTrustContext(
            ProviderStableId: "google-cloud-text-to-speech",
            AccountId: "account-1",
            ProjectId: "project-1",
            EndpointId: "https://texttospeech.googleapis.com",
            RegionId: "global",
            OperationStableId: "synthesize-speech",
            ResolvedModelId: "model-1",
            VoiceStableId: "voice-1",
            VoiceFingerprint: "voice-fingerprint-1",
            SpeechPlanIdentity: "speech-plan-1",
            LanguageTag: "en-US",
            SynthesisControlsIdentity: "controls-1",
            OutputFormat: "mp3",
            SampleFormatIdentity: "sample-format-1",
            AdapterVersion: "adapter-v1",
            CompilerVersion: "compiler-v1",
            AstVersion: "ast-v1",
            NormalizationVersion: "normalization-v1",
            PricingIdentity: "pricing-1",
            CapabilityIdentity: "capability-1",
            GovernancePolicyIdentity: "governance-1",
            ProviderFeatureIdentity: "features-1",
            AccountCapabilityIdentity: "account-capability-1").Validate();
        var previous = new GoogleGenerationPersistedQueueState(
            "account-1", "synthesize-speech", "job-1", false, null);
        var current = new GoogleGenerationPersistedQueueState(
            "account-1", "synthesize-speech", "job-1", false, null);

        var first = coordinator.ProcessPersistedTransitionAsync(
            selection, true, true, true, true,
            request, trust, previous, current,
            GoogleGenerationReconciliationResolutionEvidence.None,
            true, true, true, true,
            cancellationToken);
        await firstSubmitEntered.Task.WaitAsync(cancellationToken).ConfigureAwait(true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ProcessPersistedTransitionAsync(
            selection, true, true, true, true,
            request, trust, previous, current,
            GoogleGenerationReconciliationResolutionEvidence.None,
            true, true, true, true,
            cancellationToken)).ConfigureAwait(true);
        Assert.Equal(1, Volatile.Read(ref submitCalls));

        releaseFirstSubmit.TrySetResult();
        await first.ConfigureAwait(true);
        Assert.Equal(1, submitCalls);
    }
}
