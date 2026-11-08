using CloudScribe.App.Navigation;
using CloudScribe.App.ViewModels;
using CloudScribe.Application.Diagnostics;
using CloudScribe.Application.Generation;

namespace CloudScribe.Architecture.Tests;

public sealed partial class ShellViewModelStateTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SafeHistoryExportIsSeparateFromTheMetadataOnlyDiagnosticsAction(bool configureMetadataFirst)
    {
        var service = new SafeHistoryExportStub();
        using ShellViewModel model = CreateViewModel(supportBundles: service);
        int metadataExports = 0;
        Assert.False(model.CanExportSafeDiagnosticHistory);
        void ConfigureMetadata() => model.ConfigureStage5GenerationDiagnostics(new GenerationSupportBundleExportCoordinator(
            new GenerationSupportBundleService(), (_, _) => { metadataExports++; return Task.CompletedTask; }), true);
        if (configureMetadataFirst) ConfigureMetadata();
        model.ConfigureSafeDiagnosticExport(SafeHistoryDestination);
        if (!configureMetadataFirst) ConfigureMetadata();
        Assert.False(model.IsSafeDiagnosticExportVisible);
        model.SelectedNavigationItem = model.NavigationItems.Single(x => x.Route == AppRoute.Diagnostics);
        Assert.True(model.IsSafeDiagnosticExportVisible);
        Assert.Same(model.ExportGenerationDiagnosticsCommand, model.CurrentPage.PrimaryActionCommand);
        Assert.Equal("Export application metadata locally", model.CurrentPage.PrimaryActionLabel);
        Assert.Contains("full logs", model.CurrentPage.StateDescription, StringComparison.Ordinal);
        Assert.Contains("permanent safe trend history", model.SafeDiagnosticExportDisclosure, StringComparison.Ordinal);
        Assert.Contains("private provider captures", model.SafeDiagnosticExportDisclosure, StringComparison.Ordinal);
        Assert.Contains("Nothing is uploaded", model.SafeDiagnosticExportDisclosure, StringComparison.Ordinal);
        Assert.Equal(Path.GetFullPath(SafeHistoryDestination), model.SafeDiagnosticExportDestination);
        await model.ExportGenerationDiagnosticsCommand.ExecuteAsync(null);
        Assert.Equal(1, metadataExports);
        Assert.Equal(0, service.CreateCalls);
        model.SelectedNavigationItem = model.NavigationItems.Single(x => x.Route == AppRoute.Studio);
        Assert.False(model.IsSafeDiagnosticExportVisible);
    }

    [Fact]
    public async Task SafeHistoryExportUsesDisclosedDestinationAndLeavesDocumentsUnchanged()
    {
        var service = new SafeHistoryExportStub();
        using ShellViewModel model = CreateViewModel(supportBundles: service);
        model.DocumentText = "PRIVATE_DOCUMENT_UNCHANGED";
        model.ConfigureSafeDiagnosticExport(SafeHistoryDestination);
        await model.ExportSafeDiagnosticHistoryCommand.ExecuteAsync(null);
        Assert.Equal(1, service.CreateCalls);
        Assert.Equal(model.SafeDiagnosticExportDestination, service.Destination);
        Assert.Contains(Path.Combine(SafeHistoryDestination, "synthetic-safe-history.zip"), model.SafeDiagnosticExportStatus, StringComparison.Ordinal);
        Assert.Contains("Nothing was uploaded", model.SafeDiagnosticExportStatus, StringComparison.Ordinal);
        Assert.Equal("PRIVATE_DOCUMENT_UNCHANGED", model.DocumentText);
        Assert.True(model.CanExportSafeDiagnosticHistory);
        Assert.False(model.CanCancelSafeDiagnosticHistory);
    }

    [Fact]
    public async Task DuplicateExportInvocationDoesNotStartOrCancelAnotherExport()
    {
        var service = new SafeHistoryExportStub();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Export = async (destination, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token).ConfigureAwait(false);
            return Path.Combine(destination, "synthetic-safe-history.zip");
        };
        using ShellViewModel model = CreateViewModel(supportBundles: service);
        model.ConfigureSafeDiagnosticExport(SafeHistoryDestination);
        Task first = model.ExportSafeDiagnosticHistoryCommand.ExecuteAsync(null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(model.CanExportSafeDiagnosticHistory);
        Assert.False(model.ExportSafeDiagnosticHistoryCommand.CanExecute(null));
        Assert.True(model.CancelSafeDiagnosticHistoryCommand.CanExecute(null));
        await model.ExportSafeDiagnosticHistoryCommand.ExecuteAsync(null);
        Assert.Equal(1, service.CreateCalls);
        Assert.False(first.IsCompleted);
        release.TrySetResult();
        await first.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(model.CanExportSafeDiagnosticHistory);
        Assert.Contains("Saved locally", model.SafeDiagnosticExportStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancelSafeHistoryExportWaitsForCleanupAndAllowsExplicitRetry()
    {
        var service = new SafeHistoryExportStub();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Export = async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            return "not-published.zip";
        };
        using ShellViewModel model = CreateViewModel(supportBundles: service);
        model.ConfigureSafeDiagnosticExport(SafeHistoryDestination);
        Task export = model.ExportSafeDiagnosticHistoryCommand.ExecuteAsync(null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        model.CancelSafeDiagnosticHistoryCommand.Execute(null);
        await export.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, service.CreateCalls);
        Assert.Contains("Export cancelled", model.SafeDiagnosticExportStatus, StringComparison.Ordinal);
        Assert.DoesNotContain("Saved locally", model.SafeDiagnosticExportStatus, StringComparison.Ordinal);
        Assert.True(model.CanExportSafeDiagnosticHistory);
        Assert.False(model.CanCancelSafeDiagnosticHistory);
        service.Export = SafeHistoryExportStub.Success;
        await model.ExportSafeDiagnosticHistoryCommand.ExecuteAsync(null);
        Assert.Equal(2, service.CreateCalls);
        Assert.Contains("Saved locally", model.SafeDiagnosticExportStatus, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SafeHistoryFailureIsPrivateAndRequiresAnExplicitRetry(bool accessDenied)
    {
        var service = new SafeHistoryExportStub();
        service.Export = (_, _) => Task.FromException<string>(accessDenied
            ? new UnauthorizedAccessException("PRIVATE_SECRET_PATH") : new IOException("PRIVATE_SECRET_PATH"));
        using ShellViewModel model = CreateViewModel(supportBundles: service);
        model.ConfigureSafeDiagnosticExport(SafeHistoryDestination);
        await model.ExportSafeDiagnosticHistoryCommand.ExecuteAsync(null);
        Assert.Equal(1, service.CreateCalls);
        Assert.Contains("Export failed", model.SafeDiagnosticExportStatus, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE_SECRET_PATH", model.SafeDiagnosticExportStatus + model.StatusMessage, StringComparison.Ordinal);
        Assert.True(model.CanExportSafeDiagnosticHistory);
        service.Export = SafeHistoryExportStub.Success;
        await model.ExportSafeDiagnosticHistoryCommand.ExecuteAsync(null);
        Assert.Equal(2, service.CreateCalls);
        Assert.Contains("Saved locally", model.SafeDiagnosticExportStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShellDisposalCancelsSafeHistoryExportAndCannotRestartIt()
    {
        var service = new SafeHistoryExportStub();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Export = async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            return "not-published.zip";
        };
        using ShellViewModel model = CreateViewModel(supportBundles: service);
        model.ConfigureSafeDiagnosticExport(SafeHistoryDestination);
        Task export = model.ExportSafeDiagnosticHistoryCommand.ExecuteAsync(null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        model.Dispose();
        await export.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Export cancelled", model.SafeDiagnosticExportStatus, StringComparison.Ordinal);
        Assert.False(model.CanExportSafeDiagnosticHistory);
        await model.ExportSafeDiagnosticHistoryCommand.ExecuteAsync(null);
        Assert.Equal(1, service.CreateCalls);
    }

    [Fact]
    public void SafeHistoryDestinationMustBeExplicitAndCannotBeReconfigured()
    {
        using ShellViewModel model = CreateViewModel();
        Assert.Throws<ArgumentException>(() => model.ConfigureSafeDiagnosticExport("relative-support-bundles"));
        Assert.False(model.CanExportSafeDiagnosticHistory);
        model.ConfigureSafeDiagnosticExport(SafeHistoryDestination);
        Assert.Throws<InvalidOperationException>(() => model.ConfigureSafeDiagnosticExport(Path.Combine(SafeHistoryDestination, "other")));
        Assert.Equal(Path.GetFullPath(SafeHistoryDestination), model.SafeDiagnosticExportDestination);
    }

    private static string SafeHistoryDestination => Path.Combine(Path.GetTempPath(), "cloudscribe-synthetic-safe-history");

    private sealed class SafeHistoryExportStub : ISupportBundleService
    {
        private int _calls;
        internal int CreateCalls => Volatile.Read(ref _calls);
        internal string? Destination { get; private set; }
        internal Func<string, CancellationToken, Task<string>> Export { get; set; } = Success;
        internal static Task<string> Success(string destination, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(Path.Combine(destination, "synthetic-safe-history.zip"));
        }
        public Task<SupportBundlePreview> PreviewAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> CreateAsync(string destinationDirectory, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            Destination = destinationDirectory;
            return Export(destinationDirectory, cancellationToken);
        }
    }
}
