using CloudScribe.Application.Generation;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Separate segment receipt type. Never written through the legacy whole-document receipt recorder.</summary>
public sealed record ChirpSegmentAcceptedAudio(string PlanSha256, int CoverageOrdinal,
    ChirpNarrationRange Range, ArticleAcceptedAudio Audio);
