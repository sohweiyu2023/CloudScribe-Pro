namespace CloudScribe.Application.Documents;

internal static partial class NarrationUrlOmissionV2
{
    // Hand-maintained v2 policy, not a DNS/PSL snapshot. These literals and limits are immutable.
    // Future additions require a new transform version; never fetch suffixes at runtime.
    private const int MaximumBareHostLength = 253;

    private static bool IsBareStartBoundary(string source, int start) => start == 0
        || char.IsWhiteSpace(source[start - 1])
        || source[start - 1] is '(' or '[' or '{' or '<' or '"' or '\'' or '`'
            or ',' or ';' or '!' or '\u2018' or '\u2019' or '\u201c' or '\u201d'
            or '\u3001' or '\u3002' or '\u3008' or '\u300a' or '\u300c' or '\u3010'
            or '\uff08' or '\uff0c' or '\uff1b' or '\uff3b';

    private static bool IsBareTokenSeparator(char value) => char.IsWhiteSpace(value)
        || value is ',' or ';' or '!' or '"' or '\'' or '`'
            or '\u2018' or '\u2019' or '\u201c' or '\u201d' or '\u3001' or '\u3002'
            or '\uff0c' or '\uff1b';

    private static int ReadBareUrl(string source, int start, ProcessingBudget budget, out int urlEnd, bool trimSentenceSuffix = true)
    {
        int end = start;
        int parentheses = 0;
        int brackets = 0;
        bool path = false;
        while (end < source.Length)
        {
            budget.Consume(1);
            char current = source[end];
            if (IsUrlStop(current) || current is ',' or ';' or '!'
                || current == '(' && !path || current == ')' && parentheses == 0
                || current == '[' && !path || current == ']' && brackets == 0) break;
            path |= current is '/' or '?' or '#';
            parentheses += current == '(' ? 1 : current == ')' ? -1 : 0;
            brackets += current == '[' ? 1 : current == ']' ? -1 : 0;
            end++;
        }

        int candidateEnd = end;
        while (trimSentenceSuffix && candidateEnd > start && IsSentenceSuffix(source[candidateEnd - 1]))
        {
            budget.Consume(1);
            candidateEnd--;
        }

        int length = candidateEnd - start;
        budget.Consume((long)length * 3);
        urlEnd = length > 0
            && parentheses == 0 && brackets == 0
            && (end == source.Length || source[end] != '\\')
            && !(end - candidateEnd > 1 && source.AsSpan(candidateEnd, end - candidateEnd).Contains("..", StringComparison.Ordinal))
            && IsBareDestination(source.AsSpan(start, length)) ? candidateEnd : start;
        return end;
    }

    private static bool HasBareDestinationAt(string source, int start, ProcessingBudget budget)
    {
        if (start >= source.Length || !char.IsAsciiLetterOrDigit(source[start])) return false;
        ReadBareUrl(source, start, budget, out int end);
        return end > start;
    }

    private static bool IsBareDestination(ReadOnlySpan<char> destination)
    {
        // Unicode neighbors, Unicode paths, userinfo, escapes and non-URL punctuation are deliberately
        // preserved as a complete candidate. ASCII percent-encoded paths are supported.
        foreach (char value in destination)
        {
            if (!char.IsAscii(value) || value is '@' or '\\' or '|' or '^' or '$') return false;
        }

        int authorityEnd = destination.IndexOfAny('/', '?', '#');
        if (authorityEnd < 0) authorityEnd = destination.Length;
        ReadOnlySpan<char> authority = destination[..authorityEnd];
        int colon = authority.IndexOf(':');
        ReadOnlySpan<char> host = colon < 0 ? authority : authority[..colon];
        if (!IsBareHost(host) || colon >= 0 && !IsPort(authority[colon..])) return false;
        // Reject malformed percent escapes rather than silently omitting a prefix of a local token.
        for (int i = authorityEnd; i < destination.Length; i++)
        {
            if (destination[i] != '%') continue;
            if (i + 2 >= destination.Length || !char.IsAsciiHexDigit(destination[i + 1]) || !char.IsAsciiHexDigit(destination[i + 2])) return false;
            i += 2;
        }
        return true;
    }

