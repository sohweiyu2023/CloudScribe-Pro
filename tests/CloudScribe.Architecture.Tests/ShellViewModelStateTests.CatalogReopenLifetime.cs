using CloudScribe.App.Composition;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Architecture.Tests;

public sealed partial class ShellViewModelStateTests
{
    [Fact]
    public async Task CatalogReopenThousandVoicePersistedCatalogFitsRealWindowTimeout()
    {
        await using var f = await CatalogReopenFixture.CreateAsync(1000);
        var current = (await f.Capabilities.GetLatestAsync(GoogleGenerationProvider.StableProviderId,
            CatalogReopenFixture.Account, TestContext.Current.CancellationToken))!;
        using var model = f.NewModel();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        await f.Loader.OpenAsync(model, timeout.Token);
        elapsed.Stop();
        TestContext.Current.TestOutputHelper!.WriteLine("Real SQLite reopening with 1,003 catalog voices: {0:F3} seconds.", elapsed.Elapsed.TotalSeconds);
        Assert.True(model.HasCurrentVoiceLanguageCatalog, model.ConnectedVoiceCatalogStatus);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(30));
        Assert.Equal(2, model.VoiceLabCatalogResults.Count);
        Assert.All(model.VoiceLabCatalogResults, voice => Assert.Equal(current.Id.ToString("D"), voice.CapabilityEvidenceId));
        var retained = (await f.Capabilities.GetLatestAsync(GoogleGenerationProvider.StableProviderId,
            CatalogReopenFixture.Account, TestContext.Current.CancellationToken))!;
        Assert.Equal(current.ExpiresAtUtc, retained.ExpiresAtUtc);
        Assert.Equal(0, f.Handler.PaidRequests);
    }

    [Fact]
    public async Task CatalogReopenCancelsDelayedOpeningAndReopensWithoutPublishingPaidState()
    {
        await using var f = await CatalogReopenFixture.CreateAsync();
        string capability = (await f.Capabilities.GetLatestAsync(GoogleGenerationProvider.StableProviderId,
            CatalogReopenFixture.Account, TestContext.Current.CancellationToken))!.Id.ToString("D");
        using var model = f.NewModel();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        f.Handler.BlockCatalogAt = 2;
        Task opening = f.Loader.OpenAsync(model, cancel.Token);
        await f.Handler.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        await cancel.CancelAsync();
        await opening;
        Assert.False(model.IsConnectedVoiceCatalogLoading);
        Assert.False(model.HasCurrentVoiceLanguageCatalog);
        Assert.Null(model.SelectedVoiceLabVoice);
        Assert.Empty(model.VoiceLabCatalogResults);
        f.Handler.BlockCatalogAt = 0;
        await f.OpenAsync(model);
        Assert.True(model.HasCurrentVoiceLanguageCatalog, model.ConnectedVoiceCatalogStatus);
        Assert.Equal(capability, model.SelectedVoiceLabVoice!.CapabilityEvidenceId);
        Assert.Equal(0, f.Handler.PaidRequests);
    }

    [Fact]
    public async Task CatalogReopenConcurrentOpeningUsesOneInFlightLoadAndClearsTransientReview()
    {
        await using var f = await CatalogReopenFixture.CreateAsync();
        using var model = f.NewModel();
        await f.OpenAsync(model);
        ConfigureGoogleSpendReview(model);
        await model.PrepareGoogleGenerationSpendReviewAsync(TestContext.Current.CancellationToken);
        await model.ApproveGoogleGenerationSpendAsync(50, true, TestContext.Current.CancellationToken);
        Assert.True(model.PreparedGoogleGenerationSpendApproved);
        int previous = f.Handler.TotalRequests;
        f.Handler.BlockCatalogAt = f.Handler.CatalogRequests + 1;
        Task opening = f.OpenAsync(model);
        await f.Handler.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Null(model.PreparedGoogleGenerationSpendReview);
        Assert.False(model.PreparedGoogleGenerationSpendApproved);
        Assert.False(model.CanGenerateWithGoogle);
        await f.OpenAsync(model);
        Assert.Equal(previous + 1, f.Handler.TotalRequests);
        f.Handler.Release.TrySetResult();
        await opening;
        Assert.True(model.HasCurrentVoiceLanguageCatalog, model.ConnectedVoiceCatalogStatus);
        Assert.Equal(previous + 3, f.Handler.TotalRequests);
        Assert.Null(model.PreparedGoogleGenerationSpendReview);
        Assert.False(model.PreparedGoogleGenerationSpendApproved);
        Assert.Equal(0, f.Handler.PaidRequests);
    }

    [Fact]
    public async Task CatalogReopenRejectsMetadataDriftBetweenBootstrapAndUiCatalogQuery()
    {
        await using var f = await CatalogReopenFixture.CreateAsync();
        using var model = f.NewModel();
        f.Handler.DriftCatalogAt = 3;
        await f.OpenAsync(model);
        Assert.False(model.HasCurrentVoiceLanguageCatalog);
        Assert.Empty(model.VoiceLabCatalogResults);
        Assert.Null(model.SelectedVoiceLabVoice);
        Assert.False(model.IsConnectedVoiceCatalogLoading);
        f.Handler.DriftCatalogAt = 0;
        await f.OpenAsync(model);
        Assert.True(model.HasCurrentVoiceLanguageCatalog, model.ConnectedVoiceCatalogStatus);
        Assert.Equal(0, f.Handler.PaidRequests);
    }
}
