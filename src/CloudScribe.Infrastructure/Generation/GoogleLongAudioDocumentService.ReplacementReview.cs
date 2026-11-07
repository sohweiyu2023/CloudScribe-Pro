namespace CloudScribe.Infrastructure.Generation;

public sealed partial class GoogleLongAudioDocumentService
{
    private async Task<(int Count, long Additional)> ReviewReplacementsAsync(InstalledGoogleLongAudioContext context,
        GoogleLongAudioRequest root, string source, CancellationToken token)
    {
        if (context.Scope.FailedRangeReplacements is not { Count: > 0 }
            && context.Scope.DeferredUnknownRanges?.Any(x => x.ExplicitRegeneration is not null) != true) return (0, 0);
        var resolver = new GoogleLongAudioFailedRangeReplacementResolver(root, context.Scope, context.OperationsDirectory, context.OutputDirectory);
        var authority = new GoogleLongAudioRecoveryAuthority(root, context.OperationsDirectory, context.OutputDirectory);
        if (context.Scope.DeferredUnknownRanges is { Count: > 0 })
            authority.Deferred = new(root, context.Scope, context.OperationsDirectory, context.OutputDirectory,
                context.LedgerPath, context.ScopeSha256, authority);
        var admission = new GoogleLongAudioLedgerAdmission(context.ParentPolicyPath, context.ParentPolicySha256,
            context.ScopePath, context.ScopeSha256, context.LedgerPath, (_, _, _) => ValueTask.CompletedTask, clock).WithRecovery(authority);
        long additional = 0; int count = 0;
        foreach (var replacement in resolver.ReviewRequests(source))
        {
            var cost = await admission.ReviewAsync(replacement, token).ConfigureAwait(false);
            additional = checked(additional + cost.AdditionalCommitmentUsdCents); count++;
        }
        return (count, additional);
    }
}
