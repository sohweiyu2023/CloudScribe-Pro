#pragma warning disable MA0004 // Test code has no synchronization context to preserve.
using CloudScribe.Application.Batch;
using Microsoft.Extensions.Logging;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class BatchNarrationTests
{
    [Fact]
    public async Task FailedOutputVerificationLogsTheFinalPersistedReason()
    {
        using var w = new Workspace();
        var batch = await w.CreateAsync(["a.txt"]);
        var narrator = new FakeNarrator();
        narrator.MissingOutputs.Add("a.mp3");
        var log = new ListLogger();
        var result = await new BatchQueueRunner(w.Store, narrator, TimeProvider.System, log)
            .RunAsync(batch.BatchId, Settings.Sha256(), cancellationToken: Token);
        Assert.Equal("batch.output-not-verified", result.Items[0].ReasonCode);
        Assert.Contains(log.Messages, message => message.Contains("stopped as Failed (batch.output-not-verified)", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Messages, message => message.Contains("batch.item-complete", StringComparison.Ordinal));
        Assert.Single(narrator.Calls);
    }

    [Fact]
    public async Task NewAndResumedItemsLogTheirDurablySavedJobBeforeNarrationContinues()
    {
        using var w = new Workspace();
        var batch = await w.CreateAsync(["a.txt", "b.txt"]);
        var savedJob = Guid.NewGuid();
        batch = await w.Store.SaveAsync(batch.WithItem(batch.Items[0] with { State = BatchItemState.Running, JobId = savedJob })
            with { State = BatchQueueState.Running }, Token);
        var log = new ListLogger();
        var narrator = new FakeNarrator();
        foreach (var item in batch.Items)
            narrator.Behaviour[item.Order] = async (current, token) =>
            {
                var saved = (await w.Store.LoadAsync(batch.BatchId, token))!.Items.Single(x => x.ItemId == current.ItemId);
                Assert.NotNull(saved.JobId);
                string expected = $"Batch {batch.BatchId} item {current.ItemId} at order {current.Order} is linked to saved job {saved.JobId}.";
                Assert.Single(log.Messages, message => message.StartsWith(expected, StringComparison.Ordinal));
                return new(BatchOutcomeKind.Uncertain, "batch.long-audio-needs-reconciliation");
            };
        var result = await new BatchQueueRunner(w.Store, narrator, TimeProvider.System, log)
            .RunAsync(batch.BatchId, Settings.Sha256(), cancellationToken: Token);
        Assert.Equal(savedJob, result.Items[0].JobId);
        Assert.Equal(1, narrator.RecordJobCalls);
        Assert.Equal(2, narrator.Calls.Count);
        Assert.Equal(2, log.Messages.Count(message => message.Contains("is linked to saved job", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoggingFailureCannotChangeBatchCompletionOrCauseAnotherRequest(bool failAtEnabledCheck)
    {
        using var w = new Workspace();
        var batch = await w.CreateAsync(["a.txt"]);
        var narrator = new FakeNarrator();
        var runner = new BatchQueueRunner(w.Store, narrator, TimeProvider.System, new ThrowingBatchLogger(failAtEnabledCheck));
        var result = await runner.RunAsync(batch.BatchId, Settings.Sha256(), cancellationToken: Token);
        Assert.Equal(BatchQueueState.Finished, result.State);
        Assert.Equal(1, result.CompletedCount);
        Assert.Equal(BatchQueueState.Finished, (await w.Store.LoadAsync(batch.BatchId, Token))!.State);
        await runner.RunAsync(batch.BatchId, Settings.Sha256(), cancellationToken: Token);
        Assert.Single(narrator.Calls);
        Assert.Equal(1, narrator.RecordJobCalls);
    }

    private sealed class ThrowingBatchLogger(bool failAtEnabledCheck) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => failAtEnabledCheck ? throw new IOException("Synthetic observer failure") : true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => throw new IOException("Synthetic observer failure");
    }
}
