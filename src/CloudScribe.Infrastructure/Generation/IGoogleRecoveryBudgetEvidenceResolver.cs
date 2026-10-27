namespace CloudScribe.Infrastructure.Generation;

/// <summary>Trusted composition-owned source of approved project/allocation limits and conservative retained holds.
/// Ordinary preparation callers cannot provide the numbers or admit their own authority.</summary>
public interface IGoogleRecoveryBudgetEvidenceResolver
{
    Task<GoogleRecoveryBudgetEvidence?> ResolveAsync(string accountId, string projectId, string currency, int scale,
        string currentFinancialStateSha256, CancellationToken cancellationToken = default);
}
