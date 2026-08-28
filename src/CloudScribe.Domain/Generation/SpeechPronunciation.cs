namespace CloudScribe.Domain.Generation;

public sealed record SpeechPronunciation(string Text, string Alphabet, string Phonemes) : SpeechPlanNode
{
    public string Text { get; init; } = Require(Text, nameof(Text));

    public string Alphabet { get; init; } = Require(Alphabet, nameof(Alphabet));

    public string Phonemes { get; init; } = Require(Phonemes, nameof(Phonemes));

    private static string Require(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }
}
