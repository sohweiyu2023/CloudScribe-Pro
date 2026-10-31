using CloudScribe.App.Composition;
using CloudScribe.App.ViewModels;

namespace CloudScribe.Architecture.Tests;

public sealed class ChirpResumeSelectionBindingTests
{
    private static ShellViewModel.GoogleTtsStudioRequestSelection Selection(DateTimeOffset now) => new(Guid.NewGuid(), Guid.NewGuid(),
        "Synthetic private narration", "provider", "account", "project", "voice", Guid.NewGuid().ToString("D"), "fingerprint", "cmn-CN", "cmn-CN", now);

    [Fact]
    public void RecaptureTimestampIsNotRequestIdentityButFreshnessRemainsRequired()
    {
        var now = DateTimeOffset.UtcNow; var selection = Selection(now);
        Assert.Equal(ChirpResumeSelectionBinding.Hash(selection), ChirpResumeSelectionBinding.Hash(selection with { CapturedAtUtc = now.AddSeconds(1) }));
        ChirpResumeSelectionBinding.RequireFresh(selection, now.AddMinutes(1));
        Assert.Throws<InvalidOperationException>(() => ChirpResumeSelectionBinding.RequireFresh(selection, now.AddMinutes(10).AddTicks(1)));
        Assert.Throws<InvalidOperationException>(() => ChirpResumeSelectionBinding.RequireFresh(selection, now.AddTicks(-1)));
    }

    [Theory]
    [InlineData("text")]
    [InlineData("revision")]
    [InlineData("account")]
    [InlineData("project")]
    [InlineData("catalog")]
    [InlineData("voice")]
    [InlineData("fingerprint")]
    [InlineData("locale")]
    public void EveryStableSelectionChangeRemainsBound(string field)
    {
        var value = Selection(DateTimeOffset.UtcNow);
        var changed = field switch
        {
            "text" => value with { ExactText = value.ExactText + " changed" },
            "revision" => value with { RevisionId = Guid.NewGuid() },
            "account" => value with { AccountStableId = "different" },
            "project" => value with { ProjectStableId = "different" },
            "catalog" => value with { CapabilityEvidenceId = Guid.NewGuid().ToString("D") },
            "voice" => value with { VoiceStableId = "different" },
            "fingerprint" => value with { VoiceFingerprint = "different" },
            _ => value with { Locale = "different" },
        };
        Assert.NotEqual(ChirpResumeSelectionBinding.Hash(value), ChirpResumeSelectionBinding.Hash(changed), StringComparer.Ordinal);
    }
}
