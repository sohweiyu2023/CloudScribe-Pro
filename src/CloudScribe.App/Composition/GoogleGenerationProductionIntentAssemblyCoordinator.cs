namespace CloudScribe.App.Composition;

/// <summary>
/// Resolves one claimed user/request intent into authoritative production compile evidence and
/// publishes only evidence that is still bound to that exact intent. Authorization assertions are
/// never accepted from the shell/request caller.
/// </summary>
public sealed class GoogleGenerationProductionIntentAssemblyCoordinator
{
    private readonly GoogleGenerationProductionRequestIntentStateOwner _intentOwner;
    private readonly GoogleGenerationProductionCurrentRequestStateOwner _currentRequestOwner;
    private readonly IGoogleGenerationProductionIntentEvidenceResolver _evidenceResolver;

    public GoogleGenerationProductionIntentAssemblyCoordinator(
        GoogleGenerationProductionRequestIntentStateOwner intentOwner,
        GoogleGenerationProductionCurrentRequestStateOwner currentRequestOwner,
        IGoogleGenerationProductionIntentEvidenceResolver evidenceResolver)
    {
        _intentOwner = intentOwner ?? throw new ArgumentNullException(nameof(intentOwner));
        _currentRequestOwner = currentRequestOwner ?? throw new ArgumentNullException(nameof(currentRequestOwner));
        _evidenceResolver = evidenceResolver ?? throw new ArgumentNullException(nameof(evidenceResolver));
    }

    public async Task<GoogleGenerationProductionCurrentRequestStateOwner.CurrentRequest> AssembleCurrentAsync(
        CancellationToken cancellationToken = default)
    {
        GoogleGenerationProductionRequestIntentStateOwner.CurrentIntent claimed = _intentOwner.ClaimCurrent();

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            GoogleGenerationProductionCompileEvidence evidence = await _evidenceResolver.ResolveAsync(
                claimed.Intent,
                cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "Authoritative Google generation production evidence resolver returned no evidence.");

            ValidateIntentBinding(claimed.Intent, evidence);
            cancellationToken.ThrowIfCancellationRequested();
            return _currentRequestOwner.Publish(evidence);
        }
        catch
        {
            _intentOwner.RestoreIfUnchanged(claimed);
            throw;
        }
    }

    private static void ValidateIntentBinding(
        GoogleGenerationProductionRequestIntent intent,
        GoogleGenerationProductionCompileEvidence evidence)
    {
        if (!ReferenceEquals(intent.Plan, evidence.Plan)
            || !ReferenceEquals(intent.CompilationOptions, evidence.CompilationOptions)
            || !string.Equals(intent.AccountId, evidence.Account?.AccountId, StringComparison.Ordinal)
            || !string.Equals(intent.ProjectId, evidence.ProjectId, StringComparison.Ordinal)
            || !string.Equals(intent.ModelId, evidence.ModelId, StringComparison.Ordinal)
            || !string.Equals(intent.IdempotencyKey, evidence.IdempotencyKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Authoritative Google generation production evidence is not bound to the claimed request intent.");
        }

        if (evidence.NowUtc < intent.CapturedAtUtc)
        {
            throw new InvalidOperationException(
                "Authoritative Google generation production evidence predates the claimed request intent.");
        }
    }
}
