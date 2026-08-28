namespace CloudScribe.Application.Generation;

public sealed record GenerationCacheTrimResult(long BytesBefore, long BytesAfter, int EntriesEvicted, int EntriesProtected);
