namespace CloudScribe.Providers.Abstractions;

internal static class ProviderIdentifierRules
{
    public static string NormalizeStableId(string value, string parameterName, int maximumLength = 64)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        string normalized = value.Trim();
        if (normalized.Length > maximumLength || !IsCanonicalStableId(normalized))
        {
            throw new ArgumentException(
                $"Stable ID must be 1-{maximumLength} lowercase ASCII letters, digits, periods or hyphens, and must start and end with a letter or digit.",
                parameterName);
        }
        return normalized;
    }

    public static string NormalizeDisplayName(string value, string parameterName, int maximumLength = 80)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        string normalized = value.Trim();
        if (normalized.Length > maximumLength || normalized.Any(static character =>
            char.IsControl(character)
            || char.IsSurrogate(character)
            || char.GetUnicodeCategory(character) == System.Globalization.UnicodeCategory.Format))
        {
            throw new ArgumentException($"Display text must be 1-{maximumLength} visible characters.", parameterName);
        }
        return normalized;
    }

    private static bool IsCanonicalStableId(string value)
    {
        if (value.Length == 0 || !IsAsciiLetterOrDigit(value[0]) || !IsAsciiLetterOrDigit(value[^1]))
        {
            return false;
        }
        return value.All(static character => IsAsciiLetterOrDigit(character) || character is '-' or '.');
    }

    private static bool IsAsciiLetterOrDigit(char value) => value is >= 'a' and <= 'z' or >= '0' and <= '9';
}
