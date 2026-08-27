namespace CloudScribe.Domain.Generation;

public sealed record SpeechSpeakerChange(string SpeakerId) : SpeechPlanNode
{
    public string SpeakerId { get; init; } = Require(SpeakerId);

    private static string Require(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim();
    }
}
