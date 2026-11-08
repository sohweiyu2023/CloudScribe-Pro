namespace CloudScribe.Application.Batch;

/// <summary>A durable batch: ordered files, one settings pin, one output folder, timing and cost records.
/// <see cref="Revision"/> increases with every save so two windows can never overwrite each other's progress.</summary>
public sealed record NarrationBatch(
    int Version,
    Guid BatchId,
    string Name,
    long Revision,
    BatchQueueState State,
    BatchSettings Settings,
    string SettingsSha256,
    string OutputFolder,
    string Currency,
    int CurrencyScale,
    IReadOnlyList<BatchItem> Items,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? FirstStartedUtc = null,
    DateTimeOffset? FinishedUtc = null,
    long ActiveMilliseconds = 0,
    string? ReasonCode = null,
    bool NumberTracks = false)
{
    public const int CurrentVersion = 1;

    public IEnumerable<BatchItem> Included => Items.Where(x => x.IsIncluded);

    public int CompletedCount => Items.Count(x => x.State == BatchItemState.Completed);

    public int IncludedCount => Items.Count(x => x.IsIncluded);

    public long EstimatedMaximumMinorUnits => Included.Sum(x => x.EstimatedMaximumMinorUnits);

    public long ReservedMinorUnits => Items.Sum(x => x.ReservedMinorUnits);

    public NarrationBatch WithItem(BatchItem item) =>
        this with { Items = Items.Select(x => x.ItemId == item.ItemId ? item : x).ToArray() };
}
