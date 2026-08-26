namespace CloudScribe.Domain.Safety;

public sealed record CacheExportDecision(bool IncludeCache, string Reason);
