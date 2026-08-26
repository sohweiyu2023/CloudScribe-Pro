namespace CloudScribe.Domain.Generation;

public sealed record SpeechEmphasis(SpeechEmphasisLevel Level) : SpeechPlanNode
{
    public SpeechEmphasisLevel Level { get; init; } = Enum.IsDefined(Level)
        ? Level
        : throw new ArgumentOutOfRangeException(nameof(Level));
}
