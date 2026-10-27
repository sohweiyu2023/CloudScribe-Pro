using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;

namespace CloudScribe.Application.Tests;

public sealed class ArticleAudioManifestTests
{
    [Fact]
    public void OrdersAcceptedAudioByExactMembershipAndNeverSplitsAtAnHour()
    {
        var manifest = Manifest();
        var audio = manifest.Members.Select(Receipt).Reverse().ToArray();
        var ordered = manifest.RequireComplete(audio);
        Assert.Equal(manifest.Members.Select(x => x.DocumentId), ordered.Select(x => x.DocumentId));
        var plan = new AudioAssemblyPlan(ordered.Select(x => new AudioSegmentArtifact(x.DocumentId.ToString("N"), x.AudioPath,
            "audio/mpeg", TimeSpan.FromMinutes(45), x.AudioSha256)),
            new("article-continuous", 0, null, 0, 0), ReleaseAudioFormat.Mp3,
            TimeSpan.FromMinutes(90) + TimeSpan.FromTicks(1), Path.GetTempPath(), "article");
        Assert.Single(plan.Parts);
        Assert.Equal(TimeSpan.FromMinutes(90), plan.TotalMeasuredDuration);
        var arguments = AudioAssemblyNativePlanner.Plan(plan, Path.Combine(Path.GetTempPath(), "ffmpeg.exe"), TimeSpan.FromMinutes(30))[0].Arguments;
        Assert.Contains("-n", arguments, StringComparer.Ordinal);
        Assert.Contains("[0:a][1:a]concat=n=2:v=0:a=1,alimiter=limit=1[outa]", arguments, StringComparer.Ordinal);
    }

    [Fact]
    public void MissingStaleDuplicateAndUncertainMembershipFailClosedWithoutPrivateText()
    {
        var manifest = Manifest();
        var audio = manifest.Members.Select(Receipt).ToArray();
        Assert.Throws<InvalidOperationException>(() => manifest.RequireComplete(audio[..1]));
        Assert.Throws<InvalidOperationException>(() => manifest.RequireComplete([audio[0] with { RevisionId = Guid.NewGuid() }, audio[1]]));
        Assert.Throws<InvalidOperationException>(() => manifest.RequireComplete([audio[0], audio[0], audio[1]]));
        Assert.Throws<InvalidOperationException>(() => manifest.RequireComplete([audio[0], audio[1] with { AudioPath = audio[0].AudioPath }]));
        var error = Assert.Throws<InvalidOperationException>(() => manifest.RequireComplete([audio[0] with { TextSha256 = "PRIVATE_SENTINEL" }, audio[1]]));
        Assert.DoesNotContain("PRIVATE_SENTINEL", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(audio[0].AudioPath, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NonContiguousOrRepeatedChildOrderFailsClosed()
    {
        var manifest = Manifest();
        Assert.Throws<InvalidOperationException>(() => (manifest with { Members = manifest.Members.Reverse().ToArray() }).RequireComplete([]));
        Assert.Throws<InvalidOperationException>(() => (manifest with { Members = [manifest.Members[0], manifest.Members[0] with { Ordinal = 2 }] }).RequireComplete([]));
    }

    private static ArticleAudioManifest Manifest() => new(Guid.NewGuid(), Guid.NewGuid(), new string('a', 64),
        [new(Guid.NewGuid(), Guid.NewGuid(), new string('b', 64), 1), new(Guid.NewGuid(), Guid.NewGuid(), new string('c', 64), 2)]);

    private static ArticleAcceptedAudio Receipt(ArticleAudioMember member) => new(member.DocumentId, member.RevisionId,
        member.TextSha256, "synthetic-account", "synthetic-operation", "synthetic-key", Guid.NewGuid().ToString("N"),
        new string('d', 64), Path.Combine(Path.GetTempPath(), member.DocumentId.ToString("N") + ".mp3"), 128, new string('e', 64), "synthetic-voice", "en-US");
}
