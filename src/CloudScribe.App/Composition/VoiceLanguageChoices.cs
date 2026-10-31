namespace CloudScribe.App.Composition;

/// <summary>Presentation-only choices from observed provider codes; never voice or request authority.</summary>
public static class VoiceLanguageChoices
{
    public static IReadOnlyList<VoiceLanguageChoice> Build(IEnumerable<string> observedCodes, string? selectedCode,
        string? search, bool catalogCurrent)
    {
        ArgumentNullException.ThrowIfNull(observedCodes);
        var codes = observedCodes.Where(code => !string.IsNullOrWhiteSpace(code)).Distinct(StringComparer.Ordinal).ToArray();
        var choices = codes.Select(code => new VoiceLanguageChoice(code, LanguageDisplayNames.WithCode(code), catalogCurrent)).ToList();
        if (!string.IsNullOrWhiteSpace(selectedCode) && !codes.Contains(selectedCode, StringComparer.Ordinal))
            choices.Add(new(selectedCode, LanguageDisplayNames.WithCode(selectedCode) + " (saved; not in the current catalog)", false));
        choices.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Label, right.Label));
        choices.Insert(0, new(null, "All languages (no language filter)", catalogCurrent));
        string term = search?.Trim() ?? string.Empty;
        return choices.Where(choice => term.Length == 0 || choice.Label.Contains(term, StringComparison.OrdinalIgnoreCase)
            || choice.Code?.Contains(term, StringComparison.OrdinalIgnoreCase) == true).ToArray();
    }
}
