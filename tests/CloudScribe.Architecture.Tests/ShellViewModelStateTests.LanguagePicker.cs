using CloudScribe.App.Composition;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;

namespace CloudScribe.Architecture.Tests;

public sealed partial class ShellViewModelStateTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public async Task CatalogAlreadyExpiredAtPublicationClearsUnverifiedRowsAndPreservesManualVoice(int expiryOffsetSeconds)
    {
        var clock = new LanguageClock();
        using var model = CreateViewModel(clock: clock);
        model.ApplySavedVoiceSetup(new VoiceSetup("Chinese", "cmn-CN", "cmn-CN-Chirp3-HD-Alnilam"));
        var selected = VoiceSelection() with { VoiceStableId = "cmn-CN-Chirp3-HD-Alnilam", LanguageCodes = ["cmn-CN"] };
        model.ConfigureStage7VoiceLabCatalog(new VoiceLabCatalogQueryService((_, _) => Task.FromResult<IReadOnlyList<VoiceLabCatalogSelection>>([selected])),
            _ => Task.FromResult(CatalogState(accountAuthorized: true)));
        await model.RefreshVoiceCatalogForUiAsync(TestContext.Current.CancellationToken);
        Assert.True(model.VoiceLabCatalogLastRefreshSucceeded);
        Assert.NotNull(model.SelectedVoiceLabVoice);
        model.SetVoiceLanguageCatalog(["cmn-CN", "en-US"], clock.Now.AddSeconds(expiryOffsetSeconds), "synthetic-expired-scope");
        model.RefreshVoiceLanguageCatalogFreshness();
        Assert.False(model.HasCurrentVoiceLanguageCatalog);
        Assert.Null(model.VoiceLanguageCatalogScope);
        Assert.Empty(model.VoiceLabCatalogResults);
        Assert.Null(model.SelectedVoiceLabVoice);
        Assert.False(model.VoiceLabCatalogLastRefreshSucceeded);
        Assert.Contains("expired", model.ConnectedVoiceCatalogStatus, StringComparison.Ordinal);
        Assert.Equal("cmn-CN", model.VoiceLabLocaleFilter);
        Assert.Equal("cmn-CN-Chirp3-HD-Alnilam", model.PreferredVoiceName);
        Assert.False(model.CanGenerateWithGoogle);
    }

    [Fact]
    public void CatalogLanguageVerificationExpiresWithoutNetworkAndKeepsManualIntent()
    {
        var clock = new LanguageClock();
        using var model = CreateViewModel(clock: clock);
        model.ApplySavedVoiceSetup(new VoiceSetup("Chosen", "cmn-CN", "cmn-CN-Chirp3-HD-Alnilam"));
        model.SetVoiceLanguageCatalog(["en-US", "cmn-CN"], clock.Now.AddMinutes(30), "synthetic-scoped-evidence");
        model.VoiceLabCatalogResults.Add(VoiceSelection());
        model.SelectedVoiceLabVoice = VoiceSelection();
        Assert.True(model.HasCurrentVoiceLanguageCatalog);
        Assert.Equal("synthetic-scoped-evidence", model.VoiceLanguageCatalogScope);
        clock.Now = clock.Now.AddMinutes(30);
        Assert.False(model.HasCurrentVoiceLanguageCatalog);
        model.RefreshVoiceLanguageCatalogFreshness();
        Assert.Null(model.VoiceLanguageCatalogScope);
        Assert.Empty(model.VoiceLabCatalogResults);
        Assert.Null(model.SelectedVoiceLabVoice);
        Assert.Contains("expired", model.ConnectedVoiceCatalogStatus, StringComparison.Ordinal);
        Assert.Equal("cmn-CN", model.VoiceLabLocaleFilter);
        Assert.Equal("cmn-CN-Chirp3-HD-Alnilam", model.PreferredVoiceName);
        Assert.Equal(2, model.VoiceLanguageCodes.Count);
        Assert.False(model.CanGenerateWithGoogle);
    }

    [Fact]
    public async Task SavedLanguageChangeDuringDelayedQueryCannotRepublishOldSelection()
    {
        using var model = CreateViewModel();
        model.ApplySavedVoiceSetup(new VoiceSetup("English", "en-US", VoiceSelection().VoiceStableId));
        TaskCompletionSource<IReadOnlyList<VoiceLabCatalogSelection>> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        model.ConfigureStage7VoiceLabCatalog(new VoiceLabCatalogQueryService((_, _) => pending.Task),
            _ => Task.FromResult(CatalogState(accountAuthorized: true)));
        Task refresh = model.RefreshVoiceCatalogForUiAsync(TestContext.Current.CancellationToken);
        model.ApplySavedVoiceSetup(new VoiceSetup("Chinese", "cmn-CN", "cmn-CN-Chirp3-HD-Alnilam"));
        pending.SetResult([VoiceSelection()]);
        await refresh;
        Assert.Empty(model.VoiceLabCatalogResults);
        Assert.Null(model.SelectedVoiceLabVoice);
        Assert.False(model.VoiceLabCatalogLastRefreshSucceeded);
        Assert.Equal("cmn-CN", model.VoiceLabLocaleFilter);
        Assert.Equal("cmn-CN-Chirp3-HD-Alnilam", model.PreferredVoiceName);
        Assert.Contains("changed while loading", model.StatusMessage, StringComparison.Ordinal);
    }

    private sealed class LanguageClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 30, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
