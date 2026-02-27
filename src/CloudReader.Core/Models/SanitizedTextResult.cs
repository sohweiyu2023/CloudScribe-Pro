namespace CloudReader.Core.Models;

public sealed record SanitizedTextResult(
    string SanitizedText,
    int RemovedSpanCount,
    int CharactersRemoved);
