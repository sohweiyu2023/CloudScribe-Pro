using CloudScribe.App.ViewModels;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.App.Composition;

/// <summary>
/// Owns the single current Google generation submission that has passed the complete production
/// compile/approval boundary. A state becomes executable only after its exact durable spend
/// authorization has been persisted successfully.
/// </summary>
public sealed class GoogleGenerationProductionSubmissionStateOwner
{
    private readonly IGoogleGenerationSpendAuthorizationStore _authorizationStore;
    private GoogleGenerationProductionSubmissionState? _current;

    public GoogleGenerationProductionSubmissionStateOwner(
        IGoogleGenerationSpendAuthorizationStore authorizationStore)
    {
        _authorizationStore = authorizationStore
            ?? throw new ArgumentNullException(nameof(authorizationStore));
    }

    public async Task ApproveAsync(
        GoogleGenerationSpendAuthorization authorization,
        GoogleGenerationUiExecutionSnapshot snapshot,
        long currentEstimateMinorUnits,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (authorization.Envelope.RecoveryAuthorizationId is not null || authorization.Envelope.ChirpAuthorizationId is not null)
            throw new InvalidOperationException("Recovery requires activation of its exact dedicated durable approval, not ordinary spend approval.");
        cancellationToken.ThrowIfCancellationRequested();

        _ = GoogleGenerationProductionRuntimeRequestFactory.Create(
            authorization,
            snapshot,
            currentEstimateMinorUnits).Validate();

        var next = new GoogleGenerationProductionSubmissionState(
            authorization.Envelope,
            snapshot,
            currentEstimateMinorUnits);

        await _authorizationStore
            .SaveApprovedAsync(authorization, cancellationToken)
            .ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        Volatile.Write(ref _current, next);
    }

    public Task<GoogleGenerationProductionSubmissionState?> ResolveCurrentAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Volatile.Read(ref _current));
    }

    public async Task ActivateRecoveryAsync(GoogleGenerationSubmissionEnvelope envelope,
        GoogleGenerationUiExecutionSnapshot snapshot, long estimate, CancellationToken cancellationToken = default)
    {
        if (envelope.RecoveryAuthorizationId is not { } id
            || !string.Equals(snapshot.ProviderRequest.IdempotencyKey, GoogleExplicitRecoveryService.KeyPrefix + id.ToString("N"), StringComparison.Ordinal))
            throw new InvalidOperationException("Recovery activation requires the exact durable child identity.");
        var authorization = await _authorizationStore.LoadApprovedAsync(envelope, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The exact durable recovery authorization is unavailable.");
        if (authorization.Envelope != envelope) throw new InvalidOperationException("Durable recovery authority differs from the exact requested envelope.");
        GoogleGenerationProductionUiSnapshotValidator.Validate(snapshot);
        _ = GoogleGenerationProductionRuntimeRequestFactory.Create(authorization, snapshot, estimate).Validate();
        cancellationToken.ThrowIfCancellationRequested();
        Volatile.Write(ref _current, new(envelope, snapshot, estimate));
    }

    public void Invalidate()
    {
        Volatile.Write(ref _current, null);
    }

    public async Task ActivateArticleAsync(GoogleGenerationSubmissionEnvelope envelope,
        GoogleGenerationUiExecutionSnapshot snapshot, long estimate, CancellationToken cancellationToken = default)
    {
        if (envelope.ChirpAuthorizationId is null || envelope.RecoveryAuthorizationId is not null
            || !snapshot.ProviderRequest.IdempotencyKey.StartsWith(ChirpArticleService.KeyPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException("Internal article activation requires its exact dedicated durable approval.");
        var authorization = await _authorizationStore.LoadApprovedAsync(envelope, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The exact durable article authorization is unavailable.");
        if (authorization.Envelope != envelope) throw new InvalidOperationException("Durable article approval differs from the current envelope.");
        GoogleGenerationProductionUiSnapshotValidator.Validate(snapshot);
        _ = GoogleGenerationProductionRuntimeRequestFactory.Create(authorization, snapshot, estimate).Validate();
        cancellationToken.ThrowIfCancellationRequested();
        Volatile.Write(ref _current, new(envelope, snapshot, estimate));
    }
}
