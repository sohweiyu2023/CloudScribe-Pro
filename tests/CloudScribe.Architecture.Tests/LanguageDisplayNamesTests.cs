using CloudScribe.App.Composition;

namespace CloudScribe.Architecture.Tests;

public sealed class LanguageDisplayNamesTests
{
    [Theory]
    [InlineData("cmn-CN", "Chinese (Mandarin, Mainland China)")]
    [InlineData("CMN-cn", "Chinese (Mandarin, Mainland China)")]
    [InlineData("cmn-TW", "Chinese (Mandarin, Taiwan)")]
    [InlineData("yue-HK", "Chinese (Cantonese, Hong Kong)")]
    [InlineData("cmn", "Chinese (Mandarin)")]
    [InlineData("yue", "Chinese (Cantonese)")]
    [InlineData("en-US", "English (United States)")]
    [InlineData("en-GB", "English (United Kingdom)")]
    [InlineData("en-AU", "English (Australia)")]
    [InlineData("en-IN", "English (India)")]
    [InlineData("en-CA", "English (Canada)")]
    public void FriendlyNameKeepsExactRawCodeSecondary(string code, string name)
    {
        Assert.Equal(name, LanguageDisplayNames.Name(code));
        Assert.Equal(name + " · " + code, LanguageDisplayNames.WithCode(code));
        Assert.Equal(name, GoogleVoiceComparisonInformation.LocaleLabel(code));
    }

    [Fact]
    public void OtherRecognizedCulturesUseEnglishNames()
    {
        Assert.Equal("French (France)", LanguageDisplayNames.Name("fr-FR"));
    }

    [Fact]
    public void UnknownCodeIsShownVerbatimRatherThanGuessed()
    {
        const string code = "x-cloudscribe-test";
        Assert.Equal(code, LanguageDisplayNames.Name(code));
        Assert.Equal(code, LanguageDisplayNames.WithCode(code));
    }

    [Fact]
    public void EveryReturnedLanguageRemainsVisibleWithoutChangingOrderOrIds()
    {
        string[] codes = ["cmn-CN", "en-US", "x-cloudscribe-test"];
        Assert.Equal("Chinese (Mandarin, Mainland China) / English (United States) / x-cloudscribe-test",
            LanguageDisplayNames.Names(codes));
        Assert.Equal("Chinese (Mandarin, Mainland China) · cmn-CN / English (United States) · en-US / x-cloudscribe-test",
            LanguageDisplayNames.WithCodes(codes));
        Assert.Equal("cmn-CN|en-US|x-cloudscribe-test", string.Join('|', codes));
        Assert.Equal("Language not provided", LanguageDisplayNames.Names(null));
        Assert.Equal("Language not provided", LanguageDisplayNames.WithCodes([]));
    }
}
