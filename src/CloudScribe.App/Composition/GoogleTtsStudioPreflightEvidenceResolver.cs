using System.Security.Cryptography;
using CloudScribe.App.ViewModels;
using CloudScribe.Application.Pricing;
using CloudScribe.Application.Security;
using CloudScribe.Domain.Pricing;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Infrastructure.Pricing;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.App.Composition;

/// <summary>
/// Re-resolves the exact persisted production evidence required by a Studio capture before any
/// Stage6 intent is published. This class deliberately does not infer a Google billing family,
/// price, provider hard limit, reconciliation result, or submission endpoint from UI state.
/// </summary>
internal sealed class GoogleTtsStudioPreflightEvidenceResolver(
    GoogleGenerationProductionEvidenceResolver productionEvidenceResolver,
    GoogleGenerationProductionAccountFactory accountFactory,
    IPricingCatalogHistoryStore pricingCatalogHistoryStore,
    ICredentialVault credentialVault,
    V222ControlSet controls,
    TimeProvider timeProvider)
{
    private readonly GoogleGenerationProductionEvidenceResolver _productionEvidenceResolver =
        productionEvidenceResolver ?? throw new ArgumentNullException(nameof(productionEvidenceResolver));
    private readonly GoogleGenerationProductionAccountFactory _accountFactory =
        accountFactory ?? throw new ArgumentNullException(nameof(accountFactory));
    private readonly IPricingCatalogHistoryStore _pricingCatalogHistoryStore =
        pricingCatalogHistoryStore ?? throw new ArgumentNullException(nameof(pricingCatalogHistoryStore));
    private readonly ICredentialVault _credentialVault =
        credentialVault ?? throw new ArgumentNullException(nameof(credentialVault));
    private readonly V222ControlSet _controls = controls ?? throw new ArgumentNullException(nameof(controls));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<PreflightEvidence> ResolveAsync(
        ShellViewModel.GoogleTtsStudioRequestSelection selection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        cancellationToken.ThrowIfCancellationRequested();

        DateTimeOffset nowUtc = _timeProvider.GetUtcNow();
        GoogleGenerationProductionEvidence production = await _productionEvidenceResolver
            .ResolveAsync(selection.AccountStableId, cancellationToken)
            .ConfigureAwait(false);
        production.Validate(nowUtc);

        if (!Guid.TryParse(selection.CapabilityEvidenceId, out Guid selectedCapabilityId)
            || selectedCapabilityId == Guid.Empty
            || production.Capability.Id != selectedCapabilityId)
        {
            throw new InvalidOperationException(
                "The selected Google voice is not bound to the current persisted capability snapshot. Refresh the authenticated voice catalog before preparing generation.");
        }

        if (!string.Equals(
                production.Capability.Snapshot.Account.AccountId,
                selection.AccountStableId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The selected Google voice and current persisted capability evidence do not belong to the same account.");
        }

        GoogleGenerationAccount account = _accountFactory.Create(production);
        PricingCatalogSnapshot activePricing = await _pricingCatalogHistoryStore
            .GetActiveSnapshotAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "No active pricing catalog is available. Explicitly activate the authenticated built-in pricing catalog before preparing Google generation.");

        if (activePricing.TrustState is not (
                PricingCatalogTrustState.ValidUnsigned or PricingCatalogTrustState.SignatureVerified))
        {
            throw new InvalidOperationException(
                "The active pricing catalog is not admitted production pricing evidence.");
        }

        string authenticatedPricingSha = Convert.ToHexString(
                SHA256.HashData(_controls.PricingSeedUtf8.Span))
            .ToLowerInvariant();
        if (!string.Equals(activePricing.Sha256, authenticatedPricingSha, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The active pricing catalog is not the SHA-authenticated v2.22 Google pricing/control material. Activate the authenticated built-in pricing catalog before preparing generation.");
        }

        CredentialReference credentialReference = new(account.CredentialReferenceId);
        CredentialSecret credential = await _credentialVault
            .ReadAsync(credentialReference, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "The Windows credential for the selected Google account is no longer available.");
        using (credential)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        return new PreflightEvidence(
            production,
            account,
            activePricing,
            authenticatedPricingSha,
            nowUtc).Validate();
    }

    internal sealed record PreflightEvidence(
        GoogleGenerationProductionEvidence Production,
        GoogleGenerationAccount Account,
        PricingCatalogSnapshot ActivePricing,
        string PricingProvenanceId,
        DateTimeOffset CapturedAtUtc)
    {
        public PreflightEvidence Validate()
        {
            if (Production is null || Account is null || ActivePricing is null)
                throw new InvalidOperationException("Google Studio preflight evidence is incomplete.");
            if (string.IsNullOrWhiteSpace(PricingProvenanceId)
                || !string.Equals(ActivePricing.Sha256, PricingProvenanceId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Google Studio preflight pricing provenance is inconsistent.");
            }
            if (CapturedAtUtc == default)
                throw new InvalidOperationException("Google Studio preflight capture time is missing.");
            return this;
        }
    }
}
