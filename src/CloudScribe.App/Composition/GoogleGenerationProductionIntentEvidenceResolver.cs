using CloudScribe.Infrastructure.Generation;

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
    private readonly TimeProvider _timeProvider;

    public GoogleGenerationProductionIntentEvidenceResolver(
        GoogleGenerationProductionAuthorizationSnapshotStateOwner authorizationOwner,
        GoogleGenerationProductionEvidenceResolver productionEvidenceResolver,
        GoogleGenerationProductionAccountFactory accountFactory,
        TimeProvider timeProvider)
    {
        _authorizationOwner = authorizationOwner
            ?? throw new ArgumentNullException(nameof(authorizationOwner));
        _productionEvidenceResolver = productionEvidenceResolver
            ?? throw new ArgumentNullException(nameof(productionEvidenceResolver));
        _accountFactory = accountFactory ?? throw new ArgumentNullException(nameof(accountFactory));
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
            GoogleGenerationProductionAuthorizationSnapshotStateOwner.AuthorizationSnapshot snapshot =
                claimed.Snapshot;
            ValidateIntentBinding(intent, snapshot);

            GoogleGenerationProductionEvidence persisted = await _productionEvidenceResolver
                .ResolveAsync(intent.AccountId, cancellationToken)
                .ConfigureAwait(false);
            DateTimeOffset nowUtc = _timeProvider.GetUtcNow();
            persisted.Validate(nowUtc);
            ValidatePersistedBinding(snapshot, persisted, nowUtc);
            cancellationToken.ThrowIfCancellationRequested();

            return new GoogleGenerationProductionCompileEvidence
            {
                Plan = intent.Plan,
                CompilationOptions = intent.CompilationOptions,
                Account = snapshot.Account,
                Capabilities = snapshot.Capabilities,
                PricingProvenanceId = snapshot.PricingProvenanceId,
                RequestRevision = snapshot.RequestRevision,
                ProjectId = intent.ProjectId,
                ModelId = intent.ModelId,
                IdempotencyKey = intent.IdempotencyKey,
                AdmittedTrust = snapshot.AdmittedTrust,
                PreviousState = snapshot.PreviousState,
                CurrentState = snapshot.CurrentState,
                ResolutionEvidence = snapshot.ResolutionEvidence,
                AccountAuthorized = snapshot.AccountAuthorized,
                ProjectAuthorized = snapshot.ProjectAuthorized,
                CapabilityCurrent = snapshot.CapabilityCurrent,
                PricingCurrent = snapshot.PricingCurrent,
                AdmissionCurrent = snapshot.AdmissionCurrent,
                AccountCredentialAvailable = snapshot.AccountCredentialAvailable,
                PricingApproved = snapshot.PricingApproved,
                PostCompileLimitsSatisfied = snapshot.PostCompileLimitsSatisfied,
                Currency = snapshot.Currency,
                Scale = snapshot.Scale,
                CurrentEstimateMinorUnits = snapshot.CurrentEstimateMinorUnits,
                NowUtc = nowUtc,
            };
        }
        catch
        {
            _authorizationOwner.RestoreIfUnchanged(claimed);
            throw;
        }
    }

    private static void ValidateIntentBinding(
        GoogleGenerationProductionRequestIntent intent,
        GoogleGenerationProductionAuthorizationSnapshotStateOwner.AuthorizationSnapshot snapshot)
    {
        if (!string.Equals(intent.AccountId, snapshot.AccountId, StringComparison.Ordinal)
            || !string.Equals(intent.ProjectId, snapshot.ProjectId, StringComparison.Ordinal)
            || !string.Equals(intent.ModelId, snapshot.ModelId, StringComparison.Ordinal)
            || !string.Equals(intent.IdempotencyKey, snapshot.IdempotencyKey, StringComparison.Ordinal))
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
}
