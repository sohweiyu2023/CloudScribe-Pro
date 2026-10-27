namespace CloudScribe.Application.Generation;

public sealed record ReviewedHistoricalAudioAnchor(string EvidenceId, ReadOnlyMemory<byte> AnchorBytes,
    string IndependentlyAdmittedSha256, DateTimeOffset ExpiresAtUtc);
