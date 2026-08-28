namespace CloudScribe.Application.Generation;

public sealed record VoiceLabAuditionOutcome(
    bool CacheHit,
    ReadOnlyMemory<byte> MediaBytes,
    string DiagnosticCode);
