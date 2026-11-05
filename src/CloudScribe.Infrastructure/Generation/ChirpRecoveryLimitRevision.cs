namespace CloudScribe.Infrastructure.Generation;

/// <summary>Explicit reviewed increase of the same cumulative recovery grant, never a reset of its used requests or spending.</summary>
public sealed record ChirpRecoveryLimitRevision(int Version, string PreviousGrantSha256, long MaximumProjectMinorUnits);
