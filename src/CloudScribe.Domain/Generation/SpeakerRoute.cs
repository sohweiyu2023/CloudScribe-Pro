namespace CloudScribe.Domain.Generation;

public sealed record SpeakerRoute(string SpeakerId, string ProviderStableId, string VoiceStableId, bool IsFallback);
