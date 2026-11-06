namespace CloudScribe.Infrastructure.Generation;

/// <summary>Private provenance for a verified recovery constituent, not a document's completed MP3 receipt.</summary>
public sealed record GoogleLongAudioRecoveryArtifactReceipt(int Version, Guid RootAttemptId, string RootBindingSha256,
    GoogleLongAudioVerifiedArtifact Artifact);
