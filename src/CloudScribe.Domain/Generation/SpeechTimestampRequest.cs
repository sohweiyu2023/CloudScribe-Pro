namespace CloudScribe.Domain.Generation;

public sealed record SpeechTimestampRequest(string MarkName) : SpeechPlanNode
{
    public string MarkName { get; init; } = Require(MarkName);

    private static string Require(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim();
    }
}
