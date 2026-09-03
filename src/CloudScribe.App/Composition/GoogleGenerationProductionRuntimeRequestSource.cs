using CloudScribe.App.ViewModels;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.App.Composition;

/// <summary>
/// The exact current compile/approval handoff required by production Stage6 generation.
/// The submission envelope and execution snapshot must describe the same compiled provider payload;
/// the runtime request factory enforces that binding before any provider submission can occur.
/// </summary>
public sealed record GoogleGenerationProductionSubmissionState(
    GoogleGenerationSubmissionEnvelope SubmissionEnvelope,
    GoogleGenerationUiExecutionSnapshot Snapshot,
    long CurrentEstimateMinorUnits);

/// <summary>
/// Owns the final Stage6 handoff from the real compile/approval workflow into production runtime composition.
/// Every invocation resolves one coherent current compiled submission state, then loads the durable spend
/// authorization by that exact submission envelope immediately before execution. Authorization is never
/// supplied by an ambient delegate, reconstructed, or cached by this source.
/// </summary>
public sealed class GoogleGenerationProductionRuntimeRequestSource
{
    private readonly IGoogleGenerationSpendAuthorizationStore _authorizationStore;
    private readonly Func<CancellationToken, Task<GoogleGenerationProductionSubmissionState?>> _resolveCurrentSubmission;

    public GoogleGenerationProductionRuntimeRequestSource(
        IGoogleGenerationSpendAuthorizationStore authorizationStore,
        Func<CancellationToken, Task<GoogleGenerationProductionSubmissionState?>> resolveCurrentSubmission)
    {
        _authorizationStore = authorizationStore
            ?? throw new ArgumentNullException(nameof(authorizationStore));
        _resolveCurrentSubmission = resolveCurrentSubmission
            ?? throw new ArgumentNullException(nameof(resolveCurrentSubmission));
    }

    public async Task<GoogleGenerationProductionRuntimeRequest> ResolveAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        GoogleGenerationProductionSubmissionState submission =
            await _resolveCurrentSubmission(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "Stage6 production generation requires one coherent current compiled submission state.");

        if (submission.SubmissionEnvelope is null)
        {
            throw new InvalidOperationException(
                "Stage6 production generation requires the exact current durable submission envelope.");
        }

        if (submission.Snapshot is null)
        {
            throw new InvalidOperationException(
                "Stage6 production generation requires the exact current compiled UI execution snapshot.");
        }

        if (submission.CurrentEstimateMinorUnits < 0)
        {
            throw new InvalidOperationException(
                "Stage6 production generation current estimate cannot be negative.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        GoogleGenerationSpendAuthorization authorization = await _authorizationStore
            .LoadApprovedAsync(submission.SubmissionEnvelope, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "Stage6 production generation requires a durable spend authorization for the exact current submission envelope.");

        cancellationToken.ThrowIfCancellationRequested();

        return GoogleGenerationProductionRuntimeRequestFactory.Create(
            authorization,
            submission.Snapshot,
            submission.CurrentEstimateMinorUnits).Validate();
    }
}
