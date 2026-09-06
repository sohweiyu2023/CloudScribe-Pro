using System.Collections.ObjectModel;
using Avalonia.Threading;
using CloudScribe.App.Navigation;
using CloudScribe.Application.Pricing;
using CloudScribe.Application.Providers;

namespace CloudScribe.App.ViewModels;

public sealed partial class ShellViewModel
{
    private IPricingCatalogHistoryStore? _pricingCatalogHistoryStore;
    private IPricingContractOverrideStore? _pricingContractOverrideStore;
    private IProviderAccountStore? _providerAccountStore;
    private IProviderCapabilitySnapshotStore? _providerCapabilitySnapshotStore;
    private int _pricingHistoryStarted;

    public ObservableCollection<string> PricingCatalogHistoryItems { get; } = [];
    public ObservableCollection<string> PricingContractOverrideItems { get; } = [];
    public ObservableCollection<string> ProviderAccountItems { get; } = [];
    public ObservableCollection<string> ProviderCapabilityItems { get; } = [];

    public string PricingCatalogActiveSummary { get; private set; } = "No active pricing catalog";
    public string PricingContractOverrideSummary { get; private set; } = "No stored pricing-contract overrides";
    public string ProviderAccountSummary { get; private set; } = "No registered provider accounts";
    public string ProviderCapabilitySummary { get; private set; } = "No persisted capability evidence";
    public string ProviderQuotaStatusSummary { get; } = "Account quota unknown · no live account observation";

    public bool IsPricingSelected => SelectedNavigationItem?.Route == AppRoute.Pricing;

    public void ConfigureStage4PricingHistory(
        IPricingCatalogHistoryStore historyStore,
        IPricingContractOverrideStore overrideStore,
        IProviderAccountStore providerAccountStore,
        IProviderCapabilitySnapshotStore providerCapabilitySnapshotStore)
    {
        ArgumentNullException.ThrowIfNull(historyStore);
        ArgumentNullException.ThrowIfNull(overrideStore);
        ArgumentNullException.ThrowIfNull(providerAccountStore);
        ArgumentNullException.ThrowIfNull(providerCapabilitySnapshotStore);
        if (Interlocked.CompareExchange(ref _pricingCatalogHistoryStore, historyStore, null) is not null
            || Interlocked.CompareExchange(ref _pricingContractOverrideStore, overrideStore, null) is not null
            || Interlocked.CompareExchange(ref _providerAccountStore, providerAccountStore, null) is not null
            || Interlocked.CompareExchange(ref _providerCapabilitySnapshotStore, providerCapabilitySnapshotStore, null) is not null)
        {
            throw new InvalidOperationException("Stage 4 pricing/provider history is already configured.");
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
            if (_pricingCatalogHistoryStore is null
                || _pricingContractOverrideStore is null
                || _providerAccountStore is null
                || _providerCapabilitySnapshotStore is null)
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
            IReadOnlyList<ProviderAccountSnapshot> accounts = await _providerAccountStore
                .ListAsync()
                .ConfigureAwait(true);

            UpdateCatalogHistory(snapshots, active);
            UpdateContractOverrides(overrides);
            await UpdateProviderEvidenceAsync(accounts).ConfigureAwait(true);

            RoutePageViewModel page = _pages[AppRoute.Pricing];
            page.Detail = snapshots.Count == 0
                ? $"No admitted catalog snapshots · no active trusted catalog · {PricingContractOverrideSummary} · {ProviderAccountSummary} · {ProviderCapabilitySummary} · {ProviderQuotaStatusSummary} · billable approval remains blocked"
                : $"{snapshots.Count:N0} admitted historical snapshot{(snapshots.Count == 1 ? string.Empty : "s")} · {PricingCatalogActiveSummary} · {PricingContractOverrideSummary} · {ProviderAccountSummary} · {ProviderCapabilitySummary} · {ProviderQuotaStatusSummary}";
        }
        catch (Exception exception) when (!IsFatalUiException(exception))
        {
            PricingCatalogHistoryItems.Clear();
            PricingCatalogHistoryItems.Add("Catalog history unavailable · no activation was attempted");
            PricingContractOverrideItems.Clear();
            PricingContractOverrideItems.Add("Override history unavailable · no pricing assumption was changed");
            ProviderAccountItems.Clear();
            ProviderAccountItems.Add("Provider account registry unavailable · no account was selected");
            ProviderCapabilityItems.Clear();
            ProviderCapabilityItems.Add("Capability evidence unavailable · no provider call was attempted");
            PricingCatalogActiveSummary = "Catalog history could not be read";
            PricingContractOverrideSummary = "Override history could not be read · overrides remain inert";
            ProviderAccountSummary = "Provider account registry could not be read";
            ProviderCapabilitySummary = "Capability evidence could not be read";
            OnPropertyChanged(nameof(PricingCatalogActiveSummary));
            OnPropertyChanged(nameof(PricingContractOverrideSummary));
            OnPropertyChanged(nameof(ProviderAccountSummary));
            OnPropertyChanged(nameof(ProviderCapabilitySummary));
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


    private async Task UpdateProviderEvidenceAsync(IReadOnlyList<ProviderAccountSnapshot> accounts)
    {
        ProviderAccountItems.Clear();
        ProviderCapabilityItems.Clear();
        int freshCapabilitySnapshots = 0;
        int staleCapabilitySnapshots = 0;
        DateTimeOffset nowUtc = _timeProvider.GetUtcNow();

        foreach (ProviderAccountSnapshot account in accounts.Take(8))
        {
            string credentialState = account.Reference.CredentialReference is null ? "no credential reference" : "credential reference stored in OS-vault boundary";
            ProviderAccountItems.Add(
                $"{(account.IsEnabled ? "ENABLED" : "DISABLED")} · {account.Reference.ProviderStableId}/{account.Reference.AccountId} · r{account.Revision} · {credentialState}");

            StoredProviderCapabilitySnapshot? latest = await _providerCapabilitySnapshotStore!
                .GetLatestAsync(account.Reference.ProviderStableId, account.Reference.AccountId)
                .ConfigureAwait(true);
            if (latest is null)
            {
                ProviderCapabilityItems.Add($"NO EVIDENCE · {account.Reference.ProviderStableId}/{account.Reference.AccountId} · no provider call was attempted");
                continue;
            }

            bool stale = latest.IsStale(nowUtc);
            if (stale)
            {
                staleCapabilitySnapshots++;
            }
            else
            {
                freshCapabilitySnapshots++;
            }
            ProviderCapabilityItems.Add(
                $"{(stale ? "STALE" : "FRESH")} · {latest.Snapshot.Account.ProviderStableId}/{latest.Snapshot.Account.AccountId} · {latest.Snapshot.Capabilities.Count:N0} capabilities · {latest.Snapshot.ProvenanceId}");
        }

        ProviderAccountSummary = accounts.Count == 0
            ? "No registered provider accounts · no default account exists"
            : $"{accounts.Count:N0} registered account{(accounts.Count == 1 ? string.Empty : "s")} · metadata only · no default selection";
        ProviderCapabilitySummary = freshCapabilitySnapshots == 0 && staleCapabilitySnapshots == 0
            ? "No persisted capability evidence · discovery is never implicit"
            : $"Capability evidence · {freshCapabilitySnapshots:N0} fresh · {staleCapabilitySnapshots:N0} stale · inspection never refreshes providers";
        OnPropertyChanged(nameof(ProviderAccountSummary));
        OnPropertyChanged(nameof(ProviderCapabilitySummary));
    }
}
