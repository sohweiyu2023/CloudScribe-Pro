using System.Globalization;

namespace CloudScribe.App.Composition;

/// <summary>
/// Presentation only. Never replace provider language IDs, saved filters or request metadata
/// with these labels. Unrecognized provider codes remain visible verbatim.
/// </summary>
public static class LanguageDisplayNames
{
    private static readonly Dictionary<string, string> Overrides =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["cmn"] = "Chinese (Mandarin)",
            ["cmn-CN"] = "Chinese (Mandarin, Mainland China)",
            ["cmn-TW"] = "Chinese (Mandarin, Taiwan)",
            ["yue"] = "Chinese (Cantonese)",
            ["yue-HK"] = "Chinese (Cantonese, Hong Kong)",
            ["en-US"] = "English (United States)",
            ["en-GB"] = "English (United Kingdom)",
            ["en-AU"] = "English (Australia)",
            ["en-IN"] = "English (India)",
            ["en-CA"] = "English (Canada)",
        };

    private static readonly Dictionary<string, string> CultureNames =
        CultureInfo.GetCultures(CultureTypes.NeutralCultures | CultureTypes.SpecificCultures)
            .Where(culture => !string.IsNullOrEmpty(culture.Name))
            .DistinctBy(culture => culture.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(culture => culture.Name, culture => culture.EnglishName, StringComparer.OrdinalIgnoreCase);

    public static string Name(string code) =>
        Overrides.TryGetValue(code, out string? name) || CultureNames.TryGetValue(code, out name)
            ? name
            : code;

    public static string WithCode(string code)
    {
        string name = Name(code);
        return string.Equals(name, code, StringComparison.OrdinalIgnoreCase) ? code : name + " · " + code;
    }

    public static string Names(IReadOnlyList<string>? codes) => codes is { Count: > 0 }
        ? string.Join(" / ", codes.Select(Name))
        : "Language not provided";

    public static string WithCodes(IReadOnlyList<string>? codes) => codes is { Count: > 0 }
        ? string.Join(" / ", codes.Select(WithCode))
        : "Language not provided";
}
