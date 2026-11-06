using CloudScribe.Application.Generation;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Verified constituent audio, not a joined final article or billing settlement.</summary>
public sealed record GoogleLongAudioRecoveryOutput(ChirpNarrationRange Range, GoogleLongAudioVerifiedArtifact Artifact);
