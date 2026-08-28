using System.Text.RegularExpressions;

namespace CloudScribe.Infrastructure.Diagnostics;

public static partial class DiagnosticRedactor
{
    private const int MaximumFieldLength = 1024;
    private const int MaximumInputLength = 4096;
    private const int RegexTimeoutMilliseconds = 1000;
    private const RegexOptions SafeRegexOptions = RegexOptions.CultureInvariant
        | RegexOptions.ExplicitCapture
        | RegexOptions.NonBacktracking;
    private const RegexOptions SafeIgnoreCaseRegexOptions = SafeRegexOptions | RegexOptions.IgnoreCase;

    public static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        string boundedValue = TruncateUtf16Safely(value, MaximumInputLength, out bool inputWasTruncated);
        string sanitized = ControlCharacters().Replace(boundedValue, " ");
        sanitized = AuthorizationHeader().Replace(sanitized, "${name}=[REDACTED]");
        sanitized = SecretAssignment().Replace(sanitized, "${name}=[REDACTED]");
        sanitized = BearerToken().Replace(sanitized, "Bearer [REDACTED]");
        sanitized = UriSensitiveSuffix().Replace(sanitized, "${base}[REDACTED-URI-SUFFIX]");
        sanitized = EmailAddress().Replace(sanitized, "[EMAIL]");
        sanitized = FileUri().Replace(sanitized, "file://[PATH]");
        sanitized = WindowsUserPath().Replace(sanitized, "${prefix}\\[USER]\\");
        sanitized = MacUserPath().Replace(sanitized, "/Users/[USER]/");
        sanitized = UnixHomePath().Replace(sanitized, "/home/[USER]/");
        sanitized = TruncateUtf16Safely(sanitized, MaximumFieldLength, out bool outputWasTruncated);
        return inputWasTruncated || outputWasTruncated ? sanitized + "…" : sanitized;
    }

    private static string TruncateUtf16Safely(string value, int maximumCodeUnits, out bool wasTruncated)
    {
        if (value.Length <= maximumCodeUnits)
        {
            wasTruncated = false;
            return value;
        }

        int length = maximumCodeUnits;
        if (length > 0
            && char.IsHighSurrogate(value[length - 1])
            && length < value.Length
            && char.IsLowSurrogate(value[length]))
        {
            length--;
        }

        wasTruncated = true;
        return value[..length];
    }

    [GeneratedRegex("[\\u0000-\\u001F\\u007F]+", SafeRegexOptions, RegexTimeoutMilliseconds)]
    private static partial Regex ControlCharacters();

    [GeneratedRegex("(?<name>authorization)\\s*[:=]\\s*(?:bearer\\s+)?[^,;\\s]+", SafeIgnoreCaseRegexOptions, RegexTimeoutMilliseconds)]
    private static partial Regex AuthorizationHeader();

    [GeneratedRegex("(?<name>api[_-]?key|token|secret|password)\\s*[:=]\\s*[^,;\\s]+", SafeIgnoreCaseRegexOptions, RegexTimeoutMilliseconds)]
    private static partial Regex SecretAssignment();

    [GeneratedRegex("Bearer\\s+[A-Za-z0-9._~+\\-/]+=*", SafeIgnoreCaseRegexOptions, RegexTimeoutMilliseconds)]
    private static partial Regex BearerToken();

    [GeneratedRegex("(?<base>https?://[^\\s?#]+)[?#][^\\s]*", SafeIgnoreCaseRegexOptions, RegexTimeoutMilliseconds)]
    private static partial Regex UriSensitiveSuffix();

    [GeneratedRegex("[A-Z0-9._%+\\-]+@[A-Z0-9.\\-]+\\.[A-Z]{2,}", SafeIgnoreCaseRegexOptions, RegexTimeoutMilliseconds)]
    private static partial Regex EmailAddress();

    [GeneratedRegex("file://[^\\s]+", SafeIgnoreCaseRegexOptions, RegexTimeoutMilliseconds)]
    private static partial Regex FileUri();

    [GeneratedRegex(@"(?<prefix>[A-Z]:\\Users)\\[^\\]+\\", SafeIgnoreCaseRegexOptions, RegexTimeoutMilliseconds)]
    private static partial Regex WindowsUserPath();

    [GeneratedRegex("/Users/[^/]+/", SafeRegexOptions, RegexTimeoutMilliseconds)]
    private static partial Regex MacUserPath();

    [GeneratedRegex("/home/[^/]+/", SafeRegexOptions, RegexTimeoutMilliseconds)]
    private static partial Regex UnixHomePath();
}
