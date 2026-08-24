using System.Reflection;
using CloudScribe.Application.Generation;

namespace CloudScribe.Application.Tests;

public sealed class Stage6GoogleUiSingleFlightTests
{
    [Fact]
    public async Task Concurrent_ui_generation_is_rejected_before_validation_or_submit()
    {
        var submitCalls = 0;
        var queue = new GoogleGenerationQueueCoordinator((_, _, _) =>
        {
            submitCalls++;
            throw new InvalidOperationException("Single-flight rejection was bypassed.");
        });
        var coordinator = new GoogleGenerationUiQueueCoordinator(new GoogleGenerationBoundQueueCoordinator(queue));
        var field = typeof(GoogleGenerationUiQueueCoordinator).GetField("_queueInFlight", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Google UI single-flight field was not found.");
        field.SetValue(coordinator, 1);

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ProcessPersistedTransitionAsync(
            null!, false, false, false, false,
            null!, null!, null!, null!,
            GoogleGenerationReconciliationResolutionEvidence.None,
            false, false, false, false));

        Assert.Equal(0, submitCalls);
    }
}
