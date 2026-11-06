namespace CloudScribe.Infrastructure.Generation;

/// <summary>Safe progress projection; a verified file is deliberately distinct from financial settlement.</summary>
public sealed record GoogleLongAudioJobProgress(GoogleLongAudioCheckpoint Checkpoint, GoogleLongAudioVerifiedArtifact? Artifact);
