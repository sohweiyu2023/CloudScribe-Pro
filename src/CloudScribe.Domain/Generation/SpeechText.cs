namespace CloudScribe.Domain.Generation;

public sealed record SpeechText(string Text) : SpeechPlanNode
{
    public string Text { get; init; } = RequireText(Text);

    private static string RequireText(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        return text;
    }
}
