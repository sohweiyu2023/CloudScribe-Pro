namespace CloudScribe.Infrastructure.Generation;

public sealed record GoogleCapabilityRefreshDecision(
    bool Accepted,
    string DiagnosticCode,
    GoogleCapabilitySnapshot? Snapshot);

public static class GoogleCapabilityRefreshPolicy
{
    public static GoogleCapabilityRefreshDecision Evaluate(
        GoogleGenerationAccount account,
        GoogleCapabilitySnapshot? current,
        GoogleCapabilitySnapshot candidate,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(candidate);
        account.Validate();
        candidate.Validate(nowUtc);

        if (!string.Equals(candidate.AccountId, account.AccountId, StringComparison.Ordinal))
            return new(false, "capability-account-mismatch", null);

        if (candidate.IsStale(nowUtc))
            return new(false, "capability-candidate-stale", null);

        if (current is null)
            return new(true, "capability-initial-refresh-accepted", candidate);

        current.Validate(nowUtc);
        if (!string.Equals(current.AccountId, account.AccountId, StringComparison.Ordinal))
            return new(false, "capability-current-account-mismatch", null);

        if (candidate.ObservedAtUtc < current.ObservedAtUtc)
            return new(false, "capability-observation-regressed", null);

        if (candidate.ObservedAtUtc == current.ObservedAtUtc &&
            !string.Equals(candidate.ProvenanceId, current.ProvenanceId, StringComparison.Ordinal))
        {
            return new(false, "capability-conflicting-provenance", null);
        }

        if (candidate.ObservedAtUtc == current.ObservedAtUtc &&
            string.Equals(candidate.ProvenanceId, current.ProvenanceId, StringComparison.Ordinal))
        {
            return new(true, "capability-refresh-idempotent", current);
        }

        return new(true, "capability-refresh-accepted", candidate);
    }
}