    private static bool IsBareHost(ReadOnlySpan<char> host)
    {
        if (host.Length is < 4 or > MaximumBareHostLength) return false;
        int labelLength = 0;
        int lastDot = -1;
        for (int i = 0; i < host.Length; i++)
        {
            char value = host[i];
            if (value == '.')
            {
                if (labelLength == 0 || host[i - 1] == '-') return false;
                labelLength = 0;
                lastDot = i;
            }
            else
            {
                if (!char.IsAsciiLetterOrDigit(value) && value != '-' || labelLength == 0 && value == '-' || ++labelLength > 63) return false;
            }
        }
        if (labelLength == 0 || host[^1] == '-' || lastDot < 1) return false;
        // Punycode is intentionally outside v2's conservative bare-domain policy.
        if (host.StartsWith("xn--", StringComparison.OrdinalIgnoreCase) || host.Contains(".xn--", StringComparison.OrdinalIgnoreCase)) return false;
        ReadOnlySpan<char> suffix = host[(lastDot + 1)..];
        if (!IsBareSuffix(suffix)) return false;
        // These recognized multi-label public suffixes need an additional registrant label.
        return !host.Equals("co.uk", StringComparison.OrdinalIgnoreCase)
            && !host.Equals("org.uk", StringComparison.OrdinalIgnoreCase)
            && !host.Equals("ac.uk", StringComparison.OrdinalIgnoreCase)
            && !host.Equals("gov.uk", StringComparison.OrdinalIgnoreCase)
            && !host.Equals("com.au", StringComparison.OrdinalIgnoreCase)
            && !host.Equals("net.au", StringComparison.OrdinalIgnoreCase)
            && !host.Equals("org.au", StringComparison.OrdinalIgnoreCase)
            && !host.Equals("co.nz", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBareSuffix(ReadOnlySpan<char> suffix) => suffix.Equals("com", StringComparison.OrdinalIgnoreCase)
        || suffix.Equals("org", StringComparison.OrdinalIgnoreCase) || suffix.Equals("net", StringComparison.OrdinalIgnoreCase)
        || suffix.Equals("edu", StringComparison.OrdinalIgnoreCase) || suffix.Equals("gov", StringComparison.OrdinalIgnoreCase)
        || suffix.Equals("co", StringComparison.OrdinalIgnoreCase) || suffix.Equals("io", StringComparison.OrdinalIgnoreCase)
        || suffix.Equals("ai", StringComparison.OrdinalIgnoreCase) || suffix.Equals("dev", StringComparison.OrdinalIgnoreCase)
        || suffix.Equals("app", StringComparison.OrdinalIgnoreCase) || suffix.Equals("info", StringComparison.OrdinalIgnoreCase)
        || suffix.Equals("biz", StringComparison.OrdinalIgnoreCase) || suffix.Equals("uk", StringComparison.OrdinalIgnoreCase)
        || suffix.Equals("us", StringComparison.OrdinalIgnoreCase) || suffix.Equals("ca", StringComparison.OrdinalIgnoreCase)
        || suffix.Equals("au", StringComparison.OrdinalIgnoreCase) || suffix.Equals("nz", StringComparison.OrdinalIgnoreCase)
        || suffix.Equals("de", StringComparison.OrdinalIgnoreCase) || suffix.Equals("fr", StringComparison.OrdinalIgnoreCase)
        || suffix.Equals("jp", StringComparison.OrdinalIgnoreCase) || suffix.Equals("cn", StringComparison.OrdinalIgnoreCase)
        || suffix.Equals("in", StringComparison.OrdinalIgnoreCase) || suffix.Equals("sg", StringComparison.OrdinalIgnoreCase)
        || suffix.Equals("hk", StringComparison.OrdinalIgnoreCase) || suffix.Equals("tw", StringComparison.OrdinalIgnoreCase);
}
