namespace CloudScribe.App.Composition;

/// <summary>
/// Consumes exactly one coherent current Stage6 request, performs the production compile,
/// and restores the claimed request only when compilation/validation fails and no newer
/// request has replaced it.
/// </summary>
public sealed class GoogleGenerationProductionPreparationCoordinator
{
    private readonly GoogleGenerationProductionCurrentRequestStateOwner _currentRequestOwner;
    private readonly GoogleGenerationProductionCompileAndPrepareService _compileAndPrepare;

    public GoogleGenerationProductionPreparationCoordinator(
        GoogleGenerationProductionCurrentRequestStateOwner currentRequestOwner,
        GoogleGenerationProductionCompileAndPrepareService compileAndPrepare)
    {
        _currentRequestOwner = currentRequestOwner
            ?? throw new ArgumentNullException(nameof(currentRequestOwner));
        _compileAndPrepare = compileAndPrepare
            ?? throw new ArgumentNullException(nameof(compileAndPrepare));
    }

    public async Task<GoogleGenerationProductionPendingApprovalStateOwner.PendingState> PrepareCurrentAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GoogleGenerationProductionCurrentRequestStateOwner.CurrentRequest claimed =
            _currentRequestOwner.ClaimCurrent();

        try
        {
            return await _compileAndPrepare
                .CompileAndPrepareAsync(claimed.Evidence, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            _currentRequestOwner.RestoreIfUnchanged(claimed);
            throw;
        }
    }
}
