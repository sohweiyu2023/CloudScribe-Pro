using System.Globalization;
using System.Text;

namespace CloudScribe.Application.Documents;

/// <summary>
/// Omits explicit HTTP(S)/www destinations and conservative bare domains from a separate narration copy.
/// Non-URL text is retained verbatim; this does not normalize whitespace or edit the source.
/// </summary>
internal static partial class NarrationUrlOmissionV2
{
    /// <summary>Change this identifier whenever recognition or output semantics change.</summary>
    public const string CurrentVersion = "url-omission-v2";
    public const int MaximumSourceLength = 16 * 1024 * 1024;

    /// <summary>
    /// Performs bounded, local lexical recognition, not network or URI validation.
    /// Speakability means at least one Unicode letter or number, not a provider acceptance guarantee.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The input exceeds the shared processing budget. No partial transform is returned.
    /// </exception>
    public static NarrationUrlOmissionResult Transform(string sourceText) => Transform(sourceText, strictBareDomains: false, CurrentVersion);

    /// <summary>Shared engine. <paramref name="strictBareDomains"/> is false for v2 (unchanged, replayed for saved
    /// copies) and true for v3, which keeps sentence text that only looks like a bare domain.</summary>
    internal static NarrationUrlOmissionResult Transform(string sourceText, bool strictBareDomains, string version)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        if (sourceText.Length > MaximumSourceLength)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceText), sourceText.Length,
                $"URL omission is limited to {MaximumSourceLength} UTF-16 code units.");
        }

        List<(int Start, int End)> removals = [];
        int count = FindRemovals(sourceText, removals, strictBareDomains);
        string output = count == 0 ? sourceText : ApplyRemovals(sourceText, removals);
        return new(output, count, version, ContainsLetterOrNumber(output));
    }

    private static int FindRemovals(string source, List<(int Start, int End)> removals, bool strictBareDomains)
    {
        ProcessingBudget budget = new(source.Length);
        Stack<int> labelStarts = new();
        bool emailToken = false;
        bool bareTokenBlocked = false;
        int count = 0;
        int index = 0;
        while (index < source.Length)
        {
            char current = source[index];
            if (IsBareTokenSeparator(current)) bareTokenBlocked = false;
            emailToken = current == '@' || (emailToken && IsEmailTokenCharacter(current));
            if (current == '\\' && index + 1 < source.Length && source[index + 1] is '[' or ']' or '\\')
            {
                bareTokenBlocked = true;
                index += 2;
                continue;
            }

            if (current == '[')
            {
                labelStarts.Push(index);
            }
            else if (current == ']' && labelStarts.TryPop(out int labelStart)
                && TryReadMarkdownDestination(source, index + 1, budget, out int markdownEnd))
            {
                removals.Add((labelStart, labelStart + 1));
                removals.Add((index, markdownEnd));
                count++;
                index = markdownEnd;
                emailToken = false;
                continue;
            }

            if (TryReadExplicitUrl(source, index, emailToken, budget, out int examinedEnd, out int urlEnd))
            {
                if (urlEnd > index) count += AddRemoval(removals, index, urlEnd);
                index = urlEnd > index ? urlEnd : examinedEnd;
                emailToken = false;
                continue;
            }

            if (!bareTokenBlocked && char.IsAsciiLetterOrDigit(current) && IsBareStartBoundary(source, index))
            {
                examinedEnd = ReadBareUrl(source, index, budget, out int bareEnd);
                bareTokenBlocked = !AcceptsBare(source, index, bareEnd, strictBareDomains);
                if (!bareTokenBlocked) count += AddRemoval(removals, index, bareEnd);
                // Invalid tokens are consumed once; do not retry a domain-shaped tail.
                index = Math.Max(index + 1, examinedEnd);
                continue;
            }

            bareTokenBlocked |= IsEmailTokenCharacter(current) || current is '@' or '/' or '\\' or ':' or '=';
            index++;
        }

        return count;
    }

    // v2 accepts every bare destination it recognised; v3 additionally requires a confident domain.
    private static bool AcceptsBare(string source, int start, int end, bool strict) => end > start
        && (!strict || NarrationUrlOmissionV3.IsConfidentBareDestination(source.AsSpan(start, end - start)));

    private static int AddRemoval(List<(int Start, int End)> removals, int start, int end)
    {
        removals.Add((start, end));
        return 1;
    }

    private static bool TryReadExplicitUrl(string source, int index, bool emailToken, ProcessingBudget budget, out int examinedEnd, out int urlEnd)
    {
        examinedEnd = urlEnd = index;
        if (!TryGetPrefix(source, index, out int prefixLength, out bool www) || !IsStartBoundary(source, index, emailToken)) return false;
        examinedEnd = ReadUrl(source, index, prefixLength, www, budget, out urlEnd);
        return true;
    }

    private static bool TryReadMarkdownDestination(string source, int start, ProcessingBudget budget, out int end)
    {
        end = start;
        if (start >= source.Length || source[start] != '(')
        {
            return false;
        }

        int urlStart = SkipHorizontalWhitespace(source, start + 1, budget);
        bool angled = urlStart < source.Length && source[urlStart] == '<';
        if (angled)
        {
            urlStart++;
        }

        int urlEnd;
        if (TryGetPrefix(source, urlStart, out int prefixLength, out bool www))
        {
            ReadUrl(source, urlStart, prefixLength, www, budget, out urlEnd,
                trimSentenceSuffix: false, markdownEscapes: true);
        }
        else
        {
            ReadBareUrl(source, urlStart, budget, out urlEnd, trimSentenceSuffix: false);
        }
        if (urlEnd <= urlStart || (angled && (urlEnd >= source.Length || source[urlEnd] != '>')))
        {
            return false;
        }

        int afterUrl = angled ? urlEnd + 1 : urlEnd;
        int close = SkipHorizontalWhitespace(source, afterUrl, budget);
        if (close > afterUrl && close < source.Length && source[close] is '\'' or '"')
        {
            char quote = source[close++];
            while (close < source.Length && source[close] != quote && source[close] is not ('\r' or '\n'))
            {
                budget.Consume(2);
                close += source[close] == '\\' && close + 1 < source.Length ? 2 : 1;
            }

            if (close >= source.Length || source[close] != quote)
            {
                return false;
            }

            close = SkipHorizontalWhitespace(source, close + 1, budget);
        }

        if (close >= source.Length || source[close] != ')')
        {
            return false;
        }

        end = close + 1;
        return true;
    }

    private static int SkipHorizontalWhitespace(string source, int index, ProcessingBudget budget)
    {
        while (index < source.Length && source[index] is ' ' or '\t')
        {
            budget.Consume(1);
            index++;
        }

        return index;
    }

    private static bool TryGetPrefix(string source, int start, out int length, out bool www)
    {
        ReadOnlySpan<char> remaining = source.AsSpan(start);
        www = remaining.StartsWith("www.", StringComparison.OrdinalIgnoreCase);
        length = www ? 4
            : remaining.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? 8
            : remaining.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? 7 : 0;
        return length != 0;
    }

    private static bool IsStartBoundary(string source, int start, bool emailToken)
    {
        if (start == 0)
        {
            return true;
        }

        char last = source[start - 1];
        Rune previous;
        if (char.IsLowSurrogate(last) && start > 1)
        {
            if (!Rune.TryCreate(source[start - 2], last, out previous))
            {
                return false;
            }
        }
        else if (!Rune.TryCreate(last, out previous))
        {
            return false;
        }

        if (IsCjkLetter(previous))
        {
            return !emailToken;
        }

        return !Rune.IsLetterOrDigit(previous)
            && Rune.GetUnicodeCategory(previous) is not (UnicodeCategory.NonSpacingMark
                or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
            && previous.Value is not ('@' or '_' or '-' or '.' or '/' or '\\');
    }

    private static bool IsCjkLetter(Rune value) => Rune.IsLetter(value) && value.Value is
        >= 0x3040 and <= 0x30ff or >= 0x31f0 and <= 0x31ff
        or >= 0x3400 and <= 0x4dbf or >= 0x4e00 and <= 0x9fff
        or >= 0xf900 and <= 0xfaff or >= 0x20000 and <= 0x323af
        or >= 0x1100 and <= 0x11ff or >= 0x3130 and <= 0x318f or >= 0xac00 and <= 0xd7af;

    // Forward token state avoids repeatedly scanning long Unicode email domains backward.
    private static bool IsEmailTokenCharacter(char value) => char.IsLetterOrDigit(value) || char.IsSurrogate(value)
        || value is '_' or '-' or '.' or '+'
        || char.GetUnicodeCategory(value) is UnicodeCategory.NonSpacingMark
            or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;

    private static int ReadUrl(string source, int start, int prefixLength, bool www, ProcessingBudget budget, out int urlEnd,
        bool trimSentenceSuffix = true, bool markdownEscapes = false)
    {
        int end = start + prefixLength;
        int parentheses = 0;
        int brackets = 0;
        while (end < source.Length)
        {
            budget.Consume(1);
            char current = source[end];
            if (markdownEscapes && current == '\\' && end + 1 < source.Length && IsMarkdownEscapable(source[end + 1]))
            {
                // An escaped delimiter belongs to the destination, not its surrounding Markdown.
                budget.Consume(1);
                end += 2;
                continue;
            }

            if (IsUrlStop(current) || (current == ')' && parentheses == 0)
                || (!markdownEscapes && current == ']' && brackets == 0)
                || (!markdownEscapes && IsSentenceSuffix(current) && TryGetPrefix(source, end + 1, out _, out _))
                || (!markdownEscapes && current is ',' or ';' or '!' && HasBareDestinationAt(source, end + 1, budget)))
            {
                break;
            }

            parentheses += current == '(' ? 1 : current == ')' ? -1 : 0;
            brackets += current == '[' ? 1 : current == ']' ? -1 : 0;
            end++;
        }

        int candidateEnd = end;
        while (trimSentenceSuffix && candidateEnd > start + prefixLength && IsSentenceSuffix(source[candidateEnd - 1]))
        {
            budget.Consume(1);
            candidateEnd--;
        }

        // Reserve a conservative upper bound before authority scans, unescaping, or allocation.
        // This includes Contains, builder/string copies, and all host/port validation passes.
        budget.Consume((long)(candidateEnd - start) * (markdownEscapes ? 8 : 4));
        bool recognized = markdownEscapes
            ? IsRecognizableMarkdownAuthority(source, start, prefixLength, www, candidateEnd)
            : IsRecognizableAuthority(source, start, prefixLength, www, candidateEnd);
        urlEnd = recognized ? candidateEnd : start;
        return end;
    }

    private static bool IsMarkdownEscapable(char value) => value is >= '!' and <= '/'
        or >= ':' and <= '@' or >= '[' and <= '`' or >= '{' and <= '~';

    private static bool IsRecognizableMarkdownAuthority(string source, int start, int prefixLength, bool www, int end)
    {
        if (!source.AsSpan(start, end - start).Contains('\\'))
        {
            return IsRecognizableAuthority(source, start, prefixLength, www, end);
        }

        // Validate the same lexical authority after Markdown's ASCII-punctuation unescaping.
        // Keep original offsets for removal; decoded text is never emitted or persisted.
        StringBuilder unescaped = new(end - start);
        for (int index = start; index < end; index++)
        {
            if (source[index] == '\\' && index + 1 < end && IsMarkdownEscapable(source[index + 1]))
            {
                index++;
            }

            unescaped.Append(source[index]);
        }

        string destination = unescaped.ToString();
        return IsRecognizableAuthority(destination, 0, prefixLength, www, destination.Length);
    }

    private static bool IsUrlStop(char value) => char.IsWhiteSpace(value) || char.IsControl(value)
        || value is '<' or '>' or '\'' or '"' or '\\' or '{' or '}' or '`'
        or '\u2013' or '\u2014' or '\u2018' or '\u2019' or '\u201c' or '\u201d' or '\u2026'
        or '\u3001' or '\u3002' or '\u3008' or '\u3009' or '\u300a' or '\u300b'
        or '\u300c' or '\u300d' or '\u300e' or '\u300f' or '\u3010' or '\u3011'
        or '\uff01' or '\uff08' or '\uff09' or '\uff0c' or '\uff0e' or '\uff1a' or '\uff1b'
        or '\uff1f' or '\uff3b' or '\uff3d' or '\uff5b' or '\uff5d';

    private static bool IsSentenceSuffix(char value) => value is '.' or ',' or ';' or ':' or '!' or '?';

    private static bool IsRecognizableAuthority(string source, int start, int prefixLength, bool www, int end)
    {
        int hostStart = www ? start : start + prefixLength;
        int authorityEnd = hostStart;
        while (authorityEnd < end && source[authorityEnd] is not ('/' or '?' or '#'))
        {
            if (source[authorityEnd] == '@')
            {
                if (www || authorityEnd == hostStart)
                {
                    return false;
                }

                hostStart = authorityEnd + 1;
            }

            authorityEnd++;
        }

        if (hostStart >= authorityEnd)
        {
            return false;
        }

        if (source[hostStart] == '[')
        {
            return IsBracketedHost(source.AsSpan(hostStart, authorityEnd - hostStart));
        }

        int hostEnd = hostStart;
        while (hostEnd < authorityEnd && source[hostEnd] != ':')
        {
            hostEnd++;
        }

        return (!www || hostEnd > start + prefixLength)
            && IsDomain(source.AsSpan(hostStart, hostEnd - hostStart))
            && IsPort(source.AsSpan(hostEnd, authorityEnd - hostEnd));
    }

    private static bool IsBracketedHost(ReadOnlySpan<char> authority)
    {
        int closing = authority.IndexOf(']');
        if (closing < 2 || !authority[1..closing].Contains(':'))
        {
            return false;
        }

        foreach (char value in authority[1..closing])
        {
            if (!char.IsAsciiHexDigit(value) && value is not (':' or '.'))
            {
                return false;
            }
        }

        return IsPort(authority[(closing + 1)..]);
    }

    private static bool IsDomain(ReadOnlySpan<char> host)
    {
        bool labelStart = true;
        char previous = '\0';
        foreach (Rune rune in host.EnumerateRunes())
        {
            if (rune.Value == '.')
            {
                if (labelStart || previous == '-')
                {
                    return false;
                }

                labelStart = true;
            }
            else
            {
                bool mark = Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark;
                if ((!Rune.IsLetterOrDigit(rune) && !mark && rune.Value != '-') || (labelStart && (mark || rune.Value == '-')))
                {
                    return false;
                }

                labelStart = false;
            }

            previous = rune.IsAscii ? (char)rune.Value : '\0';
        }

        return !labelStart && previous != '-';
    }

    private static bool IsPort(ReadOnlySpan<char> port)
    {
        if (port.IsEmpty)
        {
            return true;
        }

        if (port[0] != ':' || port.Length is < 2 or > 6)
        {
            return false;
        }

        int value = 0;
        foreach (char digit in port[1..])
        {
            if (!char.IsAsciiDigit(digit))
            {
                return false;
            }

            value = (value * 10) + digit - '0';
        }

        return value <= 65535;
    }

    private static string ApplyRemovals(string source, List<(int Start, int End)> removals)
    {
        // Opening Markdown brackets may precede URLs already removed inside their labels.
        // Sorting ranges avoids repeated string edits and quadratic copying for nested labels.
        removals.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        StringBuilder output = new(source.Length);
        int copiedThrough = 0;
        foreach ((int start, int end) in removals)
        {
            if (start > copiedThrough)
            {
                output.Append(source, copiedThrough, start - copiedThrough);
            }

            copiedThrough = Math.Max(copiedThrough, end);
        }

        output.Append(source, copiedThrough, source.Length - copiedThrough);
        return output.ToString();
    }

    private static bool ContainsLetterOrNumber(string value)
    {
        foreach (Rune rune in value.EnumerateRunes())
        {
            if (Rune.IsLetter(rune) || Rune.IsNumber(rune))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class ProcessingBudget
    {
        private long _remaining;

        public ProcessingBudget(int sourceLength)
        {
            // One shared, input-proportional allowance prevents repeated malformed Markdown
            // lookahead from turning into quadratic scanning or cumulative allocations.
            _remaining = (16L * sourceLength) + 1024;
        }

        public void Consume(long units)
        {
            if (units > _remaining)
            {
                throw new InvalidOperationException(
                    "URL omission could not prepare this text within its safe processing limit. "
                    + "Simplify malformed Markdown links and try again.");
            }

            _remaining -= units;
        }
    }
}
