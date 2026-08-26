namespace CloudScribe.Infrastructure.Generation;

public sealed record GenerationCacheClearResult(int EntriesRemoved, int EntriesProtected, long BytesRemoved);
