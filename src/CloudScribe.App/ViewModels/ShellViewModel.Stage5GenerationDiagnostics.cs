using CloudScribe.Application.Generation;
using CommunityToolkit.Mvvm.Input;

namespace CloudScribe.App.ViewModels;

public sealed partial class ShellViewModel
{
    private GenerationSupportBundleExportCoordinator? _generationSupportBundleExport;
    private bool _generationDiagnosticsPolicyAllowed;

    public bool CanExportGenerationDiagnostics =>
        _generationSupportBundleExport is not null && _generationDiagnosticsPolicyAllowed;

    public void ConfigureStage5GenerationDiagnostics(
        GenerationSupportBundleExportCoordinator exportCoordinator,
        bool currentPolicyAllowsDiagnostics)
    {
        _generationSupportBundleExport = exportCoordinator ?? throw new ArgumentNullException(nameof(exportCoordinator));
        _generationDiagnosticsPolicyAllowed = currentPolicyAllowsDiagnostics;
        OnPropertyChanged(nameof(CanExportGenerationDiagnostics));
        ExportGenerationDiagnosticsCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanExportGenerationDiagnostics))]
    private async Task ExportGenerationDiagnosticsAsync(CancellationToken cancellationToken)
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
}
