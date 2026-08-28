using System.Text.RegularExpressions;

namespace CloudScribe.App.Design;

public static partial class BuildLabelFormatter
{
    private const string DevelopmentBuildLabel = "development build";
    private const int RegexTimeoutMilliseconds = 1000;

    public static string Format(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return DevelopmentBuildLabel;
        }

        string trimmed = informationalVersion.Trim();
        Match stageMatch = StageVersionPattern().Match(trimmed);
        if (stageMatch.Success)
        {
            return $"{stageMatch.Groups["version"].Value} · Stage {stageMatch.Groups["stage"].Value}";
        }

        Match semanticVersionMatch = SemanticVersionPattern().Match(trimmed);
        if (semanticVersionMatch.Success)
        {
            return semanticVersionMatch.Groups["version"].Value;
        }

        return trimmed.Length <= 32
            ? trimmed
            : DevelopmentBuildLabel;
    }

    [GeneratedRegex(
        @"^(?<version>\d+\.\d+\.\d+)-stage(?<stage>\d+)(?:[-+].*)?$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking,
        RegexTimeoutMilliseconds)]
    private static partial Regex StageVersionPattern();

    [GeneratedRegex(
        @"^(?<version>\d+\.\d+\.\d+)(?:[-+].*)?$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        RegexTimeoutMilliseconds)]
    private static partial Regex SemanticVersionPattern();
}
