using CloudScribe.App.ViewModels;
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
    private readonly GoogleGenerationProductionTransportFactory _transportFactory;
    private readonly TimeProvider _timeProvider;

    public GoogleGenerationProductionRuntimeEvidenceResolver(
        GoogleGenerationProductionEvidenceResolver productionEvidenceResolver,
        GoogleGenerationProductionAccountFactory accountFactory,
        GoogleGenerationCurrentSpendAuthorizationResolver spendAuthorizationResolver,
        GoogleGenerationProductionTransportFactory transportFactory,
        TimeProvider timeProvider)
    {
        _productionEvidenceResolver = productionEvidenceResolver ?? throw new ArgumentNullException(nameof(productionEvidenceResolver));
        _accountFactory = accountFactory ?? throw new ArgumentNullException(nameof(accountFactory));
        _spendAuthorizationResolver = spendAuthorizationResolver ?? throw new ArgumentNullException(nameof(spendAuthorizationResolver));
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
}

public sealed record GoogleGenerationProductionRuntimeRequest(
    string AccountId,
    GoogleGenerationSubmissionEnvelope SubmissionEnvelope,
    string PricingProvenanceId,
    int RequestRevision,
    string Currency,
    int Scale,
    long CurrentEstimateMinorUnits,
    GoogleGenerationUiExecutionSnapshot Snapshot)
{
    public GoogleGenerationProductionRuntimeRequest Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(AccountId);
        ArgumentNullException.ThrowIfNull(SubmissionEnvelope);
        ArgumentException.ThrowIfNullOrWhiteSpace(PricingProvenanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(Currency);
        ArgumentNullException.ThrowIfNull(Snapshot);
        if (RequestRevision < 0)
            throw new InvalidOperationException("Google generation request revision cannot be negative.");
        if (Scale is < 0 or > 9)
            throw new InvalidOperationException("Google generation currency scale must be between zero and nine.");
        if (CurrentEstimateMinorUnits < 0)
            throw new InvalidOperationException("Google generation current estimate cannot be negative.");
        if (!string.Equals(AccountId, SubmissionEnvelope.AccountId, StringComparison.Ordinal))
            throw new InvalidOperationException("Google runtime request account differs from the durable submission envelope.");
        return this;
    }
}
