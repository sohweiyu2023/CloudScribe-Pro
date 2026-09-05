using CloudScribe.Application.Pricing;
using CloudScribe.Application.Security;
using CloudScribe.Domain.Pricing;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.App.Composition;

/// <summary>
/// Resolves one claimed request intent from one atomic request-bound authorization snapshot while
/// re-reading persisted Google account/capability evidence. The resolver never grants missing
/// authorization by default and never assembles authorization from unrelated UI "latest" state.
/// </summary>
internal sealed class GoogleGenerationProductionIntentEvidenceResolver
    : IGoogleGenerationProductionIntentEvidenceResolver
{
    private readonly GoogleGenerationProductionAuthorizationSnapshotStateOwner _authorizationOwner;
    private readonly GoogleGenerationProductionEvidenceResolver _productionEvidenceResolver;
    private readonly GoogleGenerationProductionAccountFactory _accountFactory;
    private readonly ICredentialVault _credentialVault;
    private readonly IPricingCatalogHistoryStore _pricingCatalogHistoryStore;
    private readonly TimeProvider _timeProvider;

    public GoogleGenerationProductionIntentEvidenceResolver(
        GoogleGenerationProductionAuthorizationSnapshotStateOwner authorizationOwner,
        GoogleGenerationProductionEvidenceResolver productionEvidenceResolver,
        GoogleGenerationProductionAccountFactory accountFactory,
        ICredentialVault credentialVault,
        IPricingCatalogHistoryStore pricingCatalogHistoryStore,
        TimeProvider timeProvider)
    {
        _authorizationOwner = authorizationOwner
            ?? throw new ArgumentNullException(nameof(authorizationOwner));
        _productionEvidenceResolver = productionEvidenceResolver
            ?? throw new ArgumentNullException(nameof(productionEvidenceResolver));
        _accountFactory = accountFactory ?? throw new ArgumentNullException(nameof(accountFactory));
        _credentialVault = credentialVault ?? throw new ArgumentNullException(nameof(credentialVault));
        _pricingCatalogHistoryStore = pricingCatalogHistoryStore
            ?? throw new ArgumentNullException(nameof(pricingCatalogHistoryStore));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<GoogleGenerationProductionCompileEvidence> ResolveAsync(
        GoogleGenerationProductionRequestIntent intent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        intent.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        GoogleGenerationProductionAuthorizationSnapshotStateOwner.CurrentSnapshot claimed =
            _authorizationOwner.ClaimCurrent();

        try
        {
            return await ResolveClaimedAsync(intent, claimed.Snapshot, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            _authorizationOwner.RestoreIfUnchanged(claimed);
            throw;
        }
    }

    private async Task<GoogleGenerationProductionCompileEvidence> ResolveClaimedAsync(
        GoogleGenerationProductionRequestIntent intent,
        GoogleGenerationProductionAuthorizationSnapshotStateOwner.AuthorizationSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ValidateIntentBinding(intent, snapshot);
        GoogleGenerationProductionEvidence persisted = await _productionEvidenceResolver
            .ResolveAsync(intent.AccountId, cancellationToken)
            .ConfigureAwait(false);
        DateTimeOffset nowUtc = _timeProvider.GetUtcNow();
        persisted.Validate(nowUtc);
        ValidatePersistedBinding(snapshot, persisted, nowUtc);

        bool accountCredentialAvailable = await ValidateCredentialAvailableAsync(
                snapshot.Account,
                cancellationToken)
            .ConfigureAwait(false);
        PricingCatalogSnapshot activePricing = await ValidatePricingCurrentAsync(
                snapshot.PricingProvenanceId,
                cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        return BuildCompileEvidence(intent, snapshot, persisted, activePricing, accountCredentialAvailable, nowUtc);
    }

    private static GoogleGenerationProductionCompileEvidence BuildCompileEvidence(
        GoogleGenerationProductionRequestIntent intent,
        GoogleGenerationProductionAuthorizationSnapshotStateOwner.AuthorizationSnapshot snapshot,
        GoogleGenerationProductionEvidence persisted,
        PricingCatalogSnapshot activePricing,
        bool accountCredentialAvailable,
        DateTimeOffset nowUtc) =>
        new()
        {
            Plan = intent.Plan,
            CompilationOptions = intent.CompilationOptions,
            Account = snapshot.Account,
            Capabilities = snapshot.Capabilities,
            PricingProvenanceId = snapshot.PricingProvenanceId,
            RequestRevision = intent.RequestRevision,
            ProjectId = intent.ProjectId,
            ModelId = intent.ModelId,
            IdempotencyKey = intent.IdempotencyKey,
            AdmittedTrust = snapshot.AdmittedTrust,
            PreviousState = snapshot.PreviousState,
            CurrentState = snapshot.CurrentState,
            ResolutionEvidence = snapshot.ResolutionEvidence,
            AccountAuthorized = persisted.Account.IsEnabled,
            ProjectAuthorized = snapshot.ProjectAuthorized,
            CapabilityCurrent = !persisted.Capability.IsStale(nowUtc),
            PricingCurrent = string.Equals(
                activePricing.Sha256,
                snapshot.PricingProvenanceId,
                StringComparison.Ordinal),
            AdmissionCurrent = snapshot.AdmissionCurrent,
            AccountCredentialAvailable = accountCredentialAvailable,
            PricingApproved = snapshot.PricingApproved,
            PostCompileLimitsSatisfied = snapshot.PostCompileLimitsSatisfied,
            Currency = snapshot.Currency,
            Scale = snapshot.Scale,
            CurrentEstimateMinorUnits = snapshot.CurrentEstimateMinorUnits,
            NowUtc = nowUtc,
        };

    private static void ValidateIntentBinding(
        GoogleGenerationProductionRequestIntent intent,
        GoogleGenerationProductionAuthorizationSnapshotStateOwner.AuthorizationSnapshot snapshot)
    {
        if (!string.Equals(intent.AccountId, snapshot.AccountId, StringComparison.Ordinal)
            || !string.Equals(intent.ProjectId, snapshot.ProjectId, StringComparison.Ordinal)
            || !string.Equals(intent.ModelId, snapshot.ModelId, StringComparison.Ordinal)
            || !string.Equals(intent.IdempotencyKey, snapshot.IdempotencyKey, StringComparison.Ordinal)
            || intent.RequestRevision != snapshot.RequestRevision)
        {
            throw new InvalidOperationException(
                "Request-bound Google production authorization snapshot does not match the claimed request intent.");
        }

        if (snapshot.CapturedAtUtc < intent.CapturedAtUtc)
        {
            throw new InvalidOperationException(
                "Request-bound Google production authorization snapshot predates the claimed request intent.");
        }
    }

    private void ValidatePersistedBinding(
        GoogleGenerationProductionAuthorizationSnapshotStateOwner.AuthorizationSnapshot snapshot,
        GoogleGenerationProductionEvidence persisted,
        DateTimeOffset nowUtc)
    {
        GoogleGenerationAccount currentAccount = _accountFactory.Create(persisted);
        if (!Equals(currentAccount, snapshot.Account))
        {
            throw new InvalidOperationException(
                "Persisted Google account evidence changed after the request-bound authorization snapshot was captured.");
        }

        snapshot.Capabilities.Validate(nowUtc);
        if (!string.Equals(
                persisted.Capability.Snapshot.Account.AccountId,
                snapshot.Capabilities.AccountId,
                StringComparison.Ordinal)
            || !string.Equals(
                persisted.Capability.Snapshot.ProvenanceId,
                snapshot.Capabilities.ProvenanceId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Persisted Google capability evidence changed after the request-bound authorization snapshot was captured.");
        }
    }

    private async ValueTask<bool> ValidateCredentialAvailableAsync(
        GoogleGenerationAccount account,
        CancellationToken cancellationToken)
    {
        CredentialReference reference = new(account.CredentialReferenceId);
        CredentialSecret credential = await _credentialVault
            .ReadAsync(reference, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "Current Google provider account credential is unavailable at request authorization time.");
        using (credential)
        {
            return credential is not null;
        }
    }

    private async ValueTask<PricingCatalogSnapshot> ValidatePricingCurrentAsync(
        string pricingProvenanceId,
        CancellationToken cancellationToken)
    {
        PricingCatalogSnapshot activePricing = await _pricingCatalogHistoryStore
            .GetActiveSnapshotAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "No active persisted pricing catalog is available at request authorization time.");

        if (activePricing.TrustState is not (
                PricingCatalogTrustState.ValidUnsigned or PricingCatalogTrustState.SignatureVerified))
        {
            throw new InvalidOperationException(
                "The active persisted pricing catalog is not admitted production pricing evidence.");
        }

        if (!string.Equals(activePricing.Sha256, pricingProvenanceId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Active persisted pricing provenance changed after the request-bound authorization snapshot was captured.");
        }

        return activePricing;
    }
}
