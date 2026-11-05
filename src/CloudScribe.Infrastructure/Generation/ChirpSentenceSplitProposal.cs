namespace CloudScribe.Infrastructure.Generation;

public sealed record ChirpSentenceSplitProposal(Guid GrantId, Guid ParentAuthorizationId,
    IReadOnlyList<ChirpSentenceSplitPart> Parts, int ChildDepth, int ChildAttemptsAfterReservation,
    long ReservedMinorUnitsAfterReservation)
{
    public bool GrantsAuthority { get; }
}
