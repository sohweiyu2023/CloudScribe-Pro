using CloudScribe.App.Composition;

namespace CloudScribe.Architecture.Tests;

public sealed class VoiceLanguageChoicesTests
{
    [Fact]
    public void FriendlySearchUsesObservedLanguagesAndPreservesExactCodes()
    {
        string[] codes = ["en-US", "cmn-CN", "fr-FR", "cmn-CN"];
        var chinese = Assert.Single(VoiceLanguageChoices.Build(codes, "en-US", "Chinese", true));
        Assert.Equal("cmn-CN", chinese.Code);
        Assert.Contains("Chinese (Mandarin", chinese.Label, StringComparison.Ordinal);
        Assert.True(chinese.InCurrentCatalog);
        Assert.Equal("fr-FR", Assert.Single(VoiceLanguageChoices.Build(codes, null, "fr-FR", true)).Code);
        Assert.Equal(4, codes.Length);
    }

    [Fact]
    public void OfflineSavedChoiceIsRetainedWithoutClaimingVerificationOrEnglishFallback()
    {
        var choices = VoiceLanguageChoices.Build([], "cmn-CN", null, false);
        Assert.Equal(2, choices.Count);
        Assert.Contains(choices, choice => string.Equals(choice.Code, "cmn-CN", StringComparison.Ordinal));
        Assert.DoesNotContain(choices, choice => string.Equals(choice.Code, "en-US", StringComparison.Ordinal));
        Assert.All(choices, choice => Assert.False(choice.InCurrentCatalog));
        Assert.Single(VoiceLanguageChoices.Build([], null, null, false));
    }

    [Fact]
    public void UnknownSavedCodeRemainsVerbatimAndSearchDoesNotCreateNewCodes()
    {
        var choice = Assert.Single(VoiceLanguageChoices.Build(["en-US"], "x-saved", "x-saved", true));
        Assert.Equal("x-saved", choice.Code);
        Assert.False(choice.InCurrentCatalog);
        Assert.Empty(VoiceLanguageChoices.Build(["en-US"], "cmn-CN", "nonexistent", true));
    }
}
