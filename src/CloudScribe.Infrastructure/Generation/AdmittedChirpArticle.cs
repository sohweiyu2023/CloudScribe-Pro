using CloudScribe.Application.Generation;

namespace CloudScribe.Infrastructure.Generation;

public sealed record AdmittedChirpArticle(ChirpArticlePlanProposal Proposal, ChirpPlanLineage Lineage,
    IReadOnlyList<ChirpReusedArticleAudio> ReusedAudio, IReadOnlyList<ChirpSegmentAllowance> AllowedSegments);
