using CloudScribe.App.Design;
using CloudScribe.App.Navigation;
using CloudScribe.App.ViewModels;
using CloudScribe.Application.Activation;
using CloudScribe.Application.Diagnostics;
using CloudScribe.Application.Observability;
using CloudScribe.Domain.Observability;
using CloudScribe.Providers.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudScribe.Architecture.Tests;

public sealed class ShellViewModelStateTests
{
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
