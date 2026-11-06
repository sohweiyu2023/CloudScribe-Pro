using CloudScribe.Application.Generation;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>An inert exact source range and deterministic request. Independent admission still applies.</summary>
public sealed record GoogleLongAudioRecoveryPart(ChirpNarrationRange Range, GoogleLongAudioRequest Request, long MaximumHoldCents);
