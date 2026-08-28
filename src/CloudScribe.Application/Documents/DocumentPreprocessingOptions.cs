namespace CloudScribe.Application.Documents;

public sealed record DocumentPreprocessingOptions(bool NormalizeLineEndings = true, bool CollapseHorizontalWhitespace = false, bool CollapseExcessBlankLines = true, bool SimplifyUrls = false);
