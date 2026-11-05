using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed class ExistingMediaToolsTests
{
    [Fact]
    public void DiscoveryRequiresAnExactExistingPairAndNeverRunsIt()
    {
        string root = Path.Combine(Path.GetTempPath(), "cloudscribe-tool-discovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.Null(ExistingMediaTools.Find([null, "relative", root]));
            Assert.Null(ExistingMediaTools.Find([@"\\no-such-server.invalid\share", "//no-such-server.invalid/share", @"\\?\C:\device-path"]));
            File.WriteAllText(Path.Combine(root, "ffmpeg.exe"), "synthetic data; not executable");
            Assert.Null(ExistingMediaTools.Find([root]));
            File.WriteAllText(Path.Combine(root, "ffprobe.exe"), "synthetic data; not executable");
            Assert.Equal(Path.Combine(root, "ffmpeg.exe"), ExistingMediaTools.Find(["relative", root]));
            Assert.Equal("synthetic data; not executable", File.ReadAllText(Path.Combine(root, "ffmpeg.exe")));
            Assert.Null(ExistingMediaTools.Find(Enumerable.Repeat<string?>(null, 128).Append(root)));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void DiscoveryRejectsLinkedDirectoryBeforeLookingForTools()
    {
        string root = Path.Combine(Path.GetTempPath(), "cloudscribe-tool-link-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string link = Path.Combine(root, "linked");
        try
        {
            try { Directory.CreateSymbolicLink(link, Path.Combine(root, "missing-target")); }
            catch (UnauthorizedAccessException) { Assert.Skip("This host cannot create a synthetic directory link."); }
            catch (IOException error) when ((error.HResult & 0xffff) == 1314)
            { Assert.Skip("Windows has not granted symlink creation; no security setting was changed."); }
            Assert.Null(ExistingMediaTools.Find([link]));
        }
        finally
        {
            if (new DirectoryInfo(link).LinkTarget is not null) Directory.Delete(link);
            Directory.Delete(root, true);
        }
    }
}
