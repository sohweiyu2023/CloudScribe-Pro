namespace CloudScribe.Infrastructure.Generation;

/// <summary>Exact immutable predecessor; it grants no paid authority or automatic retry.</summary>
public sealed record ChirpSentencePlanSuccessor(ChirpArticlePlanProposal Parent);
