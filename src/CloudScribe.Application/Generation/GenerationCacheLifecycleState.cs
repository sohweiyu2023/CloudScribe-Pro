namespace CloudScribe.Application.Generation;

public enum GenerationCacheLifecycleState
{
    Idle,
    Active,
    Pinned,
    Referenced,
    UnresolvedSubmission,
    Completed,
}
