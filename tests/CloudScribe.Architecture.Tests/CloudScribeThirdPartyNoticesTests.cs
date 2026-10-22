using CloudScribe.App.Composition;
using Xunit;

namespace CloudScribe.Architecture.Tests;

public sealed class CloudScribeThirdPartyNoticesTests
{
    [Fact]
    public void BundledReaderPreservesEveryOriginalLicenseNoticeAndInventory()
    {
        string root = FindRepositoryRoot();
        string[] paths = ["LICENSE", CloudScribeThirdPartyNotices.IndexName, .. Directory.EnumerateFiles(Path.Combine(root, "licenses", "third-party"), "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))];
        IReadOnlyDictionary<string, string> documents = CloudScribeThirdPartyNotices.ReadDocuments();

        Assert.Equal(paths.Length, documents.Count);
        foreach (string path in paths)
        {
            Assert.True(documents.TryGetValue(path, out string? content), $"Missing bundled document: {path}");
            Assert.Equal(File.ReadAllText(Path.Combine(root, path)), content);
        }
    }

    [Fact]
    public void NoticesRetainProjectLicenseBoundaryAndIndependentProvenance()
    {
        IReadOnlyDictionary<string, string> documents = CloudScribeThirdPartyNotices.ReadDocuments();
        string index = documents[CloudScribeThirdPartyNotices.IndexName];

        Assert.Contains("project MIT license does not replace the licenses of its dependencies", index, StringComparison.Ordinal);
        Assert.Contains("Cosmic Transcriber project/reference archive keeps its own rights status", index, StringComparison.Ordinal);
        Assert.Contains("dependency licenses do not grant provider access", index, StringComparison.Ordinal);
        Assert.Contains("MIT License", documents["LICENSE"], StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CloudScribe.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("CloudScribe repository root was not found.");
    }
}
