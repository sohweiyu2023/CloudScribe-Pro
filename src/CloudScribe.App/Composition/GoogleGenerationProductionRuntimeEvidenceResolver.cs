using CloudScribe.App.ViewModels;
using CloudScribe.Application.Pricing;
using CloudScribe.Domain.Pricing;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.App.Composition;

/// <summary>
/// Builds the Stage6 runtime evidence only from a current persisted Google account/capability pair,
/// an exact durable spend authorization, the explicitly admitted pricing/request revision, and the
/// already-bound UI execution snapshot. Nothing in this resolver grants authorization by default.
/// </summary>
public sealed class GoogleGenerationProductionRuntimeEvidenceResolver
{
    private readonly GoogleGenerationProductionEvidenceResolver _productionEvidenceResolver;
    private readonly GoogleGenerationProductionAccountFactory _accountFactory;
    private readonly GoogleGenerationCurrentSpendAuthorizationResolver _spendAuthorizationResolver;
    private readonly IPricingCatalogHistoryStore _pricingCatalogHistoryStore;
    private readonly GoogleGenerationProductionTransportFactory _transportFactory;
    private readonly TimeProvider _timeProvider;

    public GoogleGenerationProductionRuntimeEvidenceResolver(
        GoogleGenerationProductionEvidenceResolver productionEvidenceResolver,
        GoogleGenerationProductionAccountFactory accountFactory,
        GoogleGenerationCurrentSpendAuthorizationResolver spendAuthorizationResolver,
        IPricingCatalogHistoryStore pricingCatalogHistoryStore,
        GoogleGenerationProductionTransportFactory transportFactory,
        TimeProvider timeProvider)
    {
        _productionEvidenceResolver = productionEvidenceResolver ?? throw new ArgumentNullException(nameof(productionEvidenceResolver));
        _accountFactory = accountFactory ?? throw new ArgumentNullException(nameof(accountFactory));
        _spendAuthorizationResolver = spendAuthorizationResolver ?? throw new ArgumentNullException(nameof(spendAuthorizationResolver));
        _pricingCatalogHistoryStore = pricingCatalogHistoryStore ?? throw new ArgumentNullException(nameof(pricingCatalogHistoryStore));
        _transportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<GoogleGenerationAuthorizedRuntimeEvidence> ResolveAsync(
        GoogleGenerationProductionRuntimeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        GoogleGenerationProductionEvidence current = await _productionEvidenceResolver
            .ResolveAsync(request.AccountId, cancellationToken)
            .ConfigureAwait(false);
        DateTimeOffset nowUtc = _timeProvider.GetUtcNow();
        current.Validate(nowUtc);
        await ValidatePricingCurrentAsync(request.PricingProvenanceId, cancellationToken).ConfigureAwait(false);

        GoogleGenerationAccount account = _accountFactory.Create(current);
        GoogleGenerationSpendAuthorization spendAuthorization = await _spendAuthorizationResolver
            .ResolveAsync(
                request.SubmissionEnvelope,
                request.Currency,
                request.Scale,
                request.CurrentEstimateMinorUnits,
                cancellationToken)
            .ConfigureAwait(false);
        GoogleCapabilitySnapshot capabilities = GoogleGenerationApprovedCapabilityProjection.Create(
            current,
            spendAuthorization,
            nowUtc);

        request.SubmissionEnvelope.EnsureStillAuthorized(
            account,
            capabilities,
            request.PricingProvenanceId,
            request.RequestRevision,
            request.Snapshot.ProviderRequest.CompiledPayload.Span,
            nowUtc);
        spendAuthorization.EnsureStillAuthorized(
            request.SubmissionEnvelope,
            request.Currency,
            request.Scale,
            request.CurrentEstimateMinorUnits);

        GoogleGenerationProductionTransport productionTransport = _transportFactory.Create(current);
        if (!Equals(productionTransport.Account, account))
        {
            throw new InvalidOperationException(
                "Google production transport account differs from the current authorized account evidence.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new GoogleGenerationAuthorizedRuntimeEvidence(
            productionTransport.Transport,
            account,
            capabilities,
            spendAuthorization,
            request.PricingProvenanceId,
            request.RequestRevision,
            request.Currency,
            request.Scale,
            request.CurrentEstimateMinorUnits,
            request.Snapshot);
    }

    private async Task ValidatePricingCurrentAsync(
        string pricingProvenanceId,
        CancellationToken cancellationToken)
    {
        PricingCatalogSnapshot activePricing = await _pricingCatalogHistoryStore
            .GetActiveSnapshotAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "No active persisted pricing catalog is available at Google generation runtime authorization time.");

        if (activePricing.TrustState is not (
                PricingCatalogTrustState.ValidUnsigned or PricingCatalogTrustState.SignatureVerified))
        {
            throw new InvalidOperationException(
                "The active persisted pricing catalog is not admitted Google generation runtime pricing evidence.");
        }

        if (!string.Equals(activePricing.Sha256, pricingProvenanceId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Active persisted pricing provenance changed before Google generation runtime submission.");
        }
    }
}
