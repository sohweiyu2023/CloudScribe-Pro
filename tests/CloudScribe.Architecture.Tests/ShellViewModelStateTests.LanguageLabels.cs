using CloudScribe.App.Composition;
using CloudScribe.App.ViewModels;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Architecture.Tests;

public sealed partial class ShellViewModelStateTests
{
    [Theory]
    [InlineData("cmn-CN")]
    [InlineData("cmn-TW")]
    [InlineData("yue-HK")]
    [InlineData("x-cloudscribe-test")]
    public async Task FriendlyLabelsDoNotChangeSavedManualOrCapturedProviderLocale(string code)
    {
        using ShellViewModel model = CreateViewModel();
        string voiceName = code + "-Standard-A";
        model.ApplySavedVoiceSetup(new VoiceSetup("Synthetic setup", code, voiceName));
        var voice = VoiceSelection() with
        {
            VoiceStableId = voiceName,
            ProviderStableId = GoogleGenerationProvider.StableProviderId,
            LanguageCodes = [code],
        };
        model.SelectedVoiceLabVoice = voice;
        model.RememberSelectedVoiceSetup();

        _ = LanguageDisplayNames.Names(voice.LanguageCodes);
        _ = LanguageDisplayNames.WithCodes(voice.LanguageCodes);
        _ = LanguageDisplayNames.WithCode(model.VoiceLabLocaleFilter!);
        Assert.Same(voice, model.SelectedVoiceLabVoice);
        Assert.Equal(code, model.VoiceLabLocaleFilter);
        Assert.Equal(voiceName, model.PreferredVoiceName);
        Assert.Null(model.PreparedGoogleGenerationSpendReview);

        ShellViewModel.GoogleTtsStudioRequestSelection? captured = null;
        model.ConfigureStage6GoogleGenerationPreparation(_ =>
        {
            captured = model.CaptureGoogleTtsStudioRequestSelection();
            return Task.FromResult(new ShellViewModel.GoogleGenerationSpendReview(
                "USD", 2, 1, "synthetic-pricing", voiceName, new string('a', 64)));
        });
        await model.PrepareGoogleVoiceSampleAsync(
            "Synthetic label check.", Guid.NewGuid(), TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.Equal(code, captured.LanguageCode);
        Assert.Equal(code, captured.Locale);
        Assert.Equal(voiceName, captured.VoiceStableId);
        Assert.Equal(code, Assert.Single(voice.LanguageCodes!));
        Assert.False(model.CanGenerateWithGoogle);
        Assert.False(model.GenerateWithGoogleCommand.IsRunning);
        model.CancelGoogleVoiceSampleReview();
    }
}
