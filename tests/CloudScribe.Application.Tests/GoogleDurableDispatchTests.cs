using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Application.Tests;

public sealed class GoogleDurableDispatchTests
{
    [Fact]
    public async Task LostClaimAcknowledgementCannotDispatchOrEraseDurableUncertainty()
    {
        GoogleGenerationPersistedQueueState? saved = null;
        int submissions = 0;
        var queue = new GoogleGenerationQueueCoordinator((_, _, _) => { submissions++; throw new InvalidOperationException("must not dispatch"); });
        var coordinator = new GoogleGenerationBoundQueueCoordinator(queue,
            (_, _, _, _) => Task.FromResult(saved), (_, _) => Task.CompletedTask,
            (claim, _) => { saved = claim; throw new IOException("Synthetic lost acknowledgement after commit"); },
            (_, _) => throw new InvalidOperationException("No response was observed"));
        await Assert.ThrowsAsync<IOException>(() => coordinator.ProcessDurableAsync(Request(), Trust(), true, true, true, true, TestContext.Current.CancellationToken));
        Assert.Equal(0, submissions);
        Assert.True(saved!.UnresolvedSubmission);
        Assert.True(saved.SubmissionAttempted);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ProcessDurableAsync(Request(), Trust(), true, true, true, true, TestContext.Current.CancellationToken));
        Assert.Equal(0, submissions);
    }

    [Fact]
    public async Task LostOutcomeAcknowledgementCannotReplayAcceptedRequest()
    {
        GoogleGenerationPersistedQueueState? saved = null;
        int submissions = 0;
        var queue = new GoogleGenerationQueueCoordinator((_, _, _) =>
        {
            submissions++;
            return Task.FromResult(new GenerationProviderResponse(SubmissionDisposition.Accepted, null,
                new byte[] { 1 }, "audio/mpeg", null, "google-accepted"));
        });
        var coordinator = new GoogleGenerationBoundQueueCoordinator(queue,
            (_, _, _, _) => Task.FromResult(saved), (_, _) => Task.CompletedTask,
            (claim, _) => { saved = claim; return Task.FromResult(true); },
            (outcome, _) => { saved = outcome; throw new IOException("Synthetic lost acknowledgement after outcome commit"); });
        await Assert.ThrowsAsync<IOException>(() => coordinator.ProcessDurableAsync(Request(), Trust(), true, true, true, true, TestContext.Current.CancellationToken));
        Assert.Equal(1, submissions);
        Assert.True(saved!.SubmissionAttempted);
        Assert.False(saved.UnresolvedSubmission);
        Assert.Null(saved.ProviderRequestId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ProcessDurableAsync(Request(), Trust(), true, true, true, true, TestContext.Current.CancellationToken));
        Assert.Equal(1, submissions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationOrUnknownWithoutProviderIdRetainsOwnedBarrier(bool cancelAfterDispatch)
    {
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        GoogleGenerationPersistedQueueState? saved = null;
        int submissions = 0;
        var queue = new GoogleGenerationQueueCoordinator((_, _, _) =>
        {
            submissions++;
            Assert.True(saved?.UnresolvedSubmission);
            Assert.Null(saved!.ProviderRequestId);
            if (cancelAfterDispatch)
            {
                caller.Cancel();
                throw new OperationCanceledException(caller.Token);
            }
            return Task.FromResult(new GenerationProviderResponse(SubmissionDisposition.UnknownRequiresReconciliation,
                null, ReadOnlyMemory<byte>.Empty, null, null, "google-transport-timeout"));
        });
        var coordinator = new GoogleGenerationBoundQueueCoordinator(queue,
            (_, _, _, _) => Task.FromResult(saved), (_, _) => Task.CompletedTask,
            (claim, _) => { saved = claim; return Task.FromResult(true); },
            (outcome, token) => { Assert.False(token.IsCancellationRequested); saved = outcome; return Task.CompletedTask; });
        if (cancelAfterDispatch)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.ProcessDurableAsync(Request(), Trust(), true, true, true, true, caller.Token));
        else
            Assert.True((await coordinator.ProcessDurableAsync(Request(), Trust(), true, true, true, true, caller.Token)).RequiresReconciliation);
        Assert.Equal(1, submissions);
        Assert.True(saved!.UnresolvedSubmission);
        Assert.True(saved.SubmissionAttempted);
        Assert.NotNull(saved.LocalAttemptId);
        Assert.Null(saved.ProviderRequestId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ProcessDurableAsync(Request(), Trust(), true, true, true, true, TestContext.Current.CancellationToken));
        Assert.Equal(1, submissions);
    }

    [Fact]
    public async Task FailedClaimOrInvalidAdmissionNeverDispatches()
    {
        int submissions = 0, claims = 0;
        var queue = new GoogleGenerationQueueCoordinator((_, _, _) => { submissions++; throw new InvalidOperationException("must not dispatch"); });
        var coordinator = new GoogleGenerationBoundQueueCoordinator(queue,
            (_, _, _, _) => Task.FromResult<GoogleGenerationPersistedQueueState?>(null), (_, _) => Task.CompletedTask,
            (_, _) => { claims++; return Task.FromResult(false); }, (_, _) => Task.CompletedTask);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ProcessDurableAsync(Request(), Trust(), false, true, true, true, TestContext.Current.CancellationToken));
        Assert.Equal(0, claims);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ProcessDurableAsync(Request(), Trust(), true, true, true, true, TestContext.Current.CancellationToken));
        Assert.Equal(1, claims);
        Assert.Equal(0, submissions);
    }

    private static GenerationProviderRequest Request() => new("google", "synthesize", "account", "key", new byte[] { 1, 2, 3 }, "mp3");
    private static GenerationCacheTrustContext Trust() => new("google", "account", "project", "endpoint", "region", "synthesize",
        "model", "voice", "fingerprint", "plan", "cmn-CN", "default", "mp3", "sample", "adapter", "compiler", "ast", "normalization",
        "price", "capability", "governance", "feature", "account-capability");
}
