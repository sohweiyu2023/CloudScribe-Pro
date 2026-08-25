using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Application.Tests;

public sealed class Stage6GoogleUiSingleFlightTests
{
    [Fact]
    public async Task Concurrent_ui_generation_is_rejected_before_second_submit()
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
            return "provider-job-1";
        });
        var coordinator = new GoogleGenerationUiQueueCoordinator(new GoogleGenerationBoundQueueCoordinator(queue));

        var selection = new GoogleGenerationUiSelection(
            "account-1", "project-1", "global", "model-1", "voice-1", "speech-plan-1", "mp3", "pricing-1", "capability-1");
        var request = new GenerationProviderRequest(
            "google-cloud-text-to-speech", "synthesize-speech", "account-1", "project-1", "model-1", "voice-1", "mp3", "payload");
        var trust = new GenerationCacheTrustContext(
            "google-cloud-text-to-speech", "synthesize-speech", "account-1", "project-1", "global", "model-1", "voice-1", "speech-plan-1", "mp3", "2.23", "pricing-1", "capability-1", "governance-1");
        var previous = new GoogleGenerationPersistedQueueState("job-1", GoogleGenerationPersistedQueueStatus.ReadyToSubmit, null, null);
        var current = new GoogleGenerationPersistedQueueState("job-1", GoogleGenerationPersistedQueueStatus.Submitting, null, null);

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
