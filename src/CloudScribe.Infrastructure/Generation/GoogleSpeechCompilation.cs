using CloudScribe.Domain.Generation;

namespace CloudScribe.Infrastructure.Generation;

public sealed record GoogleSpeechCompilation(
    ReadOnlyMemory<byte> Payload,
    IReadOnlyList<SpeechDegradation> Degradations,
    string PayloadSha256);
