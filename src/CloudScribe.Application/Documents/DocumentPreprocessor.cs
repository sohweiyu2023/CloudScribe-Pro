using System.Text;

namespace CloudScribe.Application.Documents;

public sealed record DocumentPreprocessingOptions(
    bool NormalizeLineEndings = true,
    bool CollapseHorizontalWhitespace = false,
    bool CollapseExcessBlankLines = true,
    bool SimplifyUrls = false);

public sealed record DocumentSourceMapSegment(
    int OutputStart,
    int OutputLength,
    int SourceStart,
    int SourceLength,
    string Transform);

public sealed record DocumentPreprocessingPreview(
    string SourceText,
    string OutputText,
    IReadOnlyList<DocumentSourceMapSegment> SourceMap,
    IReadOnlyList<string> Warnings);

public sealed class DocumentPreprocessor
{
    private const int MaxCharacters = 16 * 1024 * 1024;

    public DocumentPreprocessingPreview Preview(
        string sourceText,
        DocumentPreprocessingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        if (sourceText.Length > MaxCharacters)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sourceText),
                sourceText.Length,
                $"Preprocessing preview is limited to {MaxCharacters:N0} UTF-16 code units.");
        }

        DocumentPreprocessingOptions effective = options ?? new();
        StringBuilder output = new(sourceText.Length);
        List<DocumentSourceMapSegment> map = [];
        List<string> warnings = [];
        int sourceIndex = 0;
        int consecutiveNewlines = 0;
        while (sourceIndex < sourceText.Length)
        {
            if (effective.SimplifyUrls && TryReadUrl(sourceText, sourceIndex, out int urlLength, out string? replacement))
            {
                AppendMapped(output, map, replacement!, sourceIndex, urlLength, "url-simplified");
                sourceIndex += urlLength;
                consecutiveNewlines = 0;
                continue;
            }

            char current = sourceText[sourceIndex];
            if (current is '\r' or '\n')
            {
                int newlineLength = current == '\r'
                    && sourceIndex + 1 < sourceText.Length
                    && sourceText[sourceIndex + 1] == '\n'
                    ? 2
                    : 1;
                consecutiveNewlines++;
                if (!effective.CollapseExcessBlankLines || consecutiveNewlines <= 2)
                {
                    string newline = effective.NormalizeLineEndings ? "\n" : sourceText.Substring(sourceIndex, newlineLength);
                    AppendMapped(output, map, newline, sourceIndex, newlineLength, effective.NormalizeLineEndings ? "line-ending" : "identity");
                }

                sourceIndex += newlineLength;
                continue;
            }

            consecutiveNewlines = 0;
            if (effective.CollapseHorizontalWhitespace && current is ' ' or '\t')
            {
                int start = sourceIndex;
                while (sourceIndex < sourceText.Length && sourceText[sourceIndex] is ' ' or '\t')
                {
                    sourceIndex++;
                }

                AppendMapped(output, map, " ", start, sourceIndex - start, "horizontal-whitespace");
                continue;
            }

            int scalarLength = char.IsHighSurrogate(current)
                && sourceIndex + 1 < sourceText.Length
                && char.IsLowSurrogate(sourceText[sourceIndex + 1])
                ? 2
                : 1;
            AppendMapped(output, map, sourceText.Substring(sourceIndex, scalarLength), sourceIndex, scalarLength, "identity");
            sourceIndex += scalarLength;
        }

        if (effective.SimplifyUrls)
        {
            warnings.Add("URL simplification changes spoken text; the source map preserves the original URL span for review.");
        }

        return new(sourceText, output.ToString(), map, warnings);
    }

    private static bool TryReadUrl(
        string source,
        int start,
        out int length,
        out string? replacement)
    {
        length = 0;
        replacement = null;
        bool looksLikeUrl = source.AsSpan(start).StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || source.AsSpan(start).StartsWith("http://", StringComparison.OrdinalIgnoreCase);
        if (!looksLikeUrl)
        {
            return false;
        }

        int end = start;
        while (end < source.Length && !char.IsWhiteSpace(source[end]))
        {
            end++;
        }

        string candidate = source[start..end].TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '}');
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri)
            || uri.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            return false;
        }

        length = candidate.Length;
        replacement = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? uri.Host[4..]
            : uri.Host;
        return true;
    }

    private static void AppendMapped(
        StringBuilder output,
        List<DocumentSourceMapSegment> map,
        string value,
        int sourceStart,
        int sourceLength,
        string transform)
    {
        if (value.Length == 0)
        {
            return;
        }

        int outputStart = output.Length;
        output.Append(value);
        if (map.Count > 0)
        {
            DocumentSourceMapSegment previous = map[^1];
            bool merge = string.Equals(previous.Transform, transform, StringComparison.Ordinal)
                && previous.OutputStart + previous.OutputLength == outputStart
                && previous.SourceStart + previous.SourceLength == sourceStart
                && previous.OutputLength == previous.SourceLength
                && value.Length == sourceLength;
            if (merge)
            {
                map[^1] = previous with
                {
                    OutputLength = previous.OutputLength + value.Length,
                    SourceLength = previous.SourceLength + sourceLength,
                };
                return;
            }
        }

        map.Add(new(outputStart, value.Length, sourceStart, sourceLength, transform));
    }
}
