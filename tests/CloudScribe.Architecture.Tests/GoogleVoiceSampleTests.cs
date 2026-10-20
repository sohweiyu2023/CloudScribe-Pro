using CloudScribe.App.Composition;
using CloudScribe.App.ViewModels;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Pricing;

namespace CloudScribe.Architecture.Tests;

public sealed class GoogleVoiceSampleTests
{
    [Fact]
    public async Task SelectionAndUnapprovedActionNeverSubmit()
    {
        var harness = new SampleHarness();
        GoogleVoiceSampleSpec spec = Spec();
        Assert.False(harness.Session.HasCached(spec));
        Assert.Equal(0, harness.Preparations);
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Session.GenerateAsync(spec, false, TestContext.Current.CancellationToken));
        Assert.False(await harness.Session.PrepareAsync(spec, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Session.GenerateAsync(spec, false, TestContext.Current.CancellationToken));
        Assert.Equal(0, harness.Generations);
        Assert.Equal(0, harness.Approvals);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    [InlineData(5000)]
    public async Task LongDocumentOrEmptyTextCannotBecomeSample(int length)
    {
        var harness = new SampleHarness();
        GoogleVoiceSampleSpec spec = Spec() with { Text = new string('x', length) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Session.PrepareAsync(spec, TestContext.Current.CancellationToken));
        Assert.Equal(0, harness.Preparations);
        Assert.Equal(0, harness.Generations);
    }

    [Fact]
    public async Task ApprovedSampleIsSingleUseAndCachedReplayExportNeverSubmit()
    {
        var harness = new SampleHarness();
        GoogleVoiceSampleSpec spec = Spec();
        await harness.Session.PrepareAsync(spec, TestContext.Current.CancellationToken);
        await harness.Session.GenerateAsync(spec, true, TestContext.Current.CancellationToken);
        Assert.Null(harness.Session.Review);
        Assert.True(await harness.Session.PrepareAsync(spec, TestContext.Current.CancellationToken));
        await harness.Session.ReplayAsync(spec, TestContext.Current.CancellationToken);
        Assert.Equal("chosen.mp3", await harness.Session.ExportAsync(spec, "chosen.mp3", TestContext.Current.CancellationToken));
        Assert.Equal(1, harness.Generations);
        Assert.Equal(1, harness.Preparations);
        Assert.Equal(1, harness.Approvals);
        Assert.Equal(1, harness.Plays);
        Assert.Equal(1, harness.Exports);
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Session.GenerateAsync(spec, true, TestContext.Current.CancellationToken));
        Assert.Equal(1, harness.Generations);
    }

