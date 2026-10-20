using CloudScribe.App.Design;
using CloudScribe.App.Navigation;
using CloudScribe.App.ViewModels;
using CloudScribe.Application.Activation;
using CloudScribe.Application.Diagnostics;
using CloudScribe.Application.Generation;
using CloudScribe.Application.Observability;
using CloudScribe.Domain.Generation;
using CloudScribe.Domain.Observability;
using CloudScribe.Providers.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudScribe.Architecture.Tests;

public sealed partial class ShellViewModelStateTests
{
    [Fact]
    public async Task SamplePreparationCapturesOnlyBoundedTextWithoutEditingTheDocument()
    {
        using ShellViewModel model = CreateViewModel();
        model.DocumentText = "Private full document must not be used for a voice sample.";
        string originalText = model.DocumentText;
        Guid? originalId = model.CurrentDocumentId;
        model.SelectedVoiceLabVoice = VoiceSelection() with { VoiceStableId = "en-US-Standard-C", LanguageCodes = ["en-US"] };
        ShellViewModel.GoogleTtsStudioRequestSelection? captured = null;
        model.ConfigureStage6GoogleGenerationPreparation(_ =>
        {
            captured = model.CaptureGoogleTtsStudioRequestSelection();
            return Task.FromResult(new ShellViewModel.GoogleGenerationSpendReview("USD", 2, 1, "pricing", "en-US-Standard-C", new string('a', 64)));
        });
        Guid sampleId = Guid.NewGuid();
        int invalidations = 0;
        model.ConfigureGoogleRequestInvalidation(() => invalidations++);
        await model.PrepareGoogleVoiceSampleAsync("Only this sample.", sampleId, TestContext.Current.CancellationToken);
        Assert.NotNull(captured);
        Assert.Equal("Only this sample.", captured.ExactText);
        Assert.Equal(sampleId, captured.DocumentId);
        Assert.Null(captured.RevisionId);
        Assert.Equal(originalText, model.DocumentText);
        Assert.Equal(originalId, model.CurrentDocumentId);
        model.CancelGoogleVoiceSampleReview();
        Assert.Null(model.PreparedGoogleGenerationSpendReview);
        Assert.False(model.CanGenerateWithGoogle);
        Assert.True(invalidations > 0);
    }

