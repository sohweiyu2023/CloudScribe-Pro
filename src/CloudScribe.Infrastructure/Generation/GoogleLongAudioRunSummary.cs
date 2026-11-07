namespace CloudScribe.Infrastructure.Generation;

/// <summary>Read-only presentation. Retained commitments are conservative holds, not invoiced charges.</summary>
public sealed record GoogleLongAudioRunSummary(GoogleLongAudioTiming? Timing, int RetainedAttempts,
    long JobRetainedCommitmentsUsdCents, long AllJobsRetainedCommitmentsUsdCents,
    long CumulativeProvisionUsdCents, long ApprovedCapUsdCents)
{
    public static GoogleLongAudioRunSummary FromLedger(GoogleLongAudioBudgetLedger ledger, string rootBinding,
        GoogleLongAudioTiming? timing, long otherRetainedCents, long taxFxCents, long capCents)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentOutOfRangeException.ThrowIfNegative(otherRetainedCents);
        ArgumentOutOfRangeException.ThrowIfNegative(taxFxCents);
        ArgumentOutOfRangeException.ThrowIfNegative(capCents);
        long job = 0, all = 0; int count = 0;
        HashSet<Guid> ids = [];
        foreach (var claim in ledger.Claims)
        {
            if (claim.MaximumUsdCents < 0 || !ids.Add(claim.AttemptId)) throw new InvalidDataException("Invalid retained spending history.");
            all = checked(all + claim.MaximumUsdCents);
            if (string.Equals(claim.RequestBindingSha256, rootBinding, StringComparison.Ordinal)
                || string.Equals(claim.Recovery?.RootBindingSha256, rootBinding, StringComparison.Ordinal))
            { job = checked(job + claim.MaximumUsdCents); count++; }
        }
        return new(timing, count, job, checked(all + otherRetainedCents), checked(all + otherRetainedCents + taxFxCents), capCents);
    }
}
