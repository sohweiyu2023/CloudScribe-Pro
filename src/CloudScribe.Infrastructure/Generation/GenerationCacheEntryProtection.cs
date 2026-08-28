namespace CloudScribe.Infrastructure.Generation;

[Flags]
public enum GenerationCacheEntryProtection
{
    None = 0,
    Active = 1,
    Pinned = 2,
    Referenced = 4,
    UnresolvedSubmission = 8,
}
