namespace CloudReader.Core.Models;

public sealed class PreprocessOptions
{
    public bool SkipUrls { get; init; }
    public bool SkipRoundBrackets { get; init; }
    public bool SkipSquareBrackets { get; init; }
    public bool SkipCurlyBrackets { get; init; }
    public bool SkipMarkdownCodeBlocks { get; init; }
    public bool SkipQuoteLines { get; init; }
    public bool SkipFootnoteReferences { get; init; }
    public bool ExcludeInsideSsmlTags { get; init; }
    public IReadOnlyList<string> CustomRegexPatterns { get; init; } = [];
}
