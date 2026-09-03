using CloudScribe.App.ViewModels;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.App.Composition;

/// <summary>
/// Owns the final Stage6 handoff from the real compile/approval workflow into production runtime composition.
/// Every invocation resolves the current durable spend authorization, the exact current UI/provider snapshot
/// (including compiled provider payload bytes), and the current estimate independently and immediately before
/// execution. No runtime evidence is reconstructed or cached by this source.
/// </summary>
public sealed class GoogleGenerationProductionRuntimeRequestSource
{
    private readonly Func<CancellationToken, Task<GoogleGenerationSpendAuthorization?>> _resolveCurrentAuthorization;
    private readonly Func<CancellationToken, Task<GoogleGenerationUiExecutionSnapshot?>> _resolveCurrentSnapshot;
    private readonly Func<CancellationToken, Task<long?>> _resolveCurrentEstimateMinorUnits;

    public GoogleGenerationProductionRuntimeRequestSource(
        Func<CancellationToken, Task<GoogleGenerationSpendAuthorization?>> resolveCurrentAuthorization,
        Func<CancellationToken, Task<GoogleGenerationUiExecutionSnapshot?>> resolveCurrentSnapshot,
        Func<CancellationToken, Task<long?>> resolveCurrentEstimateMinorUnits)
    {
        _resolveCurrentAuthorization = resolveCurrentAuthorization
            ?? throw new ArgumentNullException(nameof(resolveCurrentAuthorization));
        _resolveCurrentSnapshot = resolveCurrentSnapshot
            ?? throw new ArgumentNullException(nameof(resolveCurrentSnapshot));
        _resolveCurrentEstimateMinorUnits = resolveCurrentEstimateMinorUnits
            ?? throw new ArgumentNullException(nameof(resolveCurrentEstimateMinorUnits));
    }

    public async Task<GoogleGenerationProductionRuntimeRequest> ResolveAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        GoogleGenerationSpendAuthorization authorization =
            await _resolveCurrentAuthorization(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "Stage6 production generation requires a current durable spend authorization.");

        cancellationToken.ThrowIfCancellationRequested();

        GoogleGenerationUiExecutionSnapshot snapshot =
            await _resolveCurrentSnapshot(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "Stage6 production generation requires the exact current compiled UI execution snapshot.");

        cancellationToken.ThrowIfCancellationRequested();

        long? currentEstimateMinorUnits =
            await _resolveCurrentEstimateMinorUnits(cancellationToken).ConfigureAwait(false);
        if (currentEstimateMinorUnits is null)
        {
            throw new InvalidOperationException(
                "Stage6 production generation requires a current provider-billed estimate.");
        }

        if (currentEstimateMinorUnits.Value < 0)
        {
            throw new InvalidOperationException(
                "Stage6 production generation current estimate cannot be negative.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        return GoogleGenerationProductionRuntimeRequestFactory.Create(
            authorization,
            snapshot,
            currentEstimateMinorUnits.Value).Validate();
    }
}
