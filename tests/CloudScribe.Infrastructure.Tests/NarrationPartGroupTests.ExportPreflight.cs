using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Theory]
    [InlineData("extension")]
    [InlineData("file")]
    [InlineData("directory")]
    [InlineData("parent")]
    [InlineData("executable-name")]
    [InlineData("missing-ffmpeg")]
    [InlineData("missing-ffprobe")]
    public void ExportPreflightRejectsDeterministicLocalFailuresWithoutWriting(string failure)
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Root);
        string tool = CreateArticleTools(fixture.Root);
        string destination = Path.Combine(fixture.Root, "new.mp3");
        switch (failure)
        {
            case "extension": destination = Path.Combine(fixture.Root, "new.wav"); break;
            case "file": File.WriteAllBytes(destination, [1, 2, 3]); break;
            case "directory": Directory.CreateDirectory(destination); break;
            case "parent": destination = Path.Combine(fixture.Root, "missing", "new.mp3"); break;
            case "executable-name":
                tool = Path.Combine(fixture.Root, "other.exe"); File.WriteAllBytes(tool, [0]); break;
            case "missing-ffmpeg": File.Delete(tool); break;
            case "missing-ffprobe": File.Delete(Path.Combine(fixture.Root, "ffprobe.exe")); break;
        }
        string[] before = Directory.GetFileSystemEntries(fixture.Root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
        Assert.Throws<InvalidOperationException>(() => ArticleAudioExportService.PreflightExport(destination, tool));
        Assert.Equal(before, Directory.GetFileSystemEntries(fixture.Root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray());
        if (string.Equals(failure, "file", StringComparison.Ordinal)) Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(destination));
    }

    [Fact]
    public void ExportPreflightReturnsCanonicalPathsWithoutCreatingDestination()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Root);
        string tool = CreateArticleTools(fixture.Root);
        string destination = Path.Combine(fixture.Root, ".", "new.MP3");
        var result = ArticleAudioExportService.PreflightExport(destination, tool);
        Assert.Equal(Path.GetFullPath(destination), result.Destination);
        Assert.Equal(Path.GetFullPath(tool), result.Ffmpeg);
        Assert.Equal(Path.Combine(fixture.Root, "ffprobe.exe"), result.Ffprobe);
        Assert.False(File.Exists(result.Destination));
        Assert.Equal(2, Directory.GetFiles(fixture.Root).Length);
    }

    public static bool CanTestExportSymlinks => !OperatingSystem.IsWindows();

    [Fact(Skip = "Windows symlink privileges are not assumed; native Windows coverage remains required.", SkipUnless = nameof(CanTestExportSymlinks))]
    public void ExportPreflightRejectsLinkedParentWithoutCreatingDestination()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Root);
        string tool = CreateArticleTools(fixture.Root);
        string real = Path.Combine(fixture.Root, "real"); Directory.CreateDirectory(real);
        string link = Path.Combine(fixture.Root, "linked"); Directory.CreateSymbolicLink(link, real);
        try
        {
            Assert.Throws<InvalidOperationException>(() => ArticleAudioExportService.PreflightExport(Path.Combine(link, "new.mp3"), tool));
            Assert.Empty(Directory.GetFiles(real));
        }
        finally { Directory.Delete(link); }
    }
}
