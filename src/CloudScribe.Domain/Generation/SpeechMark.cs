namespace CloudScribe.Domain.Generation;

public sealed record SpeechMark(string Name) : SpeechPlanNode
{
    public string Name { get; init; } = Require(Name);

    private static string Require(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim();
    }
}
