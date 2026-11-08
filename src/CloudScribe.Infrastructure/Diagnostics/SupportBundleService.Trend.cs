using CloudScribe.Application.Diagnostics;
using CloudScribe.Infrastructure.Files;

namespace CloudScribe.Infrastructure.Diagnostics;

public sealed partial class SupportBundleService
{
    private const string TrendFileName = "google-trend-history-v1.jsonl";
    private const string TrendRelativePath = "history/" + TrendFileName;

    private void AddTrendSource(List<SupportBundleFile> captured)
    {
        var directory = new DirectoryInfo(Path.Combine(paths.RootDirectory, "data"));
        if (directory.LinkTarget is not null || directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("Support bundles do not traverse a linked trend-history directory.");
        if (!directory.Exists) return;
        PhysicalDirectoryPolicy.ValidateExistsWithoutLinks(directory.FullName, "Support bundles require a physical trend-history directory.");
        var file = new FileInfo(Path.Combine(directory.FullName, TrendFileName));
        if (file.LinkTarget is not null || file.Exists && file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("Support bundles do not follow linked trend-history files.");
        if (file.Exists) captured.Add(new(TrendRelativePath, file.Length, "persistent-safe-trend-metadata"));
    }

    private string ResolveSourcePath(SupportBundleFile file)
    {
        if (string.Equals(file.RelativePath, TrendRelativePath, StringComparison.Ordinal))
        {
            string directory = PhysicalDirectoryPolicy.ValidateExistsWithoutLinks(Path.Combine(paths.RootDirectory, "data"),
                "Support bundles require a physical trend-history directory.");
            return Path.Combine(directory, TrendFileName);
        }
        EnsureDiagnosticsDirectoryIsPhysical();
        const string prefix = "logs/";
        if (!file.RelativePath.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidOperationException("A support-bundle diagnostic entry has an invalid relative path.");
        string name = file.RelativePath[prefix.Length..];
        if (string.IsNullOrWhiteSpace(name) || !string.Equals(name, Path.GetFileName(name), StringComparison.Ordinal))
            throw new InvalidOperationException("A support-bundle diagnostic filename is not a single physical path segment.");
        string root = Path.GetFullPath(paths.DiagnosticsDirectory) + Path.DirectorySeparatorChar;
        string source = Path.GetFullPath(Path.Combine(paths.DiagnosticsDirectory, name));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!source.StartsWith(root, comparison)) throw new InvalidOperationException("A support-bundle source escaped the diagnostics directory.");
        return source;
    }
}
