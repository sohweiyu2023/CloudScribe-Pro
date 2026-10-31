using CloudScribe.Application.Generation;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Offline proposal only. Does not replace an immutable plan, approve a retry or change financial state.</summary>
public sealed record ChirpFailedRangeSubdivision(string Version, string ParentPlanSha256, int ParentCoverageOrdinal,
    ChirpNarrationRange ParentRange, string ParentPayloadSha256, int ParentPayloadBytes,
    string LanguageCode, string VoiceName, string SubdivisionSha256, IReadOnlyList<ChirpArticlePlanPart> Parts);
