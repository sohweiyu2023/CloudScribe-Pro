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
        page.PrimaryActionLabel = "Export application metadata locally";
        page.PrimaryActionCommand = ExportGenerationDiagnosticsCommand;
        page.StateDescription = "This export contains application version, platform and export time only. It excludes documents, audio, credentials, full logs and crash dumps. Find the local JSON in the workspace's support-bundles folder, available through Setups. Nothing is uploaded.";
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

            StatusMessage = "Application metadata saved locally in workspace/support-bundles";
        }
        catch (Exception exception) when (!IsFatalUiException(exception))
        {
            StatusMessage = "Metadata export failed. Check workspace folder access and disk space, then retry. Your documents and audio were left unchanged.";
        }
        finally
        {
            Volatile.Write(ref _generationDiagnosticsExportInFlight, 0);
            OnPropertyChanged(nameof(CanExportGenerationDiagnostics));
            ExportGenerationDiagnosticsCommand.NotifyCanExecuteChanged();
        }
    }
}