    [Fact]
    public async Task ChangedTextVoiceLanguageAccountProjectOrFingerprintCannotReuseApprovalOrAudio()
    {
        var harness = new SampleHarness();
        GoogleVoiceSampleSpec original = Spec();
        await harness.Session.PrepareAsync(original, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Session.GenerateAsync(original with { Text = "different" }, true, TestContext.Current.CancellationToken));
        Assert.Equal(0, harness.Generations);
        await harness.Session.PrepareAsync(original, TestContext.Current.CancellationToken);
        await harness.Session.GenerateAsync(original, true, TestContext.Current.CancellationToken);
        GoogleVoiceSampleSpec[] changed =
        [
            original with { Text = original.Text + " " },
            original with { LanguageCode = "en-GB" },
            original with { Voice = original.Voice with { VoiceStableId = "en-US-Standard-D" } },
            original with { Voice = original.Voice with { ProviderStableId = "another-provider" } },
            original with { Voice = original.Voice with { AccountStableId = "another-account" } },
            original with { Voice = original.Voice with { ProjectStableId = "another-project" } },
            original with { Voice = original.Voice with { VoiceFingerprint = "changed-fingerprint" } },
        ];
        Assert.All(changed, item => Assert.False(harness.Session.HasCached(item)));
        foreach (GoogleVoiceSampleSpec spec in changed)
            await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Session.ReplayAsync(spec, TestContext.Current.CancellationToken));
        Assert.Equal(1, harness.Generations);
        Assert.Equal(0, harness.Plays);
    }

    [Fact]
    public async Task ChangedAcceptedFileCannotBeReplayedOrSilentlyRegenerated()
    {
        var harness = new SampleHarness();
        GoogleVoiceSampleSpec spec = Spec();
        await harness.Session.PrepareAsync(spec, TestContext.Current.CancellationToken);
        await harness.Session.GenerateAsync(spec, true, TestContext.Current.CancellationToken);
        harness.Corrupt = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Session.ReplayAsync(spec, TestContext.Current.CancellationToken));
        Assert.False(harness.Session.HasCached(spec));
        Assert.Equal(0, harness.Plays);
        Assert.Equal(1, harness.Generations);
    }

    [Fact]
    public async Task FailedSubmissionIsNotRetriedOrCached()
    {
        var harness = new SampleHarness { FailGeneration = true };
        GoogleVoiceSampleSpec spec = Spec();
        await harness.Session.PrepareAsync(spec, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Session.GenerateAsync(spec, true, TestContext.Current.CancellationToken));
        Assert.Null(harness.Session.Review);
        Assert.False(harness.Session.HasCached(spec));
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Session.PrepareAsync(spec, TestContext.Current.CancellationToken));
        Assert.Equal(1, harness.Generations);
        Assert.Equal(1, harness.Preparations);
    }

    [Fact]
    public void PriceComparisonUsesAuthenticatedRatesAndDoesNotInventUnsupportedRates()
    {
        var controls = new V222ControlSet(new StrictJsonObjectReader());
        Assert.Equal(4m, GoogleVoiceComparisonInformation.Rate(controls, "en-US-Standard-C")!.UsdPerMillion);
        Assert.Equal(4m, GoogleVoiceComparisonInformation.Rate(controls, "en-US-Wavenet-C")!.UsdPerMillion);
        Assert.Equal(16m, GoogleVoiceComparisonInformation.Rate(controls, "en-US-Neural2-D")!.UsdPerMillion);
        Assert.Null(GoogleVoiceComparisonInformation.Rate(controls, "en-US-Polyglot-A"));
        Assert.False(GoogleVoiceComparisonInformation.SupportsSampleFamily("en-US-Chirp3-HD-Aoede"));
        Assert.Equal(1, GoogleVoiceComparisonInformation.Rate(controls, "en-US-Standard-C")!.ConservativeCents("a short sample"));
    }

    private static GoogleVoiceSampleSpec Spec() => new(new VoiceLabCatalogSelection(
        "en-US-Standard-C", "google-cloud", "account", "project", "capability", "fingerprint",
        true, true, true, ["en-US", "en-GB"], "FEMALE", 24000), "en-US", "A short shared sample.");

    private sealed class SampleHarness
    {
        public SampleHarness()
        {
            Session = new GoogleVoiceSampleSession(
                (spec, _, _) =>
                {
                    Preparations++;
                    return Task.FromResult(new ShellViewModel.GoogleGenerationSpendReview("USD", 2, 1, "pricing", spec.Voice.VoiceStableId, new string('a', 64)));
                },
                (maximum, confirmed, _) =>
                {
                    Assert.True(confirmed); Assert.Equal(1, maximum); Approvals++; return Task.CompletedTask;
                },
                _ =>
                {
                    Generations++;
                    return FailGeneration ? Task.FromException<string>(new InvalidOperationException("Uncertain provider result")) : Task.FromResult("accepted.mp3");
                },
                (path, _) => Corrupt ? Task.FromException<string>(new InvalidOperationException("Accepted bytes changed")) : Task.FromResult(path),
                (_, _) => { Plays++; return Task.CompletedTask; },
                (_, destination, _) => { Exports++; return Task.FromResult(destination); },
                () => { });
        }

        public GoogleVoiceSampleSession Session { get; }
        public int Preparations { get; private set; }
        public int Approvals { get; private set; }
        public int Generations { get; private set; }
        public int Plays { get; private set; }
        public int Exports { get; private set; }
        public bool Corrupt { get; set; }
        public bool FailGeneration { get; set; }
    }
}
