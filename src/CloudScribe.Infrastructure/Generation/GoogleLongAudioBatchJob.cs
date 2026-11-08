namespace CloudScribe.Infrastructure.Generation;

/// <summary>A Long Audio job admitted by a batch approval rather than by the installed scope. Its request binding covers
/// the exact saved text, attempt and output object; <see cref="FamilyMaximumUsdCents"/> bounds the root request plus any
/// adaptive split requests for the same file.</summary>
public sealed record GoogleLongAudioBatchJob(
    Guid BatchId,
    Guid ItemId,
    Guid DocumentId,
    Guid RevisionId,
    string SourceSha256,
    Guid AttemptId,
    string OutputGcsUri,
    long MaximumOutputBytes,
    int ExpectedSampleRate,
    int ExpectedChannels,
    string RequestBindingSha256,
    long CeilingUsdCents,
    long FamilyMaximumUsdCents,
    bool AdaptiveRecovery,
    bool Committed = false,
    string? NarrationTextSha256 = null)
{
    public GoogleLongAudioDocumentJob ToDocumentJob() =>
        new(DocumentId, RevisionId, NarrationTextSha256 ?? SourceSha256, AttemptId, OutputGcsUri, "text", MaximumOutputBytes, ExpectedSampleRate, ExpectedChannels);
}
