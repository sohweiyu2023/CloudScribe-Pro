using CloudScribe.Application.Batch.Authority;

namespace CloudScribe.Application.Batch;

/// <summary>A batch that is being prepared and has not been saved to the library yet. It is kept on disk after every
/// change, so closing the window, opening another batch or a crash never loses the arrangement. It holds file names,
/// paths, sizes and SHA-256 hashes, never the text: the text is read again (and must still match) when the draft is saved.</summary>
public sealed record BatchDraft(
    int Version,
    string? Name,
    string? OutputFolder,
    bool NumberTracks,
    string Engine,
    BatchFailurePolicy FailurePolicy,
    IReadOnlyList<BatchItem> Items,
    DateTimeOffset UpdatedUtc)
{
    public const int CurrentVersion = 1;

    public static BatchDraft Empty(DateTimeOffset now) =>
        new(CurrentVersion, null, null, false, BatchEngines.LongAudioFirst, BatchFailurePolicy.SkipAndContinue, [], now);

    public bool HasFiles => Items.Count > 0;
}