    [Fact]
    public void UpdateViewportRejectsInvalidGeometryWithoutMutatingLayout()
    {
        using ShellViewModel viewModel = CreateViewModel();
        AdaptiveLayoutState original = viewModel.Layout;

        Assert.Throws<ArgumentOutOfRangeException>(() => viewModel.UpdateViewport(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => viewModel.UpdateViewport(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => viewModel.UpdateViewport(double.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => viewModel.UpdateViewport(double.NegativeInfinity));

        Assert.Equal(original, viewModel.Layout);
    }

    [Theory]
    [InlineData(640, AdaptiveLayoutMode.Narrow)]
    [InlineData(900, AdaptiveLayoutMode.Compact)]
    [InlineData(1200, AdaptiveLayoutMode.Standard)]
    [InlineData(1600, AdaptiveLayoutMode.Full)]
    public void UpdateViewportAppliesFiniteLayoutBands(double width, AdaptiveLayoutMode expected)
    {
        using ShellViewModel viewModel = CreateViewModel();
        AdaptiveLayoutMode originalMode = viewModel.Layout.Mode;
        string originalStatus = viewModel.StatusMessage;

        viewModel.UpdateViewport(width);

        Assert.Equal(expected, viewModel.Layout.Mode);
        Assert.Equal(
            expected == originalMode ? originalStatus : $"Adaptive layout · {expected}",
            viewModel.StatusMessage);
    }

    [Fact]
    public void FocusReadingExitsWhenDocumentContextDisappears()
    {
        using ShellViewModel viewModel = CreateViewModel();
        Assert.True(viewModel.CanUseFocusReading);

        viewModel.IsFocusReading = true;
        viewModel.LifecycleState = WorkspaceLifecycleState.Empty;

        Assert.False(viewModel.IsFocusReading);
        Assert.False(viewModel.CanUseFocusReading);
        Assert.True(viewModel.IsEmptyWorkspaceVisible);
    }

    [Fact]
    public void RouteChangeExitsFocusReadingAndUsesRouteContext()
    {
        using ShellViewModel viewModel = CreateViewModel();
        viewModel.IsFocusReading = true;

        NavigationItem library = viewModel.NavigationItems.Single(
            item => item.Route == AppRoute.Library);
        viewModel.SelectedNavigationItem = library;

        Assert.False(viewModel.IsFocusReading);
        Assert.False(viewModel.CanOpenDocumentSurfaces);
        Assert.Equal("Library", viewModel.CommandContextTitle);
    }

    [Fact]
    public void NullSelectionsRecoverToCanonicalDefaults()
    {
        using ShellViewModel viewModel = CreateViewModel();

        viewModel.SelectedNavigationItem = null;
        viewModel.SelectedThemeOption = null;

        Assert.Equal(AppRoute.Studio, viewModel.SelectedNavigationItem?.Route);
        Assert.Equal(ThemePreference.FollowSystem, viewModel.SelectedThemeOption?.Preference);
    }

    [Fact]
    public void CompletedRunDialogUsesExactCurrentOutputDirectory()
    {
        string outputDirectory = Path.Combine(
            Path.GetTempPath(),
            "cloudscribe-completion-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outputDirectory);
        try
        {
            using ShellViewModel viewModel = CreateViewModel();
            string? requestedDirectory = null;
            viewModel.OutputFolderRequested += (_, eventArgs) => requestedDirectory = eventArgs.OutputDirectory;

            viewModel.ShowRunCompletedDialog("Transcription", outputDirectory, 3);

            Assert.True(viewModel.IsCompletionDialogOpen);
            Assert.Equal("Transcription complete", viewModel.CompletionTitle);
            Assert.Equal("3 output files are ready.", viewModel.CompletionMessage);
            Assert.Equal(Path.GetFullPath(outputDirectory), viewModel.CompletionOutputDirectory);
            Assert.True(viewModel.CanOpenCompletionOutputFolder);

            viewModel.OpenCompletionOutputFolderCommand.Execute(null);
            Assert.Equal(Path.GetFullPath(outputDirectory), requestedDirectory);

            viewModel.CloseTransientSurfacesCommand.Execute(null);
            Assert.False(viewModel.IsCompletionDialogOpen);
        }
        finally
        {
            Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Fact]
    public void CompletedRunDialogDoesNotCrashOnMalformedOutputPath()
    {
        using ShellViewModel viewModel = CreateViewModel();

        viewModel.ShowRunCompletedDialog("Transcription", "invalid\0path", 1);

        Assert.True(viewModel.IsCompletionDialogOpen);
        Assert.False(viewModel.CanOpenCompletionOutputFolder);
        Assert.Equal("Transcription complete · Output folder unavailable", viewModel.StatusMessage);
    }

    [Fact]
    public void CompletedRunDialogDoesNotOpenMissingOlderFolder()
    {
        using ShellViewModel viewModel = CreateViewModel();
        string missing = Path.Combine(
            Path.GetTempPath(),
            "cloudscribe-completion-tests",
            Guid.NewGuid().ToString("N"));
        bool requested = false;
        viewModel.OutputFolderRequested += (_, _) => requested = true;

        viewModel.ShowRunCompletedDialog("Transcription", missing, 1);
        viewModel.OpenCompletionOutputFolderCommand.Execute(null);

        Assert.False(viewModel.CanOpenCompletionOutputFolder);
        Assert.False(requested);
        Assert.Equal("Output folder is unavailable", viewModel.StatusMessage);
    }

    [Fact]
    public async Task GoogleGenerationFailureClearsSingleUseApprovalWithoutEscapingOrExposingPrivateDetails()
    {
        using ShellViewModel viewModel = CreateViewModel();
        int resolutions = 0;
        viewModel.ConfigureStage6GoogleGeneration(_ =>
        {
            resolutions++;
            return Task.FromException<GoogleGenerationUiExecutionContext>(
                new InvalidOperationException("private-document-text credential-secret"));
        });
        ConfigureGoogleSpendReview(viewModel);
        await viewModel.PrepareGoogleGenerationSpendReviewAsync(TestContext.Current.CancellationToken);
        await viewModel.ApproveGoogleGenerationSpendAsync(50, true, TestContext.Current.CancellationToken);
        Assert.True(viewModel.GenerateWithGoogleCommand.CanExecute(null));

        await viewModel.GenerateWithGoogleCommand.ExecuteAsync(null);

        Assert.Equal(1, resolutions);
        Assert.Null(viewModel.PreparedGoogleGenerationSpendReview);
        Assert.False(viewModel.PreparedGoogleGenerationSpendApproved);
        Assert.False(viewModel.GenerateWithGoogleCommand.CanExecute(null));
        Assert.True(viewModel.CanPrepareGoogleGenerationSpend);
        Assert.Equal(
            "Google generation stopped · approval cleared; review queue status before preparing another request",
            viewModel.StatusMessage);

        await viewModel.PrepareGoogleGenerationSpendReviewAsync(TestContext.Current.CancellationToken);
        Assert.False(viewModel.CanGenerateWithGoogle);
        Assert.True(viewModel.CanApproveGoogleGenerationSpend);
    }

    [Fact]
    public async Task GoogleGenerationCancellationClearsApprovalAndReleasesOperationState()
    {
        using ShellViewModel viewModel = CreateViewModel();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.ConfigureStage6GoogleGeneration(async cancellationToken =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("Cancellation was expected.");
        });
        ConfigureGoogleSpendReview(viewModel);
        await viewModel.PrepareGoogleGenerationSpendReviewAsync(TestContext.Current.CancellationToken);
        await viewModel.ApproveGoogleGenerationSpendAsync(50, true, TestContext.Current.CancellationToken);

        Task execution = viewModel.GenerateWithGoogleCommand.ExecuteAsync(null);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        viewModel.GenerateWithGoogleCommand.Cancel();
        await execution;

        Assert.Null(viewModel.PreparedGoogleGenerationSpendReview);
        Assert.False(viewModel.PreparedGoogleGenerationSpendApproved);
        Assert.False(viewModel.CanGenerateWithGoogle);
        Assert.True(viewModel.CanPrepareGoogleGenerationSpend);
        Assert.Equal(
            "Google generation cancelled · approval cleared; check queue status before another request",
            viewModel.StatusMessage);
    }

    [Fact]
    public async Task GoogleGenerationFatalFailureIsNotSuppressed()
    {
        using ShellViewModel viewModel = CreateViewModel();
#pragma warning disable CA2201 // Deliberately inject a fatal exception to verify that the UI catch filter preserves it.
        viewModel.ConfigureStage6GoogleGeneration(_ =>
            Task.FromException<GoogleGenerationUiExecutionContext>(new AccessViolationException()));
#pragma warning restore CA2201
        ConfigureGoogleSpendReview(viewModel);
        await viewModel.PrepareGoogleGenerationSpendReviewAsync(TestContext.Current.CancellationToken);
        await viewModel.ApproveGoogleGenerationSpendAsync(50, true, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<AccessViolationException>(() => viewModel.GenerateWithGoogleCommand.ExecuteAsync(null));

        Assert.Null(viewModel.PreparedGoogleGenerationSpendReview);
        Assert.False(viewModel.PreparedGoogleGenerationSpendApproved);
        Assert.True(viewModel.CanPrepareGoogleGenerationSpend);
    }

    [Fact]
    public async Task GooglePlaybackWithoutAcceptedOutputReportsFailureWithoutGenerating()
    {
        using ShellViewModel viewModel = CreateViewModel();
        bool outputRequested = false;
        viewModel.ConfigureStage6GoogleGenerationOutput(
            (_, _) =>
            {
                outputRequested = true;
                return Task.FromResult("unused.mp3");
            },
            (_, _) => Task.CompletedTask);

        await viewModel.PlayLastGeneratedGoogleMp3Command.ExecuteAsync(null);

        Assert.False(outputRequested);
        Assert.Null(viewModel.LastGeneratedGoogleMp3Path);
        Assert.False(viewModel.CanPlayLastGeneratedGoogleMp3);
        Assert.Equal(
            "Google MP3 playback unavailable · the verified output could not be opened; no new generation was started",
            viewModel.StatusMessage);
    }

    [Fact]
    public async Task VoiceCatalogWithoutAccountAuthorizationStaysUsableWithoutDispatch()
    {
        using ShellViewModel viewModel = CreateViewModel();
        int requests = 0;
        var catalog = new VoiceLabCatalogQueryService((_, _) =>
        {
            requests++;
            return Task.FromResult<IReadOnlyList<VoiceLabCatalogSelection>>([]);
        });
        viewModel.ConfigureStage7VoiceLabCatalog(catalog, _ => Task.FromResult(CatalogState(accountAuthorized: false)));

        await viewModel.RefreshVoiceLabCatalogCommand.ExecuteAsync(null);

        Assert.Equal(0, requests);
        Assert.Empty(viewModel.VoiceLabCatalogResults);
        Assert.True(viewModel.CanRefreshVoiceLabCatalog);
        Assert.True(viewModel.RefreshVoiceLabCatalogCommand.CanExecute(null));
        Assert.Equal(
            "Voice Lab unavailable · connect an account and check its access, connection, and filters before refreshing",
            viewModel.StatusMessage);
    }

    [Fact]
    public async Task VoiceCatalogFailureDoesNotRetryOrExposePrivateDetailsAndAllowsExplicitRefresh()
    {
        using ShellViewModel viewModel = CreateViewModel();
        int requests = 0;
        var catalog = new VoiceLabCatalogQueryService((_, _) =>
        {
            requests++;
            return requests == 1
                ? Task.FromException<IReadOnlyList<VoiceLabCatalogSelection>>(
                    new InvalidOperationException("private-account credential-secret"))
                : Task.FromResult<IReadOnlyList<VoiceLabCatalogSelection>>([VoiceSelection()]);
        });
        viewModel.ConfigureStage7VoiceLabCatalog(catalog, _ => Task.FromResult(CatalogState(accountAuthorized: true)));

        await viewModel.RefreshVoiceLabCatalogCommand.ExecuteAsync(null);

        Assert.Equal(1, requests);
        Assert.True(viewModel.RefreshVoiceLabCatalogCommand.CanExecute(null));
        Assert.Equal(
            "Voice Lab unavailable · connect an account and check its access, connection, and filters before refreshing",
            viewModel.StatusMessage);

        await viewModel.RefreshVoiceLabCatalogCommand.ExecuteAsync(null);

        Assert.Equal(2, requests);
        Assert.Single(viewModel.VoiceLabCatalogResults);
        Assert.Equal(VoiceSelection(), viewModel.SelectedVoiceLabVoice);
        Assert.Equal("Voice Lab · 1 trusted voices", viewModel.StatusMessage);
    }

    [Fact]
    public async Task VoiceCatalogCancellationReleasesCommandWithoutRetry()
    {
        using ShellViewModel viewModel = CreateViewModel();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int requests = 0;
        var catalog = new VoiceLabCatalogQueryService(async (_, cancellationToken) =>
        {
            requests++;
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return Array.Empty<VoiceLabCatalogSelection>();
        });
        viewModel.ConfigureStage7VoiceLabCatalog(catalog, _ => Task.FromResult(CatalogState(accountAuthorized: true)));

        Task execution = viewModel.RefreshVoiceLabCatalogCommand.ExecuteAsync(null);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        viewModel.RefreshVoiceLabCatalogCommand.Cancel();
        await execution;

        Assert.Equal(1, requests);
        Assert.True(viewModel.RefreshVoiceLabCatalogCommand.CanExecute(null));
        Assert.Equal("Voice Lab refresh cancelled", viewModel.StatusMessage);
    }

    [Fact]
    public async Task VoiceAuditionFailureDoesNotRetryOrExposePrivateDetailsAndReleasesCommand()
    {
        using ShellViewModel viewModel = CreateViewModel();
        VoiceLabCatalogSelection selected = VoiceSelection();
        int preparations = 0;
        int refreshes = 0;
        viewModel.SelectedVoiceLabVoice = selected;
        viewModel.ConfigureStage7VoiceLabAudition(
            (_, _, _) =>
            {
                preparations++;
                return Task.FromException<VoiceLabAuditionExecutionService>(
                    new InvalidOperationException("private-audition-text credential-secret"));
            },
            (_, _) => Task.FromResult(new VoiceLabAuditionRequest(
                selected, CachePolicyEligible: false, ForceFresh: true,
                ExplicitSpendApproved: false, PricingCurrent: true, OutputFormat: "mp3")),
            (selection, _) =>
            {
                refreshes++;
                return Task.FromResult(selection);
            });

        await viewModel.AuditionSelectedVoiceCommand.ExecuteAsync(null);

        Assert.Equal(1, preparations);
        Assert.Equal(1, refreshes);
        Assert.Same(selected, viewModel.SelectedVoiceLabVoice);
        Assert.True(viewModel.AuditionSelectedVoiceCommand.CanExecute(null));
        Assert.Equal(
            "Voice Lab audition stopped · review queue and spend status before preparing another audition",
            viewModel.StatusMessage);
    }

    private static VoiceLabCatalogUiState CatalogState(bool accountAuthorized) => new(
        new VoiceLabCatalogQuery("provider", "account", "project", null, null, false),
        accountAuthorized,
        ProjectAuthorized: true,
        PrivateVoiceAccessAuthorized: false);

    private static VoiceLabCatalogSelection VoiceSelection() => new(
        VoiceStableId: "voice",
        ProviderStableId: "provider",
        AccountStableId: "account",
        ProjectStableId: "project",
        CapabilityEvidenceId: "capability",
        VoiceFingerprint: "fingerprint",
        CapabilityCurrent: true,
        VoiceEnabled: true,
        AccountProjectAuthorized: true);

    private static void ConfigureGoogleSpendReview(ShellViewModel viewModel)
    {
        viewModel.ConfigureStage6GoogleGenerationPreparation(_ => Task.FromResult(
            new ShellViewModel.GoogleGenerationSpendReview("USD", 2, 50, "pricing-test", "voice-test", new string('a', 64))));
        viewModel.ConfigureStage6GoogleGenerationSpendApproval((_, _, _) => Task.CompletedTask);
        viewModel.ConfigureStage6GoogleGenerationOutput(
            (_, _) => Task.FromResult("unused.mp3"),
            (_, _) => Task.CompletedTask);
    }

    private static ShellViewModel CreateViewModel() => new(
        new StubTimelineStore(),
        new StubSupportBundleService(),
        new StubDiagnosticLogStatus(),
        new StubProviderFactoryRegistry(),
        new StubActivationRouter(),
        TimeProvider.System,
        NullLogger<ShellViewModel>.Instance);

    private sealed class StubTimelineStore : IActivityTimelineStore
    {
        public Task AppendAsync(ActivityTimelineEntry entry, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(entry);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ActivityTimelineEntry>> GetRecentAsync(
            int maximumCount,
            CancellationToken cancellationToken = default)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCount);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<ActivityTimelineEntry>>([]);
        }
    }

    private sealed class StubSupportBundleService : ISupportBundleService
    {
        public Task<SupportBundlePreview> PreviewAsync(CancellationToken cancellationToken = default) =>
            Task.FromException<SupportBundlePreview>(new NotSupportedException());

        public Task<string> CreateAsync(
            string destinationDirectory,
            CancellationToken cancellationToken = default) =>
            Task.FromException<string>(new NotSupportedException());
    }

    private sealed class StubDiagnosticLogStatus : IDiagnosticLogStatus
    {
        public event EventHandler? StatusChanged
        {
            add { }
            remove { }
        }

        public bool IsAvailable => true;

        public string LogDirectory => Path.Combine(Path.GetTempPath(), "cloudscribe-test-logs");

        public string CurrentLogPath => Path.Combine(LogDirectory, "cloudscribe-test.jsonl");

        public long DroppedRecordCount => 0;
    }

    private sealed class StubProviderFactoryRegistry : IProviderFactoryRegistry
    {
        public IReadOnlyList<ProviderDescriptor> AvailableProviders { get; } = [];

        public bool TryGetFactory(string stableProviderId, out IProviderAdapterFactory? factory)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(stableProviderId);
            factory = null;
            return false;
        }
    }

    private sealed class StubActivationRouter : IActivationRouter
    {
        public event EventHandler<ActivationReceivedEventArgs>? ActivationReceived
        {
            add { }
            remove { }
        }

        public void Route(ActivationReceivedEventArgs request)
        {
            ArgumentNullException.ThrowIfNull(request);
        }
    }
}
