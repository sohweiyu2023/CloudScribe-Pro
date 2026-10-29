namespace CloudScribe.Application.Generation;

/// <summary>Historical coverage supplied by an independent current-source/queue resolver, never by titles or ordinals alone.</summary>
public sealed record ChirpHistoricalRange(Guid DocumentId, Guid RevisionId, int StartScalar, int EndScalarExclusive,
    string TextSha256, IReadOnlyList<ChirpHistoricalAttempt> Attempts);
