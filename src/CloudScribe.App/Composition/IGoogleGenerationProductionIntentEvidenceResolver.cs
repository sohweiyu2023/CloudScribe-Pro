namespace CloudScribe.App.Composition;

/// <summary>
/// Production-only boundary that resolves authorization/pricing/trust/queue/reconciliation state
/// for a claimed request intent. Implementations must fail closed when any required authoritative
/// source is unavailable or no longer matches the intent.
/// </summary>
public interface IGoogleGenerationProductionIntentEvidenceResolver
{
    Task<GoogleGenerationProductionCompileEvidence> ResolveAsync(
        GoogleGenerationProductionRequestIntent intent,
        CancellationToken cancellationToken = default);
}
