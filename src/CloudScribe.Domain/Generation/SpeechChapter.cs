namespace CloudScribe.Domain.Generation;

public sealed record SpeechChapter(string ChapterId, string Title) : SpeechPlanNode
{
    public string ChapterId { get; init; } = Require(ChapterId, nameof(ChapterId));

    public string Title { get; init; } = Require(Title, nameof(Title));

    private static string Require(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }
}
