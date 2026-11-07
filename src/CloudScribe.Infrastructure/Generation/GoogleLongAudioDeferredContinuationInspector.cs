namespace CloudScribe.Infrastructure.Generation;

/// <summary>Native proof of exact deferred evidence; no scope, claim or provider client is created.
/// Journal readers may create empty lock metadata, so zero-live-write proofs must use pinned private copies.</summary>
public static class GoogleLongAudioDeferredContinuationInspector
{
    public static GoogleLongAudioCheckpoint Verify(GoogleLongAudioRequest root, GoogleLongAudioBudgetScope scope,
        string scopeSha256, string ledgerPath, string operationsDirectory, string outputDirectory)
    {
        ArgumentNullException.ThrowIfNull(root); ArgumentNullException.ThrowIfNull(scope);
        GoogleLongAudioDeferredRange.RequireScope(scope);
        var authority = new GoogleLongAudioRecoveryAuthority(root, operationsDirectory, outputDirectory);
        var work = new GoogleLongAudioDeferredWork(root, scope, operationsDirectory, outputDirectory, ledgerPath, scopeSha256, authority);
        work.RequireReady(root, newPost: false);
        return new GoogleLongAudioFileJournal(operationsDirectory).Load(scope.DeferredUnknownRanges![0].AttemptId);
    }
}
