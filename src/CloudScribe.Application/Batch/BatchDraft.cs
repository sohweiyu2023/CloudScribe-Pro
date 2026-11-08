using CloudScribe.Application.Batch.Authority;
using CloudScribe.Application.Documents;

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
    DateTimeOffset UpdatedUtc,
    bool SkipUrls = false,
    string? UrlTransformVersion = null)
{
    public const int CurrentVersion = 1;

    public static BatchDraft Empty(DateTimeOffset now) =>
        new(CurrentVersion, null, null, false, BatchEngines.LongAudioFirst, BatchFailurePolicy.SkipAndContinue, [], now, UrlTransformVersion: NarrationUrlOmission.CurrentVersion);

    /// <summary>Old enabled drafts retain v1; previously disabled, unprepared drafts select current when enabled.</summary>
    public string ResolveUrlTransformVersion()
    {
        string? inferred = UrlTransformVersion;
        foreach (var item in Items)
        {
            if (item.NarrationSource is not { } source) continue;
            if (!SkipUrls || !NarrationUrlOmission.IsSupportedVersion(source.TransformVersion)
                || inferred is not null && !string.Equals(inferred, source.TransformVersion, StringComparison.Ordinal))
                throw new InvalidOperationException("The draft mixes URL skipping versions or options. Its saved copies were not changed.");
            inferred = source.TransformVersion;
        }
        inferred ??= SkipUrls ? NarrationUrlOmission.LegacyVersion : NarrationUrlOmission.CurrentVersion;
        if (!NarrationUrlOmission.IsSupportedVersion(inferred))
            throw new InvalidOperationException("The draft's URL skipping version is unsupported. Its saved text was not changed.");
        return inferred;
    }

    public bool HasFiles => Items.Count > 0;
}
