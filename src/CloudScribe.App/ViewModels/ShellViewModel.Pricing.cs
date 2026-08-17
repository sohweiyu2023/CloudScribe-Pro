using System.Collections.ObjectModel;
using Avalonia.Threading;
using CloudScribe.App.Navigation;
using CloudScribe.Application.Pricing;

namespace CloudScribe.App.ViewModels;

public sealed partial class ShellViewModel
{
    private IPricingCatalogHistoryStore? _pricingCatalogHistoryStore;
    private IPricingContractOverrideStore? _pricingContractOverrideStore;
    private int _pricingHistoryStarted;

    public ObservableCollection<string> PricingCatalogHistoryItems { get; } = [];
    public ObservableCollection<string> PricingContractOverrideItems { get; } = [];

    public string PricingCatalogActiveSummary { get; private set; } = "No active pricing catalog";
    public string PricingContractOverrideSummary { get; private set; } = "No stored pricing-contract overrides";
    public string ProviderQuotaStatusSummary { get; } = "Account quota unknown · no live account observation";

    public bool IsPricingSelected => SelectedNavigationItem?.Route == AppRoute.Pricing;

    public void ConfigureStage4PricingHistory(
        IPricingCatalogHistoryStore historyStore,
        IPricingContractOverrideStore overrideStore)
    {
        ArgumentNullException.ThrowIfNull(historyStore);
        ArgumentNullException.ThrowIfNull(overrideStore);
        if (Interlocked.CompareExchange(ref _pricingCatalogHistoryStore, historyStore, null) is not null
            || Interlocked.CompareExchange(ref _pricingContractOverrideStore, overrideStore, null) is not null)
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
            if (_pricingCatalogHistoryStore is null || _pricingContractOverrideStore is null)
            {
                return;
            }

            IReadOnlyList<PricingCatalogSnapshot> snapshots = await _pricingCatalogHistoryStore
                .ListSnapshotsAsync()
                .ConfigureAwait(true);
            PricingCatalogSnapshot? active = await _pricingCatalogHistoryStore
                .GetActiveSnapshotAsync()
                .ConfigureAwait(true);
            IReadOnlyList<PricingContractOverrideSnapshot> overrides = await _pricingContractOverrideStore
                .ListInactiveAsync()
                .ConfigureAwait(true);

            UpdateCatalogHistory(snapshots, active);
            UpdateContractOverrides(overrides);

            RoutePageViewModel page = _pages[AppRoute.Pricing];
            page.Detail = snapshots.Count == 0
                ? $"No admitted catalog snapshots · exact schema 1.1.5/seed bytes still required · {PricingContractOverrideSummary} · {ProviderQuotaStatusSummary}"
                : $"{snapshots.Count:N0} admitted historical snapshot{(snapshots.Count == 1 ? string.Empty : "s")} · {PricingCatalogActiveSummary} · {PricingContractOverrideSummary} · {ProviderQuotaStatusSummary}";
        }
        catch (Exception exception) when (!IsFatalUiException(exception))
        {
            PricingCatalogHistoryItems.Clear();
            PricingCatalogHistoryItems.Add("Catalog history unavailable · no activation was attempted");
            PricingContractOverrideItems.Clear();
            PricingContractOverrideItems.Add("Override history unavailable · no pricing assumption was changed");
            PricingCatalogActiveSummary = "Catalog history could not be read";
            PricingContractOverrideSummary = "Override history could not be read · overrides remain inert";
            OnPropertyChanged(nameof(PricingCatalogActiveSummary));
            OnPropertyChanged(nameof(PricingContractOverrideSummary));
        }
    }

    private void UpdateCatalogHistory(
        IReadOnlyList<PricingCatalogSnapshot> snapshots,
        PricingCatalogSnapshot? active)
    {
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
    }

    private void UpdateContractOverrides(IReadOnlyList<PricingContractOverrideSnapshot> overrides)
    {
        PricingContractOverrideItems.Clear();
        foreach (PricingContractOverrideSnapshot item in overrides.Take(6))
        {
            PricingContractOverrideItems.Add($"INACTIVE · {item.Label} · {item.Sha256[..12]}…");
        }

        PricingContractOverrideSummary = overrides.Count == 0
            ? "No stored pricing-contract overrides"
            : $"{overrides.Count:N0} stored inactive override{(overrides.Count == 1 ? string.Empty : "s")} · never merged automatically";
        OnPropertyChanged(nameof(PricingContractOverrideSummary));
    }
}
