using CloudScribe.Application.Generation;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Verified exact coverage by real accepted child responses; does not assert a provider accepted the failed parent.</summary>
public sealed record ChirpRecoveredArticleCoverage(string PlanSha256, int CoverageOrdinal,
    ChirpNarrationRange Range, IReadOnlyList<ChirpSegmentAcceptedAudio> Leaves);
