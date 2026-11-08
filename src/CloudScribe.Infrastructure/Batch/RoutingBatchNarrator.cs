using CloudScribe.Application.Batch;
using CloudScribe.Application.Batch.Authority;

namespace CloudScribe.Infrastructure.Batch;

/// <summary>Sends each file to the engine fixed in its approval. Authority is checked once for the batch by the gate.</summary>
public sealed class RoutingBatchNarrator(BatchAuthorityGate gate, Func<(string AccountStableId, string ProjectStableId)> identity,
    IReadOnlyDictionary<string, IBatchNarrator> engines) : IBatchNarrator
{
    public async Task<(bool Authorized, string Explanation)> CheckAuthorityAsync(NarrationBatch queue, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(queue);
        var (account, project) = identity();
        var decision = await gate.CheckRunAsync(queue, account, project, cancellationToken).ConfigureAwait(false);
        return (decision.Allowed, decision.Explanation);
    }

    public async Task<BatchNarrationOutcome> NarrateAsync(NarrationBatch queue, BatchItem item, Func<Guid, Task> recordJob,
        IProgress<BatchItemProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(item);
        var (account, project) = identity();
        var authority = await gate.CheckItemAsync(queue, item, account, project, cancellationToken).ConfigureAwait(false);
        if (!authority.Allowed) return new(BatchOutcomeKind.NotAuthorized,
            authority.ReasonCode is "batch.narration-changed" or "batch.narration-validation-unavailable" or "batch.narration-version"
                ? authority.ReasonCode : "batch.not-authorized");
        var consent = await gate.LoadConsentAsync(queue.BatchId, cancellationToken).ConfigureAwait(false);
        string? engine = consent?.Items.SingleOrDefault(x => x.ItemId == item.ItemId)?.Engine;
        if (engine is null || !engines.TryGetValue(engine, out var narrator))
            return new(BatchOutcomeKind.NotAuthorized, "batch.file-not-approved");
        if (item.JobId is not null && item.Engine is { } started && !string.Equals(started, engine, StringComparison.Ordinal))
            return new(BatchOutcomeKind.Uncertain, "batch.engine-changed-after-start"); // Never switch engines once a job exists.
        return await narrator.NarrateAsync(queue, item, recordJob, progress, cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> VerifyOutputAsync(BatchItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        return BatchOutputFiles.MatchesAsync(item.OutputPath, item.OutputSha256, item.OutputBytes, cancellationToken);
    }
}
