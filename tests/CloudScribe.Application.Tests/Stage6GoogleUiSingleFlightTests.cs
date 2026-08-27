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
        var coordinator = CreateCoordinator(firstSubmitEntered, releaseFirstSubmit, () => Interlocked.Increment(ref submitCalls));
        var state = CreateState();

        var first = ExecuteAsync(coordinator, state, cancellationToken);
        await firstSubmitEntered.Task.WaitAsync(cancellationToken).ConfigureAwait(true);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ExecuteAsync(coordinator, state, cancellationToken)).ConfigureAwait(true);
        Assert.Equal(1, Volatile.Read(ref submitCalls));

        releaseFirstSubmit.TrySetResult();
        await first.ConfigureAwait(true);
        Assert.Equal(1, submitCalls);
    }

    private static GoogleGenerationUiQueueCoordinator CreateCoordinator(
        TaskCompletionSource firstSubmitEntered,
        TaskCompletionSource releaseFirstSubmit,
        Action recordSubmit)
    {
        var queue = new GoogleGenerationQueueCoordinator(async (_, _, cancellationToken) =>
        {
            recordSubmit();
            firstSubmitEntered.TrySetResult();
            await releaseFirstSubmit.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new GenerationProviderResponse(
                SubmissionDisposition.Accepted,
                "provider-job-1",
                new byte[] { 1 },
                "audio/mpeg",
                null,
                "google.accepted");
        });
        return new GoogleGenerationUiQueueCoordinator(new GoogleGenerationBoundQueueCoordinator(queue));
    }

    private static UiState CreateState() => new(
        new GoogleGenerationUiSelection("account-1", "project-1", "voice-1", "model-1", "capability-1", "mp3"),
        new GenerationProviderRequest(
            "google-cloud-text-to-speech",
            "synthesize-speech",
            "account-1",
            "job-1",
            Encoding.UTF8.GetBytes("payload"),
            "mp3"),
        CreateTrust(),
        new GoogleGenerationPersistedQueueState("account-1", "synthesize-speech", "job-1", false, null));

    private static GenerationCacheTrustContext CreateTrust() => new GenerationCacheTrustContext(
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

    private static Task<GoogleGenerationPersistedQueueOutcome> ExecuteAsync(
        GoogleGenerationUiQueueCoordinator coordinator,
        UiState state,
        CancellationToken cancellationToken) => coordinator.ProcessPersistedTransitionAsync(
            state.Selection,
            accountAuthorized: true,
            projectAuthorized: true,
            capabilityCurrent: true,
            pricingCurrent: true,
            state.Request,
            state.Trust,
            state.QueueState,
            state.QueueState,
            GoogleGenerationReconciliationResolutionEvidence.None,
            admissionCurrent: true,
            accountCredentialAvailable: true,
            pricingApproved: true,
            postCompileLimitsSatisfied: true,
            cancellationToken);

    private sealed record UiState(
        GoogleGenerationUiSelection Selection,
        GenerationProviderRequest Request,
        GenerationCacheTrustContext Trust,
        GoogleGenerationPersistedQueueState QueueState);
}
