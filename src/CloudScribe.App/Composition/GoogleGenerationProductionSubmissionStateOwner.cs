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

    public void Invalidate()
    {
        Volatile.Write(ref _current, null);
    }
}
