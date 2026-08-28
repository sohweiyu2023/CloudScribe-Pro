using System.Net;
using System.Text;

namespace CloudScribe.Infrastructure.Files;

internal static class BoundedHtmlTextExtractor
{
    public static string Extract(string html, int maxCharacters)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCharacters);
        StringBuilder output = new(Math.Min(html.Length, maxCharacters));
        int index = 0;
        while (index < html.Length)
        {
            index = html[index] == '<'
                ? ProcessTag(html, index, output)
                : ProcessText(html, index, output);
            if (output.Length > maxCharacters)
            {
                throw new InvalidDataException("HTML extracted text exceeds the configured text limit.");
            }
        }

        return NormalizeWhitespace(output.ToString());
    }

    private static int ProcessText(string html, int index, StringBuilder output)
    {
        int nextTag = html.IndexOf('<', index);
        if (nextTag < 0)
        {
            nextTag = html.Length;
        }

        if (nextTag > index)
        {
            output.Append(WebUtility.HtmlDecode(html[index..nextTag]));
        }

        return nextTag;
    }

    private static int ProcessTag(string html, int index, StringBuilder output)
    {
        int tagEnd = html.IndexOf('>', index + 1);
        if (tagEnd < 0)
        {
            output.Append(WebUtility.HtmlDecode(html[index..]));
            return html.Length;
        }

        ReadOnlySpan<char> tag = html.AsSpan(index + 1, tagEnd - index - 1).Trim();
        string tagName = ReadTagName(tag);
        bool closing = tag.Length > 0 && tag[0] == '/';
        if (!closing && IsDiscardedContainer(tagName))
        {
            return SkipContainer(html, tagEnd + 1, tagName);
        }

        if (IsLineBreakTag(tagName))
        {
            AppendNewline(output);
        }

        return tagEnd + 1;
    }

    private static int SkipContainer(string html, int start, string tagName)
    {
        string closingTag = "</" + tagName;
        int closeStart = html.IndexOf(closingTag, start, StringComparison.OrdinalIgnoreCase);
        if (closeStart < 0)
        {
            return html.Length;
        }

        int closeEnd = html.IndexOf('>', closeStart + closingTag.Length);
        return closeEnd < 0 ? html.Length : closeEnd + 1;
    }

    private static string ReadTagName(ReadOnlySpan<char> tag)
    {
        if (tag.Length > 0 && tag[0] == '/')
        {
            tag = tag[1..].TrimStart();
        }

        int length = 0;
        while (length < tag.Length && (char.IsLetterOrDigit(tag[length]) || tag[length] is ':' or '-'))
        {
            length++;
        }

        return tag[..length].ToString().ToLowerInvariant();
    }

    private static bool IsDiscardedContainer(string tagName) => tagName is
        "script" or "style" or "noscript" or "template" or "svg" or "math";

    private static bool IsLineBreakTag(string tagName) => tagName is
        "br" or "p" or "div" or "li" or "tr" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "blockquote" or "pre";

    private static void AppendNewline(StringBuilder output)
    {
        if (output.Length > 0 && output[^1] != '\n')
        {
            output.Append('\n');
        }
    }

    private static string NormalizeWhitespace(string value)
    {
        string normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        StringBuilder output = new(normalized.Length);
        int blankLines = 0;
        foreach (string rawLine in normalized.Split('\n'))
        {
            string line = rawLine.TrimEnd();
            if (line.Length == 0)
            {
                blankLines++;
                if (blankLines > 1)
                {
                    continue;
                }
            }
            else
            {
                blankLines = 0;
            }

            if (output.Length > 0)
            {
                output.Append('\n');
            }

            output.Append(line);
        }

        return output.ToString().Trim();
    }
}
