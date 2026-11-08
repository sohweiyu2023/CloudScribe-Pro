namespace CloudScribe.Application.Batch;

/// <summary>One input file and its single output MP3. Article text is never stored here; the library holds it.
/// <see cref="SameTextAs"/> is the order number of an earlier file with identical text: shown as a note, never excluded.
/// <see cref="Engine"/> is the engine chosen for this file when the batch was approved; it never changes after a job starts.</summary>
public sealed record BatchItem(
    Guid ItemId,
    int Order,
    string SourceFileName,
    string SourceSha256,
    long SourceBytes,
    int Characters,
    string OutputFileName,
    BatchItemState State,
    BatchExclusion Exclusion = BatchExclusion.None,
    Guid? DocumentId = null,
    Guid? RevisionId = null,
    Guid? JobId = null,
    string? ReasonCode = null,
    int Attempts = 0,
    long EstimatedMaximumMinorUnits = 0,
    long ReservedMinorUnits = 0,
    string? OutputPath = null,
    long OutputBytes = 0,
    string? OutputSha256 = null,
    DateTimeOffset? StartedUtc = null,
    DateTimeOffset? FinishedUtc = null,
    long ActiveMilliseconds = 0,
    int? SameTextAs = null,
    string? Engine = null)
{
    public bool IsIncluded => State is not BatchItemState.Excluded and not BatchItemState.Skipped;

    public bool IsTerminal => State is BatchItemState.Completed or BatchItemState.Failed or BatchItemState.Uncertain
        or BatchItemState.Excluded or BatchItemState.Skipped;
}
