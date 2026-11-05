namespace CloudScribe.Infrastructure.Generation;

internal sealed record PrivateArticlePersistenceEvidence(Guid? JobId, int? Ordinal, string PayloadSha256, bool Saved,
    bool CompleteError);
