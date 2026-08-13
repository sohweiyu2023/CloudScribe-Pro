using System.Text;

namespace CloudScribe.Application.Documents;

internal sealed class DocumentPreprocessorEngine
{
    private readonly int _maxCharacters = 16 * 1024 * 1024;

    public DocumentPreprocessingPreview Preview(
        string sourceText,
        DocumentPreprocessingOptions? options)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        if (sourceText.Length > _maxCharacters)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sourceText),
                sourceText.Length,
                $"Preprocessing preview is limited to {_maxCharacters:N0} UTF-16 code units.");
        }

        DocumentPreprocessingOptions effective = options ?? new();
        (string outputText, List<DocumentSourceMapSegment> sourceMap) = Transform(sourceText, effective);
        List<string> warnings = [];
        if (effective.SimplifyUrls)
        {
            warnings.Add("URL simplification changes spoken text; the source map preserves the original URL span for review.");
        }

        return new(sourceText, outputText, sourceMap, warnings);
    }

    private static (string OutputText, List<DocumentSourceMapSegment> SourceMap) Transform(
        string sourceText,
        DocumentPreprocessingOptions options)
    {
        StringBuilder output = new(sourceText.Length);
        List<DocumentSourceMapSegment> map = [];
        int sourceIndex = 0;
        int consecutiveNewlines = 0;
        while (sourceIndex < sourceText.Length)
        {
            if (TryAppendUrl(sourceText, options, output, map, ref sourceIndex, ref consecutiveNewlines)
                || TryAppendNewline(sourceText, options, output, map, ref sourceIndex, ref consecutiveNewlines)
                || TryAppendWhitespace(sourceText, options, output, map, ref sourceIndex, ref consecutiveNewlines))
            {
                continue;
            }

            AppendScalar(sourceText, output, map, ref sourceIndex, ref consecutiveNewlines);
        }

        return (output.ToString(), map);
    }

    private static bool TryAppendUrl(
        string source,
        DocumentPreprocessingOptions options,
        StringBuilder output,
        List<DocumentSourceMapSegment> map,
        ref int sourceIndex,
        ref int consecutiveNewlines)
    {
        if (!options.SimplifyUrls
            || !TryReadUrl(source, sourceIndex, out int urlLength, out string? replacement))
        {
            return false;
        }

        AppendMapped(output, map, replacement!, sourceIndex, urlLength, "url-simplified");
        sourceIndex += urlLength;
        consecutiveNewlines = 0;
        return true;
    }

    private static bool TryAppendNewline(
        string source,
        DocumentPreprocessingOptions options,
        StringBuilder output,
        List<DocumentSourceMapSegment> map,
        ref int sourceIndex,
        ref int consecutiveNewlines)
    {
        char current = source[sourceIndex];
        if (current is not ('\r' or '\n'))
        {
            return false;
        }

        int newlineLength = current == '\r'
            && sourceIndex + 1 < source.Length
            && source[sourceIndex + 1] == '\n'
            ? 2
            : 1;
        consecutiveNewlines++;
        if (!options.CollapseExcessBlankLines || consecutiveNewlines <= 2)
        {
            string newline = options.NormalizeLineEndings ? "\n" : source.Substring(sourceIndex, newlineLength);
            string transform = options.NormalizeLineEndings ? "line-ending" : "identity";
            AppendMapped(output, map, newline, sourceIndex, newlineLength, transform);
        }

        sourceIndex += newlineLength;
        return true;
    }

    private static bool TryAppendWhitespace(
        string source,
        DocumentPreprocessingOptions options,
        StringBuilder output,
        List<DocumentSourceMapSegment> map,
        ref int sourceIndex,
        ref int consecutiveNewlines)
    {
        char current = source[sourceIndex];
        if (!options.CollapseHorizontalWhitespace || current is not (' ' or '\t'))
        {
            return false;
        }

        int start = sourceIndex;
        while (sourceIndex < source.Length && source[sourceIndex] is ' ' or '\t')
        {
            sourceIndex++;
        }

        consecutiveNewlines = 0;
        AppendMapped(output, map, " ", start, sourceIndex - start, "horizontal-whitespace");
        return true;
    }

    private static void AppendScalar(
        string source,
        StringBuilder output,
        List<DocumentSourceMapSegment> map,
        ref int sourceIndex,
        ref int consecutiveNewlines)
    {
        char current = source[sourceIndex];
        int scalarLength = char.IsHighSurrogate(current)
            && sourceIndex + 1 < source.Length
            && char.IsLowSurrogate(source[sourceIndex + 1])
            ? 2
            : 1;
        AppendMapped(output, map, source.Substring(sourceIndex, scalarLength), sourceIndex, scalarLength, "identity");
        sourceIndex += scalarLength;
        consecutiveNewlines = 0;
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
            || (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
                && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
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
        if (CanMerge(map, outputStart, sourceStart, sourceLength, value.Length, transform))
        {
            DocumentSourceMapSegment previous = map[^1];
            map[^1] = previous with
            {
                OutputLength = previous.OutputLength + value.Length,
                SourceLength = previous.SourceLength + sourceLength,
            };
            return;
        }

        map.Add(new(outputStart, value.Length, sourceStart, sourceLength, transform));
    }

    private static bool CanMerge(
        List<DocumentSourceMapSegment> map,
        int outputStart,
        int sourceStart,
        int sourceLength,
        int valueLength,
        string transform)
    {
        if (map.Count == 0)
        {
            return false;
        }

        DocumentSourceMapSegment previous = map[^1];
        return string.Equals(previous.Transform, transform, StringComparison.Ordinal)
            && previous.OutputStart + previous.OutputLength == outputStart
            && previous.SourceStart + previous.SourceLength == sourceStart
            && previous.OutputLength == previous.SourceLength
            && valueLength == sourceLength;
    }
}
