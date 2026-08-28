namespace CloudScribe.Application.Documents;

public sealed record DocumentSourceMapSegment(
    int OutputStart,
    int OutputLength,
    int SourceStart,
    int SourceLength,
    string Transform);
