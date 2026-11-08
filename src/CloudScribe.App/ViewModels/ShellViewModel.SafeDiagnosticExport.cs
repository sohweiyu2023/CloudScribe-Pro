using CloudScribe.App.Navigation;
using CloudScribe.Application.Logging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CloudScribe.App.ViewModels;

public sealed partial class ShellViewModel
{
    private string? _safeDiagnosticExportDestination;
    private CancellationTokenSource? _safeDiagnosticExportCancellation;
    private int _safeDiagnosticExportInFlight;

    [ObservableProperty]
    private string _safeDiagnosticExportStatus = "No safe history ZIP has been exported in this session.";

    public bool IsSafeDiagnosticExportVisible =>
        SelectedNavigationItem?.Route == AppRoute.Diagnostics && _safeDiagnosticExportDestination is not null;

    public string SafeDiagnosticExportDestination => _safeDiagnosticExportDestination ?? "Not configured";

    public string SafeDiagnosticExportDisclosure =>
        "This separate ZIP contains allowlisted metadata from available local logs and permanent safe trend history, plus a generated manifest. It excludes documents, audio, databases, credentials, private provider captures, request/response bodies and free-form exception text. All captured records are streamed; malformed, oversized or unsupported records have omission markers. Existing records are not rewritten. Nothing is uploaded and no provider request is sent.";

    public bool CanExportSafeDiagnosticHistory => _safeDiagnosticExportDestination is not null
        && Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _safeDiagnosticExportInFlight) == 0;

    public bool CanCancelSafeDiagnosticHistory => Volatile.Read(ref _safeDiagnosticExportInFlight) != 0;

    public void ConfigureSafeDiagnosticExport(string destinationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!Path.IsPathFullyQualified(destinationDirectory))
            throw new ArgumentException("A fully qualified local support-bundle directory is required.", nameof(destinationDirectory));
        if (_safeDiagnosticExportDestination is not null)
            throw new InvalidOperationException("The local support-bundle destination is already configured.");
        _safeDiagnosticExportDestination = Path.GetFullPath(destinationDirectory);
        OnPropertyChanged(nameof(SafeDiagnosticExportDestination));
        OnPropertyChanged(nameof(IsSafeDiagnosticExportVisible));
        NotifySafeDiagnosticExportState();
    }

    [RelayCommand(CanExecute = nameof(CanExportSafeDiagnosticHistory))]
    private async Task ExportSafeDiagnosticHistoryAsync()
    {
        if (_safeDiagnosticExportDestination is not { } destination || Volatile.Read(ref _disposed) != 0
            || Interlocked.CompareExchange(ref _safeDiagnosticExportInFlight, 1, 0) != 0) return;
        using var cancellation = new CancellationTokenSource();
        Volatile.Write(ref _safeDiagnosticExportCancellation, cancellation);
        NotifySafeDiagnosticExportState();
        SafeDiagnosticExportStatus = "Creating the safe history ZIP locally. You can cancel this export; existing records are not rewritten.";
        try
        {
            if (Volatile.Read(ref _disposed) != 0) cancellation.Cancel();
            string path = await Task.Run(() => _supportBundles.CreateAsync(destination, cancellation.Token), cancellation.Token).ConfigureAwait(true);
            SafeDiagnosticExportStatus = $"Saved locally: {path}\nNothing was uploaded. Review the ZIP before sharing it.";
            StatusMessage = "Safe diagnostic history ZIP saved locally";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            SafeDiagnosticExportStatus = "Export cancelled. No ZIP was published by this attempt. Existing log and history records are not rewritten.";
            StatusMessage = "Safe diagnostic history export cancelled";
        }
        catch (Exception error) when (!IsFatalUiException(error))
        {
            SafeDiagnosticExportStatus = "Export failed. Check workspace folder access, available disk space and whether diagnostic files are locked or linked, then retry explicitly. Existing log and history records are not rewritten.";
            StatusMessage = "Safe diagnostic history export failed";
            CloudScribeLog.Observe(() => LocalRequestDiagnostics.Failure(_logger, Guid.NewGuid(), LocalRequestStage.Export, error, _timeProvider.GetUtcNow()));
        }
        finally
        {
            Interlocked.CompareExchange(ref _safeDiagnosticExportCancellation, null, cancellation);
            Volatile.Write(ref _safeDiagnosticExportInFlight, 0);
            NotifySafeDiagnosticExportState();
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancelSafeDiagnosticHistory))]
    private void CancelSafeDiagnosticHistory()
    {
        try { Volatile.Read(ref _safeDiagnosticExportCancellation)?.Cancel(); }
        catch (ObjectDisposedException) { /* A concurrently completed export has already disposed its cancellation source. */ }
    }

    private void NotifySafeDiagnosticExportState()
    {
        OnPropertyChanged(nameof(CanExportSafeDiagnosticHistory));
        OnPropertyChanged(nameof(CanCancelSafeDiagnosticHistory));
        ExportSafeDiagnosticHistoryCommand.NotifyCanExecuteChanged();
        CancelSafeDiagnosticHistoryCommand.NotifyCanExecuteChanged();
    }
}
