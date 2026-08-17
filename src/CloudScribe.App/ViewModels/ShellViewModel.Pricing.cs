using System.Collections.ObjectModel;
using Avalonia.Threading;
using CloudScribe.App.Navigation;
using CloudScribe.Application.Pricing;

namespace CloudScribe.App.ViewModels;

public sealed partial class ShellViewModel
{
    private IPricingCatalogHistoryStore? _pricingCatalogHistoryStore;
    private int _pricingHistoryStarted;

    public ObservableCollection<string> PricingCatalogHistoryItems { get; } = [];

    public string PricingCatalogActiveSummary { get; private set; } = "No active pricing catalog";

    public bool IsPricingSelected => SelectedNavigationItem?.Route == AppRoute.Pricing;

    public void ConfigureStage4PricingHistory(IPricingCatalogHistoryStore historyStore)
    {
        ArgumentNullException.ThrowIfNull(historyStore);
        if (Interlocked.CompareExchange(ref _pricingCatalogHistoryStore, historyStore, null) is not null)
        {
            throw new InvalidOperationException("Stage 4 pricing history is already configured.");
        }
    }

    public void SchedulePricingHistoryStart()
    {
        if (IsStage2VisualCaptureRequested())
        {
            return;
        }

        Dispatcher.UIThread.Post(StartPricingHistory, DispatcherPriority.Loaded);
    }

    private void StartPricingHistory()
    {
        if (Interlocked.Exchange(ref _pricingHistoryStarted, 1) != 0)
        {
            return;
        }
        _ = RefreshPricingHistoryObservedAsync();
    }

    private async Task RefreshPricingHistoryObservedAsync()
    {
        try
        {
            if (_pricingCatalogHistoryStore is null)
            {
                return;
            }

            IReadOnlyList<PricingCatalogSnapshot> snapshots = await _pricingCatalogHistoryStore
                .ListSnapshotsAsync()
                .ConfigureAwait(true);
            PricingCatalogSnapshot? active = await _pricingCatalogHistoryStore
                .GetActiveSnapshotAsync()
                .ConfigureAwait(true);

            PricingCatalogHistoryItems.Clear();
            foreach (PricingCatalogSnapshot snapshot in snapshots.Take(8))
            {
                string activeMarker = active?.Id == snapshot.Id ? "ACTIVE · " : string.Empty;
                PricingCatalogHistoryItems.Add(
                    $"{activeMarker}{snapshot.TrustState} · {snapshot.Source.Label} · {snapshot.Sha256[..12]}…");
            }

            PricingCatalogActiveSummary = active is null
                ? "No active pricing catalog · activation is never automatic"
                : $"Active · {active.TrustState} · {active.Source.Label} · {active.Sha256[..12]}…";
            OnPropertyChanged(nameof(PricingCatalogActiveSummary));

            RoutePageViewModel page = _pages[AppRoute.Pricing];
            page.Detail = snapshots.Count == 0
                ? "No admitted catalog snapshots · exact schema 1.1.5/seed bytes still required"
                : $"{snapshots.Count:N0} admitted historical snapshot{(snapshots.Count == 1 ? string.Empty : "s")} · {PricingCatalogActiveSummary}";
        }
        catch (Exception exception) when (!IsFatalUiException(exception))
        {
            PricingCatalogHistoryItems.Clear();
            PricingCatalogHistoryItems.Add("Catalog history unavailable · no activation was attempted");
            PricingCatalogActiveSummary = "Catalog history could not be read";
            OnPropertyChanged(nameof(PricingCatalogActiveSummary));
        }
    }
}
