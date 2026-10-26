using System.Text.Json;
using CloudScribe.App.Composition;
using CloudScribe.App.ViewModels;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Infrastructure.Pricing;

namespace CloudScribe.Architecture.Tests;

public sealed class GoogleVoiceRecommendationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 0, 0, 0, TimeSpan.Zero);
    private static VoiceLabCatalogSelection Voice(string name, string locale = "en-US") => new(
        name, GoogleGenerationProvider.StableProviderId, "account", "project", "evidence", "fingerprint", true, true, true, [locale]);

    [Fact]
    public void BalancedEnglishUsesVerifiedNeural2ThenAvailableWaveNet()
    {
        var wave = Voice("en-US-Wavenet-C");
        var neural = Voice("en-US-Neural2-D");
        Assert.Same(neural, GoogleVoiceRecommendationPolicy.Recommend([wave, neural], "en-US"));
        Assert.Same(wave, GoogleVoiceRecommendationPolicy.Recommend([wave], "en-US"));
        Assert.Null(GoogleVoiceRecommendationPolicy.Recommend([], "en-US"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("en")]
    [InlineData("en-GB")]
    [InlineData("zh-CN")]
    public void NoGuessedLocaleOrCrossRegionFallback(string? locale) =>
        Assert.Null(GoogleVoiceRecommendationPolicy.Recommend([Voice("en-US-Neural2-C")], locale));

    [Fact]
    public void MandarinWaveNetRequiresExplicitSelectionAndPremiumIsOptIn()
    {
        var wave = Voice("cmn-CN-Wavenet-A", "cmn-CN");
        var hd = Voice("cmn-CN-Chirp3-HD-Alnilam", "cmn-CN");
        Assert.Same(wave, GoogleVoiceRecommendationPolicy.Recommend([hd, wave], "cmn-CN"));
        Assert.Same(hd, GoogleVoiceRecommendationPolicy.Recommend([hd, wave], "cmn-CN", premiumHd: true));
        Assert.Null(GoogleVoiceRecommendationPolicy.Recommend([wave], "cmn-CN", premiumHd: true));
        Assert.Null(GoogleVoiceRecommendationPolicy.Recommend([Voice("cmn-CN-Neural2-A", "cmn-CN")], "cmn-CN"));
        Assert.False(GoogleVoiceRecommendationPolicy.MayApplyDefault(wave, null, null, "cmn-CN", false, true));
        Assert.False(GoogleVoiceRecommendationPolicy.MayApplyDefault(hd, null, null, "cmn-CN", true, true));
    }

    [Fact]
    public void UnavailableDisabledExpiredUnauthorizedOrUnsupportedFamilyCannotBeRecommended()
    {
        var original = Voice("en-US-Neural2-C");
        var invalid = new[] { original with { CapabilityCurrent = false }, original with { VoiceEnabled = false },
            original with { AccountProjectAuthorized = false }, original with { ProviderStableId = "other" },
            original with { LanguageCodes = null }, original with { VoiceStableId = "en-US-Studio-O" } };
        Assert.All(invalid, voice => Assert.Null(GoogleVoiceRecommendationPolicy.Recommend([voice], "en-US")));
    }

    [Fact]
    public void SavedManualAndUnavailableRememberedChoicesArePreserved()
    {
        var suggested = Voice("en-US-Neural2-C");
        var manual = Voice("en-US-Standard-D");
        Assert.False(GoogleVoiceRecommendationPolicy.MayApplyDefault(suggested, manual, null, "en-US", false, true));
        Assert.False(GoogleVoiceRecommendationPolicy.MayApplyDefault(suggested, null, "removed-remembered-voice", "en-US", false, true));
        Assert.True(GoogleVoiceRecommendationPolicy.MayApplyDefault(suggested, null, null, "en-US", false, true));
        Assert.False(GoogleVoiceRecommendationPolicy.MayApplyDefault(suggested, null, null, "en-US", false, false));
    }

    [Fact]
    public void ReferencePricesDiscloseSeparatePremiumAndNeverImplyCurrentSpendApproval()
    {
        var controls = new V222ControlSet(new StrictJsonObjectReader());
        var neural = GoogleVoiceRecommendationPolicy.ReferencePrice(controls, "en-US-Neural2-C")!;
        var hd = GoogleVoiceRecommendationPolicy.ReferencePrice(controls, "cmn-CN-Chirp3-HD-Alnilam")!;
        Assert.Equal(16m, neural.UsdPerMillion);
        Assert.Equal(30m, hd.UsdPerMillion);
        Assert.Contains("Current exact-request cost review is still required", hd.Describe(Now), StringComparison.Ordinal);
        Assert.Null(GoogleVoiceRecommendationPolicy.ReferencePrice(controls, "en-US-Unknown-C"));
        Assert.False((hd with { VerifiedAtUtc = Now.AddDays(-121) }).IsUsable(Now));
        Assert.False((hd with { VerifiedAtUtc = Now.AddDays(1) }).IsUsable(Now));
        Assert.Contains("stale or invalid", (hd with { VerifiedAtUtc = Now.AddDays(-121) }).Describe(Now), StringComparison.Ordinal);
    }

    [Fact]
    public void ChirpPlainTextMp3OmitsUnsupportedOrPreviewControls()
    {
        var plan = new SpeechPlan("cmn-CN", [new SpeechText("Synthetic test")], "offline");
        var options = new GoogleSpeechCompilationOptions("cmn-CN", "cmn-CN-Chirp3-HD-Alnilam", "MP3", 5000);
        var compiled = GoogleSpeechPlanCompiler.Compile(plan, options);
        using JsonDocument json = JsonDocument.Parse(compiled.Payload);
        Assert.True(json.RootElement.GetProperty("input").TryGetProperty("text", out _));
        Assert.False(json.RootElement.GetProperty("input").TryGetProperty("ssml", out _));
        Assert.Single(json.RootElement.GetProperty("audioConfig").EnumerateObject());
        Assert.Equal("MP3", json.RootElement.GetProperty("audioConfig").GetProperty("audioEncoding").GetString());
        Assert.Throws<InvalidOperationException>(() => GoogleSpeechPlanCompiler.Compile(plan, options with { AudioEncoding = "LINEAR16" }));
        var changed = new SpeechPlan("cmn-CN", [new SpeechProsody(1.1m, 0m, 0m), new SpeechProsody(1m, 0m, 0m), new SpeechText("test")], "offline");
        Assert.Throws<InvalidOperationException>(() => GoogleSpeechPlanCompiler.Compile(changed, options));
    }
}

