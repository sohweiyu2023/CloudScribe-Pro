namespace CloudScribe.Application.Generation;

public sealed record GenerationCollectionReleaseDecision(
    Guid CollectionId,
    int RequestRevision,
    string PricingProvenanceId,
    IReadOnlyList<GenerationProofResult> ProofResults,
    IReadOnlyList<OutputReservation> OutputReservations,
    DateTimeOffset EvaluatedAtUtc)
{
    public bool IsReleaseSafe => ProofResults.All(static result => result.IsReleaseSafe);
}
