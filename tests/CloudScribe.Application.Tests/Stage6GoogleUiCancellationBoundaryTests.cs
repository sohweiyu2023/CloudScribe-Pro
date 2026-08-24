using CloudScribe.Application.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Application.Tests;

public sealed class Stage6GoogleUiCancellationBoundaryTests
{
    [Fact]
    public async Task Pre_cancelled_UI_generation_never_reaches_queue_or_submit()
    {
        var submitCalls = 0;
        var queue = new GoogleGenerationQueueCoordinator((_, _, _) =>
        {
            submitCalls++;
            throw new InvalidOperationException("Cancelled UI generation reached provider submit.");
        });
        var coordinator = new GoogleGenerationUiQueueCoordinator(new GoogleGenerationBoundQueueCoordinator(queue));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.ProcessPersistedTransitionAsync(
            null!, false, false, false, false,
            null!, null!, null!, null!,
            GoogleGenerationReconciliationResolutionEvidence.None,
            false, false, false, false,
            cancellation.Token));

        Assert.Equal(0, submitCalls);
    }
}
