using System.Text;
using System.Text.RegularExpressions;
using CloudReader.Core.Models;

namespace CloudReader.Core.Processing;

public sealed class TextSanitizer
{
    public string Sanitize(string input, PreprocessOptions options)
    {
        var text = input;
        if (options.SkipMarkdownCodeBlocks)
        {
            text = Regex.Replace(text, "```[\\s\\S]*?```", " ", RegexOptions.Multiline);
        }

        if (options.SkipQuoteLines)
        {
            text = string.Join('\n', text.Split('\n').Where(line => !line.TrimStart().StartsWith("> ", StringComparison.Ordinal)));
        }

        if (options.SkipUrls)
        {
            text = Regex.Replace(text, @"(https?://\S+|www\.\S+|\b[\w.-]+\.[a-z]{2,}\b)", " [link omitted] ", RegexOptions.IgnoreCase);
        }

        if (options.SkipRoundBrackets) text = RemoveNested(text, '(', ')');
        if (options.SkipSquareBrackets) text = RemoveNested(text, '[', ']');
        if (options.SkipCurlyBrackets) text = RemoveNested(text, '{', '}');

        if (options.SkipFootnoteReferences)
        {
            text = Regex.Replace(text, @"(\[\d+\]|\(\d+\))", " ");
        }

        foreach (var pattern in options.CustomRegexPatterns)
        {
            text = Regex.Replace(text, pattern, " ");
        }

        return Regex.Replace(text, "\\s+", " ").Trim();
    }

    private static string RemoveNested(string text, char open, char close)
    {
        var output = new List<System.Text.Rune>();
        var depth = 0;

        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value == open) { depth++; continue; }
            if (rune.Value == close && depth > 0) { depth--; continue; }
            if (depth == 0) output.Add(rune);
        }

        return string.Concat(output.Select(static r => r.ToString()));
    }
}
