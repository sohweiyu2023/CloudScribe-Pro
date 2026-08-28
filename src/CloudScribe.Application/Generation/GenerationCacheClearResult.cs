namespace CloudScribe.Application.Generation;

public sealed record GenerationCacheClearResult(int EntriesRemoved, int EntriesProtected, long BytesRemoved);
