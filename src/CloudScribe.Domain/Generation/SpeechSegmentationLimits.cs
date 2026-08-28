namespace CloudScribe.Domain.Generation;

public sealed record SpeechSegmentationLimits(int MaximumTextElements, int MaximumCompiledPayloadCharacters)
{
    public int MaximumTextElements { get; init; } = MaximumTextElements > 0
        ? MaximumTextElements
        : throw new ArgumentOutOfRangeException(nameof(MaximumTextElements));

    public int MaximumCompiledPayloadCharacters { get; init; } = MaximumCompiledPayloadCharacters > 0
        ? MaximumCompiledPayloadCharacters
        : throw new ArgumentOutOfRangeException(nameof(MaximumCompiledPayloadCharacters));
}
