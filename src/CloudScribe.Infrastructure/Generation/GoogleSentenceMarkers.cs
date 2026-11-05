namespace CloudScribe.Infrastructure.Generation;

/// <summary>Explicit compiled input shape, not permission to reserve or submit.</summary>
public sealed record GoogleSentenceMarkers(int Version, IReadOnlyList<int> EndScalars);
