using System.Globalization;
using CloudScribe.Application.Generation;

namespace CloudScribe.Application.Tests;

public sealed class ArticleAudioConcatPlannerTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(350)]
    [InlineData(10_000)]
    public void ArticleConcatKeepsCommandBoundedAndManifestInExactOrder(int count)
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "article user's [test]"));
        var plan = AudioAssemblyNativePlanner.PlanArticleSnapshots(count, root, Path.Combine(root, "ffmpeg.exe"));
        Assert.True(plan.Invocation.Arguments.Sum(x => x.Length + 3) < 4096);
        Assert.Single(plan.Invocation.Arguments, x => string.Equals(x, "-i", StringComparison.Ordinal));
        Assert.DoesNotContain("-filter_complex", plan.Invocation.Arguments, StringComparer.Ordinal);
        Assert.Contains("0:a:0", plan.Invocation.Arguments, StringComparer.Ordinal);
        Assert.Contains("-vn", plan.Invocation.Arguments, StringComparer.Ordinal);
        string[] lines = plan.Manifest.Split('\n');
        Assert.Equal("ffconcat version 1.0", lines[0]);
        Assert.Equal(count + 2, lines.Length);
        Assert.Equal("", lines[^1]);
        for (int index = 1; index <= count; index++)
            Assert.Equal($"file 'input-{index.ToString("D3", CultureInfo.InvariantCulture)}.mp3'", lines[index]);
        Assert.DoesNotContain(root, plan.Manifest, StringComparison.Ordinal);
        Assert.Equal(Path.Combine(root, "article.part-001.mp3"), plan.Invocation.Arguments[^1]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10_001)]
    public void ArticleConcatRejectsUnsupportedCoverageCount(int count)
    {
        string root = Path.GetFullPath(Path.GetTempPath());
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioAssemblyNativePlanner.PlanArticleSnapshots(count, root, Path.Combine(root, "ffmpeg.exe")));
    }
}
