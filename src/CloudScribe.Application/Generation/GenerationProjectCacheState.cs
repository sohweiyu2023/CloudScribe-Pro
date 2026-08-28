namespace CloudScribe.Application.Generation;

public sealed record GenerationProjectCacheState(
    bool Active,
    bool Pinned,
    bool Referenced,
    bool UnresolvedSubmission);
