namespace CloudScribe.Infrastructure.Generation;

/// <summary>Supplies batch-approved additions to the installed Long Audio scope. Implementations must not throw: any
/// problem means "no additions", so installed jobs keep working exactly as before.</summary>
public interface IGoogleLongAudioScopeExtensionSource
{
    /// <param name="scope">The installed scope, exactly as pinned (never already extended).</param>
    /// <param name="scopeSha256">Its pinned hash.</param>
    /// <param name="ledgerPath">The cumulative ledger, read to keep jobs that already hold commitments.</param>
    /// <param name="fixedCommitmentsUsdCents">Project ceiling + tax/FX provision + everything the installed scope approves.</param>
    /// <param name="cancellationToken">Cancels reading.</param>
    ValueTask<GoogleLongAudioScopeExtension> LoadAsync(GoogleLongAudioBudgetScope scope, string scopeSha256, string ledgerPath,
        long fixedCommitmentsUsdCents, CancellationToken cancellationToken);
}
