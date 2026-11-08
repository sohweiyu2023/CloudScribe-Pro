namespace CloudScribe.Application.Batch;

/// <summary>One input file and its single output MP3. Article text is never stored here; the library holds it.
/// <see cref="SameTextAs"/> is the order number of an earlier file with identical text: shown as a note, never excluded.
/// <see cref="Engine"/> is the engine chosen for this file when the batch was approved; it never changes after a job starts.
/// <see cref="SourcePath"/> is where the TXT was read from (when the system gave a local path), so an unsaved draft or an
/// interrupted import can read it again; the file's SHA-256 must still match. <see cref="UncertainOutcomes"/> counts runs
/// whose request may have reached Google without a confirmed result: it never goes down, so a possible earlier charge stays
/// visible even after replacement audio is verified.</summary>
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
    string? Engine = null,
    string? SourcePath = null,
    int UncertainOutcomes = 0)
{
    public bool IsIncluded => State is not BatchItemState.Excluded and not BatchItemState.Skipped;

    public bool IsTerminal => State is BatchItemState.Completed or BatchItemState.Failed or BatchItemState.Uncertain
        or BatchItemState.Excluded or BatchItemState.Skipped;
}
