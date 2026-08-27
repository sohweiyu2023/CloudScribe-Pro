using CloudScribe.App.Navigation;
using CloudScribe.Application.Generation;
using CommunityToolkit.Mvvm.Input;

namespace CloudScribe.App.ViewModels;

public sealed partial class ShellViewModel
{
    private GenerationSupportBundleExportCoordinator? _generationSupportBundleExport;
    private bool _generationDiagnosticsPolicyAllowed;
    private int _generationDiagnosticsExportInFlight;

    public bool CanExportGenerationDiagnostics =>
        _generationSupportBundleExport is not null &&
        _generationDiagnosticsPolicyAllowed &&
        Volatile.Read(ref _generationDiagnosticsExportInFlight) == 0;

    public void ConfigureStage5GenerationDiagnostics(
        GenerationSupportBundleExportCoordinator exportCoordinator,
        bool currentPolicyAllowsDiagnostics)
    {
        _generationSupportBundleExport = exportCoordinator ?? throw new ArgumentNullException(nameof(exportCoordinator));
        _generationDiagnosticsPolicyAllowed = currentPolicyAllowsDiagnostics;
        OnPropertyChanged(nameof(CanExportGenerationDiagnostics));
        ExportGenerationDiagnosticsCommand.NotifyCanExecuteChanged();
        RefreshGenerationDiagnosticsRouteAction();
    }

    private void RefreshGenerationDiagnosticsRouteAction()
    {
        if (!_pages.TryGetValue(CloudScribe.App.Navigation.AppRoute.Diagnostics, out RoutePageViewModel? page) || page is null)
        {
            return;
        }

        if (_generationSupportBundleExport is null || !_generationDiagnosticsPolicyAllowed)
        {
            return;
        }

        page.HasPrimaryAction = true;
        page.PrimaryActionLabel = "Export generation diagnostics";
        page.PrimaryActionCommand = ExportGenerationDiagnosticsCommand;
    }

    [RelayCommand(CanExecute = nameof(CanExportGenerationDiagnostics))]
    private async Task ExportGenerationDiagnosticsAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _generationDiagnosticsExportInFlight, 1, 0) != 0)
        {
            throw new InvalidOperationException("A generation diagnostic export is already in progress.");
        }

        OnPropertyChanged(nameof(CanExportGenerationDiagnostics));
        ExportGenerationDiagnosticsCommand.NotifyCanExecuteChanged();

        try
        {
            var exportCoordinator = _generationSupportBundleExport
                ?? throw new InvalidOperationException("Generation diagnostic export is not configured.");

            var applicationVersion = typeof(ShellViewModel).Assembly.GetName().Version?.ToString() ?? "unknown";
            var platform = OperatingSystem.IsWindows()
                ? "windows"
                : OperatingSystem.IsMacOS()
                    ? "macos"
                    : OperatingSystem.IsLinux()
                        ? "linux"
                        : "unknown";

            var metadata = new GenerationSupportBundleMetadata(
                applicationVersion,
                platform,
                "generation-support",
                DateTimeOffset.UtcNow);

            await exportCoordinator.ExportAsync(
                userExplicitlyRequestedDiagnosticBundle: true,
                currentPolicyAllowsDiagnostics: _generationDiagnosticsPolicyAllowed,
                metadata,
                cancellationToken).ConfigureAwait(true);

            StatusMessage = "Generation diagnostics exported · metadata only";
        }
        finally
        {
            Volatile.Write(ref _generationDiagnosticsExportInFlight, 0);
            OnPropertyChanged(nameof(CanExportGenerationDiagnostics));
            ExportGenerationDiagnosticsCommand.NotifyCanExecuteChanged();
        }
    }
}
