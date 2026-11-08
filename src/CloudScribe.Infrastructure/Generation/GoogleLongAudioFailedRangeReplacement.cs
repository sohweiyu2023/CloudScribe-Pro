namespace CloudScribe.Infrastructure.Generation;

/// <summary>Independent, exact one-outcome approval. It does not classify an error as retryable or approve a replacement chain.</summary>
public sealed record GoogleLongAudioFailedRangeReplacement(string RootBindingSha256, Guid FailedAttemptId,
    string FailedBindingSha256, string FailedCheckpointSha256, int StartScalar, int EndScalarExclusive,
    string SourceSha256, Guid ReplacementAttemptId, string ReplacementOutputGcsUri, string ReplacementBindingSha256,
    long MaximumUsdCents);
