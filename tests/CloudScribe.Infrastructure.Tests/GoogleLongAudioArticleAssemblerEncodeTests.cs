using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed class GoogleLongAudioArticleAssemblerEncodeTests
{
    [Fact]
    public void FinalEncodeAppliesOnlyFixedHeadroomBeforeTheMp3Encoder()
    {
        var arguments = GoogleLongAudioArticleAssembler.FinalEncodeArguments("inputs.ffconcat", "article.mp3");
        int filter = IndexOf(arguments, "-af");
        int codec = IndexOf(arguments, "libmp3lame");
        Assert.InRange(filter, 0, codec - 1);
        Assert.Equal(GoogleLongAudioArticleAssembler.EncodeHeadroomFilter, arguments[filter + 1]);
        Assert.Equal("volume=-1.5dB", GoogleLongAudioArticleAssembler.EncodeHeadroomFilter);
        Assert.Single(arguments, x => string.Equals(x, "-af", StringComparison.Ordinal));
        Assert.DoesNotContain(arguments, x => x.Contains("loudnorm", StringComparison.Ordinal)
            || x.Contains("atempo", StringComparison.Ordinal) || x.Contains("asetrate", StringComparison.Ordinal)
            || x.Contains("compand", StringComparison.Ordinal) || x.Contains("alimiter", StringComparison.Ordinal));
        Assert.Equal("inputs.ffconcat", arguments[IndexOf(arguments, "-i") + 1]);
        Assert.Equal("-n", arguments[^2]);
        Assert.Equal("article.mp3", arguments[^1]);
    }

    [Fact]
    public void FinalEncodeTagsTheArticleTitleWithoutTextOrControlCharacters()
    {
        var arguments = GoogleLongAudioArticleAssembler.FinalEncodeArguments("inputs.ffconcat", "article.mp3", "無我\n  Anatta\t(Chinese)");
        int metadata = IndexOf(arguments, "-metadata");
        Assert.Equal("title=無我 Anatta (Chinese)", arguments[metadata + 1]);
        Assert.Equal("-1", arguments[IndexOf(arguments, "-map_metadata") + 1]);
        Assert.Equal("-n", arguments[^2]);
        Assert.Null(GoogleLongAudioArticleAssembler.MetadataTitle("  \r\n "));
        Assert.Equal(200, GoogleLongAudioArticleAssembler.MetadataTitle(new string('a', 500))!.Length);
        Assert.DoesNotContain("-metadata", GoogleLongAudioArticleAssembler.FinalEncodeArguments("m", "p", null), StringComparer.Ordinal);
    }

    [Fact]
    public void VerifiedAssemblyRemovesOnlyDuplicateWavSnapshots()
    {
        string work = Path.Combine(Path.GetTempPath(), "cloudscribe-snapshots-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            foreach (string name in new[] { "part-00000000.wav", "part-00000001.wav", "part-unrelated.wav", "part-00000002.wav", "ordered-inputs.json", "inputs.ffconcat", "article.mp3" })
                File.WriteAllText(Path.Combine(work, name), name);
            Assert.Equal(2, GoogleLongAudioArticleAssembler.RemoveVerifiedSnapshots(work, 2));
            Assert.Equal(["article.mp3", "inputs.ffconcat", "ordered-inputs.json", "part-00000002.wav", "part-unrelated.wav"],
                Directory.EnumerateFiles(work).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray());
        }
        finally { Directory.Delete(work, recursive: true); }
    }

    [Fact]
    public void MissingOrLockedSnapshotsDoNotTurnVerifiedCompletionIntoFailure()
    {
        string work = Path.Combine(Path.GetTempPath(), "cloudscribe-locked-snapshot-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(0, GoogleLongAudioArticleAssembler.RemoveVerifiedSnapshots(work, 2));
        if (!OperatingSystem.IsWindows()) return; // Windows file-sharing denial is the condition under test.
        Directory.CreateDirectory(work);
        try
        {
            string path = Path.Combine(work, "part-00000000.wav");
            using (var locked = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                _ = GoogleLongAudioArticleAssembler.RemoveVerifiedSnapshots(work, 1);
                Assert.True(File.Exists(path));
            }
            Assert.Equal(1, GoogleLongAudioArticleAssembler.RemoveVerifiedSnapshots(work, 1));
        }
        finally { Directory.Delete(work, recursive: true); }
    }

    private static int IndexOf(IReadOnlyList<string> values, string value)
    {
        for (int i = 0; i < values.Count; i++)
            if (string.Equals(values[i], value, StringComparison.Ordinal)) return i;
        return -1;
    }
}
