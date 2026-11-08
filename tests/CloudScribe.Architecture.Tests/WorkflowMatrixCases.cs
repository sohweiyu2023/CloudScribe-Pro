using System.Text.Json;

namespace CloudScribe.Architecture.Tests;

internal static class WorkflowMatrixCases
{
    internal const int Seed = 20261108;
    internal static readonly ConstrainedPairwise.Axis[] Axes =
    [
        new("voice", ["neural2", "aoede"]),
        new("omission", ["off", "v1", "v2"]),
        new("text", ["plain", "explicit-url", "bare-domain", "unicode", "url-only"]),
        new("observation", ["open", "filter", "refresh"]),
        new("evidence", ["unchanged", "missing", "expired", "metadata"]),
        new("lifetime", ["same-session", "reopen-english", "reopen-chinese"]),
        new("work", ["pending", "approved", "completed", "uncertain"]),
    ];

    // Both omission versions make the explicit URL-only fixture unspeakable. That refusal is tested separately.
    internal static bool IsValid(string[] row) => !string.Equals(row[2], "url-only", StringComparison.Ordinal)
        || string.Equals(row[1], "off", StringComparison.Ordinal);

    internal static string ManifestPath
    {
        get
        {
            string? directory = AppContext.BaseDirectory;
            while (directory is not null && !File.Exists(Path.Combine(directory, "CloudScribe.sln"))) directory = Path.GetDirectoryName(directory);
            return Path.Combine(directory ?? throw new DirectoryNotFoundException("Matrix source inventory is required."),
                "tests", "CloudScribe.Architecture.Tests", "Fixtures", "catalog-workflow-pairwise-v1.json");
        }
    }

    internal static string[] ReadIds()
    {
        using var json = JsonDocument.Parse(File.ReadAllBytes(ManifestPath));
        if (json.RootElement.GetProperty("seed").GetInt32() != Seed) throw new InvalidDataException("Matrix seed changed without regenerating cases.");
        return json.RootElement.GetProperty("cases").EnumerateArray().Select(item => item.GetString()!).ToArray();
    }
}
