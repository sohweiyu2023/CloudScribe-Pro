using CloudScribe.Application.Documents;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Read-only current source slice; no approval or provider authority is carried by this selection.</summary>
public sealed record ChirpResolvedSegment(DocumentSnapshot Document, ChirpArticlePlanProposal Proposal,
    ChirpArticlePlanPart Part, string ExactText);
