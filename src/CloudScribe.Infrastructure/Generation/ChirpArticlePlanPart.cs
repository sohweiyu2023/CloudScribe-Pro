using CloudScribe.Application.Generation;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>An internal coverage identity, deliberately separate from a user document ID.</summary>
public sealed record ChirpArticlePlanPart(int CoverageOrdinal, ChirpNarrationRange Range,
    string? PayloadSha256, int PayloadBytes);
