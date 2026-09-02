using CloudScribe.App.ViewModels;

namespace CloudScribe.App.Composition;

/// <summary>
/// Resolves the production Stage6 execution context only after the current UI snapshot and
/// runtime authorization evidence have both passed their fail-closed production boundaries.
/// </summary>
public sealed class GoogleGenerationProductionExecutionContextResolver
{
    private readonly GoogleGenerationProductionRuntimeEvidenceResolver _runtimeEvidenceResolver;
    private readonly TimeProvider _timeProvider;

    public GoogleGenerationProductionExecutionContextResolver(
        GoogleGenerationProductionRuntimeEvidenceResolver runtimeEvidenceResolver,
        TimeProvider timeProvider)
    {
        _runtimeEvidenceResolver = runtimeEvidenceResolver
            ?? throw new ArgumentNullException(nameof(runtimeEvidenceResolver));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<GoogleGenerationUiExecutionContext> ResolveAsync(
        GoogleGenerationProductionRuntimeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        GoogleGenerationUiExecutionSnapshot validatedSnapshot =
            GoogleGenerationProductionUiSnapshotValidator.Validate(request.Snapshot);
        if (!ReferenceEquals(validatedSnapshot, request.Snapshot))
        {
            throw new InvalidOperationException(
                "Stage6 production snapshot validation must preserve the exact current evidence instance.");
        }

        GoogleGenerationAuthorizedRuntimeEvidence evidence = await _runtimeEvidenceResolver
            .ResolveAsync(request, cancellationToken)
            .ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        GoogleGenerationUiExecutionSnapshot runtimeSnapshot =
            GoogleGenerationProductionUiSnapshotValidator.Validate(evidence.Snapshot);
        if (!ReferenceEquals(runtimeSnapshot, request.Snapshot))
        {
            throw new InvalidOperationException(
                "Stage6 runtime authorization evidence is not bound to the exact validated UI snapshot.");
        }

        return GoogleGenerationUiExecutionContextFactory.Create(evidence, _timeProvider);
    }
}
