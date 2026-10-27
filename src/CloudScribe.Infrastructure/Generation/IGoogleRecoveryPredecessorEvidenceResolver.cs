namespace CloudScribe.Infrastructure.Generation;

/// <summary>Trusted historical request-to-narration association; never reconstructed from current selection.</summary>
public interface IGoogleRecoveryPredecessorEvidenceResolver
{
    Task<GoogleRecoveryPredecessorEvidence?> ResolveAsync(string accountId, string operationId, string historicalKey,
        string exactHistoricalStateSha256, CancellationToken cancellationToken = default);
}
