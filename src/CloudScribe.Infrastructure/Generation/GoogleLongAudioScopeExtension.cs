namespace CloudScribe.Infrastructure.Generation;

/// <summary>Batch-approved additions to an installed Long Audio scope. The installed scope's voice, language, encoding,
/// project, credential, bucket, pricing, total cap and existing jobs are never changed: additions are extra jobs and
/// exact request ceilings, admitted only while the total of all approvals still fits the installed cap.</summary>
public sealed record GoogleLongAudioScopeExtension(IReadOnlyList<GoogleLongAudioBatchJob> Jobs)
{
    public static readonly GoogleLongAudioScopeExtension Empty = new([]);

    /// <summary>Everything the installed scope already approves (requests, replacements and regenerations).</summary>
    public static long InstalledApprovedUsdCents(GoogleLongAudioBudgetScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        long total = 0;
        foreach (long amount in scope.ApprovedRequestCeilingsUsdCents?.Values ?? []) total = checked(total + amount);
        foreach (var replacement in scope.FailedRangeReplacements ?? []) total = checked(total + replacement.MaximumUsdCents);
        foreach (var descriptor in scope.DeferredUnknownRanges ?? [])
            if (descriptor.ExplicitRegeneration is { } regeneration) total = checked(total + regeneration.MaximumUsdCents);
        return total;
    }

    /// <summary>The installed scope with these jobs added. Throws if an addition collides with installed authority,
    /// so a faulty source can never silently replace an installed job or ceiling.</summary>
    public GoogleLongAudioBudgetScope Apply(GoogleLongAudioBudgetScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (Jobs.Count == 0) return scope;
        var ceilings = new Dictionary<string, long>(scope.ApprovedRequestCeilingsUsdCents ?? new Dictionary<string, long>(StringComparer.Ordinal), StringComparer.Ordinal);
        var jobs = (scope.DocumentJobs ?? []).ToList();
        var roots = (scope.AdaptiveRecoveryRootBindings ?? []).ToList();
        foreach (var job in Jobs)
        {
            if (jobs.Any(x => x.DocumentId == job.DocumentId || x.AttemptId == job.AttemptId) || !ceilings.TryAdd(job.RequestBindingSha256, job.CeilingUsdCents))
                throw new InvalidDataException("A batch-approved Long Audio job collides with installed authority.");
            jobs.Add(job.ToDocumentJob());
            if (job.AdaptiveRecovery) roots.Add(job.RequestBindingSha256);
        }
        return scope with
        {
            ApprovedRequestCeilingsUsdCents = ceilings,
            DocumentJobs = jobs,
            AdaptiveRecoveryRootBindings = roots.Count == 0 ? scope.AdaptiveRecoveryRootBindings : roots,
        };
    }

    /// <summary>The batch job whose root binding is <paramref name="rootBindingSha256"/>, if any.</summary>
    public GoogleLongAudioBatchJob? ForRoot(string rootBindingSha256) =>
        Jobs.SingleOrDefault(x => string.Equals(x.RequestBindingSha256, rootBindingSha256, StringComparison.Ordinal));
}
