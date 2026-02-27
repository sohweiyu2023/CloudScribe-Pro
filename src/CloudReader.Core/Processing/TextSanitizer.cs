using System.Text;
using System.Text.RegularExpressions;
using CloudReader.Core.Models;

namespace CloudReader.Core.Processing;

public sealed class TextSanitizer
{
    public string Sanitize(string input, PreprocessOptions options) => SanitizeWithReport(input, options).SanitizedText;

    public SanitizedTextResult SanitizeWithReport(string input, PreprocessOptions options)
    {
        var working = input;
        var removedSpans = 0;

        if (options.ExcludeInsideSsmlTags)
        {
            working = ApplyOutsideSsml(working, segment => SanitizePlain(segment, options, ref removedSpans));
        }
        else
        {
            working = SanitizePlain(working, options, ref removedSpans);
        }

        var cleaned = NormalizeWhitespacePreservingParagraphs(working).Trim();
        var charsRemoved = Math.Max(0, input.Length - cleaned.Length);
        return new SanitizedTextResult(cleaned, removedSpans, charsRemoved);
    }

    private static string SanitizePlain(string text, PreprocessOptions options, ref int removedSpans)
    {
        text = Replace(text, options.SkipMarkdownCodeBlocks, "```[\\s\\S]*?```", " ", RegexOptions.Multiline, ref removedSpans);

        if (options.SkipQuoteLines)
        {
            var originalLineCount = text.Split('\n').Length;
            text = string.Join('\n', text.Split('\n').Where(line => !line.TrimStart().StartsWith("> ", StringComparison.Ordinal)));
            removedSpans += Math.Max(0, originalLineCount - text.Split('\n').Length);
        }

        text = Replace(text, options.SkipUrls, @"(https?://\S+|www\.\S+|\b[\w.-]+\.[a-z]{2,}\b)", " link omitted ", RegexOptions.IgnoreCase, ref removedSpans);

        if (options.SkipRoundBrackets) text = RemoveNested(text, '(', ')', ref removedSpans);
        if (options.SkipSquareBrackets) text = RemoveNested(text, '[', ']', ref removedSpans);
        if (options.SkipCurlyBrackets) text = RemoveNested(text, '{', '}', ref removedSpans);

        text = Replace(text, options.SkipFootnoteReferences, @"(\[\d+\]|\(\d+\))", " ", RegexOptions.None, ref removedSpans);

        foreach (var pattern in options.CustomRegexPatterns)
        {
            text = Replace(text, true, pattern, " ", RegexOptions.None, ref removedSpans);
        }

        return text;
    }

    private static string Replace(string input, bool enabled, string pattern, string replacement, RegexOptions options, ref int removedSpans)
    {
        if (!enabled) return input;

        var regex = new Regex(pattern, options);
        var matches = regex.Matches(input);
        if (matches.Count == 0) return input;

        removedSpans += matches.Count;
        return regex.Replace(input, replacement);
    }

    private static string ApplyOutsideSsml(string input, Func<string, string> sanitizeOutside)
    {
        // Protect simple SSML/XML regions so skip rules are only applied to true text content.
        var pattern = new Regex(@"(<[^>]+>[^<]*</[^>]+>|<[^>]+>)", RegexOptions.Singleline);
        var output = new StringBuilder(input.Length);
        var cursor = 0;

        foreach (Match match in pattern.Matches(input))
        {
            if (match.Index > cursor)
            {
                var outside = input[cursor..match.Index];
                output.Append(sanitizeOutside(outside));
            }

            output.Append(match.Value);
            cursor = match.Index + match.Length;
        }

        if (cursor < input.Length)
        {
            output.Append(sanitizeOutside(input[cursor..]));
        }

        return output.ToString();
    }

    private static string NormalizeWhitespacePreservingParagraphs(string input)
    {
        var normalized = input.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var collapsedInnerWhitespace = Regex.Replace(normalized, @"[^\S\n]+", " ");
        collapsedInnerWhitespace = Regex.Replace(collapsedInnerWhitespace, @"\n{3,}", "\n\n");
        collapsedInnerWhitespace = Regex.Replace(collapsedInnerWhitespace, @" ?\n ?", "\n");
        return collapsedInnerWhitespace;
    }

    private static string RemoveNested(string text, char open, char close, ref int removedSpans)
    {
        var output = new List<Rune>();
        var depth = 0;

        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value == open)
            {
                depth++;
                if (depth == 1) removedSpans++;
                continue;
            }

            if (rune.Value == close && depth > 0)
            {
                depth--;
                continue;
            }

            if (depth == 0) output.Add(rune);
        }

        return string.Concat(output.Select(static r => r.ToString()));
    }
}
