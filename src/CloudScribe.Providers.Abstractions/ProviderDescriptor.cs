namespace CloudScribe.Providers.Abstractions;

public sealed record ProviderDescriptor
{
    private const int MaximumStableIdLength = 64;
    private const int MaximumDisplayNameLength = 80;

    public ProviderDescriptor(
        string stableId,
        string displayName,
        bool requiresNetwork,
        bool requiresCredentials)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        string normalizedId = stableId.Trim();
        if (normalizedId.Length > MaximumStableIdLength
            || !IsCanonicalStableId(normalizedId))
        {
            throw new ArgumentException(
                "Provider stable ID must be 1-64 lowercase ASCII letters, digits, periods or hyphens, and must start and end with a letter or digit.",
                nameof(stableId));
        }

        string normalizedDisplayName = displayName.Trim();
        if (normalizedDisplayName.Length > MaximumDisplayNameLength
            || normalizedDisplayName.Any(static character =>
                char.IsControl(character)
                || char.IsSurrogate(character)
                || char.GetUnicodeCategory(character) == System.Globalization.UnicodeCategory.Format))
        {
            throw new ArgumentException(
                "Provider display name must be 1-80 characters and cannot contain control, format or surrogate characters.",
                nameof(displayName));
        }

        StableId = normalizedId;
        DisplayName = normalizedDisplayName;
        RequiresNetwork = requiresNetwork;
        RequiresCredentials = requiresCredentials;
    }

    public string StableId { get; }

    public string DisplayName { get; }

    public bool RequiresNetwork { get; }

    public bool RequiresCredentials { get; }

    private static bool IsCanonicalStableId(string value)
    {
        if (!IsAsciiLetterOrDigit(value[0]) || !IsAsciiLetterOrDigit(value[^1]))
        {
            return false;
        }

        return value.All(static character =>
            IsAsciiLetterOrDigit(character) || character is '-' or '.');
    }

    private static bool IsAsciiLetterOrDigit(char value) =>
        value is >= 'a' and <= 'z' or >= '0' and <= '9';
}
