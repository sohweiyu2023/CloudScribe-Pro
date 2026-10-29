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
    private readonly IGoogleGenerationProjectAuthorizationStore? _projectAuthorizations;

    public GoogleGenerationProductionRuntimeEvidenceResolver(
        GoogleGenerationProductionEvidenceResolver productionEvidenceResolver,
        GoogleGenerationProductionAccountFactory accountFactory,
        GoogleGenerationCurrentSpendAuthorizationResolver spendAuthorizationResolver,
        IPricingCatalogHistoryStore pricingCatalogHistoryStore,
        GoogleGenerationProductionTransportFactory transportFactory,
        TimeProvider timeProvider, IGoogleGenerationProjectAuthorizationStore? projectAuthorizations = null)
    {
        _productionEvidenceResolver = productionEvidenceResolver ?? throw new ArgumentNullException(nameof(productionEvidenceResolver));
        _accountFactory = accountFactory ?? throw new ArgumentNullException(nameof(accountFactory));
        _spendAuthorizationResolver = spendAuthorizationResolver ?? throw new ArgumentNullException(nameof(spendAuthorizationResolver));
        _pricingCatalogHistoryStore = pricingCatalogHistoryStore ?? throw new ArgumentNullException(nameof(pricingCatalogHistoryStore));
        _transportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _projectAuthorizations = projectAuthorizations;
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
        await ValidateCurrentRuntimeAuthoritiesAsync(request, nowUtc, cancellationToken).ConfigureAwait(false);

        GoogleGenerationAccount account = await _accountFactory
            .CreateSubmissionAccountAsync(current, cancellationToken)
            .ConfigureAwait(false);
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

        GoogleGenerationProductionTransport productionTransport = _transportFactory.Create(current, account);
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

    private async Task ValidateCurrentRuntimeAuthoritiesAsync(GoogleGenerationProductionRuntimeRequest request,
        DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        await ValidatePricingCurrentAsync(request.PricingProvenanceId, cancellationToken).ConfigureAwait(false);
        if (request.SubmissionEnvelope.RecoveryAuthorizationId is not null || request.SubmissionEnvelope.ChirpAuthorizationId is not null)
            await ValidateRecoveryProjectCurrentAsync(request, nowUtc, cancellationToken).ConfigureAwait(false);
    }

    private async Task ValidateRecoveryProjectCurrentAsync(GoogleGenerationProductionRuntimeRequest request,
        DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var store = _projectAuthorizations ?? throw new InvalidOperationException("Current recovery project authority is unavailable.");
        var authorization = await store.LoadCurrentAsync(request.AccountId, request.Snapshot.UiSelection.ProjectId,
            request.SubmissionEnvelope.VoiceName, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Current recovery project authority is missing.");
        authorization.Validate(nowUtc);
        if (!authorization.IsCurrent(nowUtc) || !string.Equals(authorization.AccountId, request.AccountId, StringComparison.Ordinal)
            || !string.Equals(authorization.ProjectId, request.Snapshot.UiSelection.ProjectId, StringComparison.Ordinal)
            || !string.Equals(authorization.ModelId, request.Snapshot.UiSelection.ModelId, StringComparison.Ordinal)
            || !string.Equals(authorization.CapabilityProvenanceId, request.SubmissionEnvelope.CapabilityProvenanceId, StringComparison.Ordinal))
            throw new InvalidOperationException("Recovery project/model/capability authorization changed before execution.");
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
