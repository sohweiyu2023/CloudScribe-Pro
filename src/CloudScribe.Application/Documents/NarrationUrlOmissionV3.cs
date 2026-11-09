namespace CloudScribe.Application.Documents;

/// <summary>
/// v3: the same recognition as v2 for explicit HTTP(S)/www addresses and Markdown links, with a stricter bare-domain
/// rule so ordinary prose is never deleted from the narration. In v2, a missing space after a full stop ("awareness.In
/// this state", "for us.Us"), and numbers before a word ("2024.de facto", "section 3.in") were read as bare domains and
/// silently removed. v3 omits a bare domain only when:
/// <list type="bullet">
/// <item>its suffix is not mixed case: "com" and "COM" are domains, but "In" or "Net" is the capitalised first word of a
/// sentence typed without a space;</item>
/// <item>the label before the suffix contains a letter, except digit names on com/org/net/co/io (for example 84000.co);</item>
/// <item>it is not a two-label name ending in "in" or "us" with no path or port (English words such as "end.in" are far
/// more common in prose than bare .in/.us domains; such a domain is spoken instead of risking deleted words).</item>
/// </list>
/// Keeping a real domain only means it is spoken; deleting words changes the narration, so doubtful cases are kept.
/// v2 remains available unchanged for saved copies and settings that recorded it.
/// </summary>
internal static class NarrationUrlOmissionV3
{
    /// <summary>Change this identifier whenever recognition or output semantics change.</summary>
    public const string CurrentVersion = "url-omission-v3";

    public static NarrationUrlOmissionResult Transform(string sourceText) =>
        NarrationUrlOmissionV2.Transform(sourceText, strictBareDomains: true, CurrentVersion);

    /// <summary>Extra v3 conditions for a token that v2 already recognised as a bare domain.</summary>
    internal static bool IsConfidentBareDestination(ReadOnlySpan<char> destination)
    {
        int authorityEnd = destination.IndexOfAny('/', '?', '#');
        bool hasPath = authorityEnd >= 0;
        ReadOnlySpan<char> authority = hasPath ? destination[..authorityEnd] : destination;
        int colon = authority.IndexOf(':');
        bool hasPort = colon >= 0;
        ReadOnlySpan<char> host = hasPort ? authority[..colon] : authority;
        int lastDot = host.LastIndexOf('.');
        if (lastDot < 1) return false;
        ReadOnlySpan<char> suffix = host[(lastDot + 1)..];
        bool upper = false, lower = false;
        foreach (char value in suffix) { upper |= char.IsAsciiLetterUpper(value); lower |= char.IsAsciiLetterLower(value); }
        if (upper && lower) return false;
        ReadOnlySpan<char> beforeSuffix = host[..lastDot];
        int previousDot = beforeSuffix.LastIndexOf('.');
        ReadOnlySpan<char> registrable = previousDot < 0 ? beforeSuffix : beforeSuffix[(previousDot + 1)..];
        bool hasLetter = false;
        foreach (char value in registrable) hasLetter |= char.IsAsciiLetter(value);
        if (!hasLetter && !IsAny(suffix, "com", "org", "net", "co", "io")) return false;
        bool twoLabels = previousDot < 0;
        return !(twoLabels && !hasPath && !hasPort && IsAny(suffix, "in", "us"));
    }

    private static bool IsAny(ReadOnlySpan<char> value, params string[] options)
    {
        foreach (string option in options)
            if (value.Equals(option, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
