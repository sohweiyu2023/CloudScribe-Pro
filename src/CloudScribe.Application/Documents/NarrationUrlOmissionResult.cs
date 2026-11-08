namespace CloudScribe.Application.Documents;

/// <summary>An immutable, local narration transform result, not authorization or synthesis evidence.</summary>
public sealed record NarrationUrlOmissionResult(
    string OutputText,
    int OmittedUrlCount,
    string TransformVersion,
    bool HasSpeakableContent);
