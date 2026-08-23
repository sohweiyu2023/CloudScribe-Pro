namespace CloudScribe.Application.Generation;

public enum GenerationCacheLifecycleState
{
    Idle,
    Active,
    Pinned,
    Referenced,
    UnresolvedSubmission,
    Completed
}

public static class GenerationCacheProtectionPolicy
{
    public static GenerationCacheEntryProtection ForState(GenerationCacheLifecycleState state) => state switch
    {
        GenerationCacheLifecycleState.Idle => GenerationCacheEntryProtection.None,
        GenerationCacheLifecycleState.Active => GenerationCacheEntryProtection.Active,
        GenerationCacheLifecycleState.Pinned => GenerationCacheEntryProtection.Pinned,
        GenerationCacheLifecycleState.Referenced => GenerationCacheEntryProtection.Referenced,
        GenerationCacheLifecycleState.UnresolvedSubmission => GenerationCacheEntryProtection.UnresolvedSubmission,
        GenerationCacheLifecycleState.Completed => GenerationCacheEntryProtection.None,
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    };

    public static GenerationCacheEntryProtection Combine(
        bool active,
        bool pinned,
        bool referenced,
        bool unresolvedSubmission)
    {
        var result = GenerationCacheEntryProtection.None;
        if (active) result |= GenerationCacheEntryProtection.Active;
        if (pinned) result |= GenerationCacheEntryProtection.Pinned;
        if (referenced) result |= GenerationCacheEntryProtection.Referenced;
        if (unresolvedSubmission) result |= GenerationCacheEntryProtection.UnresolvedSubmission;
        return result;
    }

    public static bool MayEvict(GenerationCacheEntryProtection protection) =>
        protection == GenerationCacheEntryProtection.None;
}
