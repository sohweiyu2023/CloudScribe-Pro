namespace CloudScribe.Application.Batch.Authority;

/// <summary>Rolling-window commitments for ceilings. Uses the larger of each file's held amount and its approved
/// maximum while it is unfinished, so an in-flight file can never be under-counted.</summary>
public static class BatchSpendLedger
{
    /// <summary>Commitments of other batches inside the window: finished files count their holds; started, unfinished
    /// files count the larger of their hold and approved maximum; approved files that have not started count their
    /// approved maximum while their approval is still valid (they may start at any time).</summary>
    public static long Committed(IEnumerable<NarrationBatch> batches, IEnumerable<BatchConsent> consents, DateTimeOffset now, int windowDays,
        Guid? excludeBatchId = null)
    {
        ArgumentNullException.ThrowIfNull(batches);
        ArgumentNullException.ThrowIfNull(consents);
        var start = now - TimeSpan.FromDays(windowDays);
        var active = consents.Where(c => c.BatchId != excludeBatchId).ToArray();
        var maxima = active.SelectMany(c => c.Items).GroupBy(x => x.ItemId).ToDictionary(g => g.Key, g => g.Max(x => x.MaximumMinorUnits));
        var pendingApproval = active.Where(c => now < c.ExpiresUtc).SelectMany(c => c.Items).Select(x => x.ItemId).ToHashSet();
        long total = 0;
        foreach (var batch in batches.Where(b => b.BatchId != excludeBatchId))
        {
            foreach (var item in batch.Items)
            {
                long approved = maxima.GetValueOrDefault(item.ItemId);
                if (item.JobId is not null)
                {
                    if ((item.StartedUtc ?? now) < start) continue;
                    total = checked(total + (item.State is BatchItemState.Completed ? item.ReservedMinorUnits : Math.Max(item.ReservedMinorUnits, approved)));
                }
                else if (item.State == BatchItemState.Ready && batch.State != BatchQueueState.Finished && pendingApproval.Contains(item.ItemId))
                    total = checked(total + approved);
            }
        }
        return total;
    }
}
