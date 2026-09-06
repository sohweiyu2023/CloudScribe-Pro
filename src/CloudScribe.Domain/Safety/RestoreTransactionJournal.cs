using System.Security.Cryptography;
using System.Text;

namespace CloudScribe.Domain.Safety;

public sealed record RestoreTransactionJournal(
    Guid TransactionId,
    string PlanSha256,
    RestoreTransactionState State,
    IReadOnlyList<string> CompletedRelativePaths,
    DateTimeOffset UpdatedAtUtc)
{
    public RestoreExecutionPlan? PersistedPlan { get; init; }

    public static RestoreTransactionJournal Start(RestoreExecutionPlan plan, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (nowUtc == default) throw new ArgumentException("Timestamp is required.", nameof(nowUtc));
        return new RestoreTransactionJournal(
            Guid.NewGuid(),
            ComputePlanSha256(plan),
            RestoreTransactionState.Pending,
            Array.Empty<string>(),
            nowUtc.ToUniversalTime())
        {
            PersistedPlan = plan,
        };
    }

    public RestoreTransactionJournal BeginCopy(RestoreExecutionPlan plan, DateTimeOffset nowUtc)
    {
        EnsurePlan(plan);
        EnsureForwardTime(nowUtc);
        if (State != RestoreTransactionState.Pending)
            throw new InvalidOperationException("Restore copying can begin only from Pending state.");
        return this with { State = RestoreTransactionState.Copying, UpdatedAtUtc = nowUtc.ToUniversalTime() };
    }

    public RestoreTransactionJournal MarkCopied(RestoreExecutionPlan plan, string relativePath, DateTimeOffset nowUtc)
    {
        EnsurePlan(plan);
        EnsureForwardTime(nowUtc);
        if (State != RestoreTransactionState.Copying)
            throw new InvalidOperationException("Restore files can be marked copied only while Copying.");
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (!plan.Steps.Any(step => string.Equals(step.RelativePath, relativePath, StringComparison.Ordinal)))
            throw new InvalidOperationException("Restore journal cannot record a path absent from the bound plan.");
        if (CompletedRelativePaths.Contains(relativePath, StringComparer.Ordinal))
            return this with { UpdatedAtUtc = nowUtc.ToUniversalTime() };

        return this with
        {
            CompletedRelativePaths = CompletedRelativePaths.Append(relativePath).Order(StringComparer.Ordinal).ToArray(),
            UpdatedAtUtc = nowUtc.ToUniversalTime(),
        };
    }

    public RestoreTransactionJournal BeginVerification(RestoreExecutionPlan plan, DateTimeOffset nowUtc)
    {
        EnsurePlan(plan);
        EnsureForwardTime(nowUtc);
        if (State != RestoreTransactionState.Copying)
            throw new InvalidOperationException("Restore verification can begin only after copying.");
        if (CompletedRelativePaths.Count != plan.Steps.Count ||
            plan.Steps.Any(step => !CompletedRelativePaths.Contains(step.RelativePath, StringComparer.Ordinal)))
            throw new InvalidOperationException("Restore verification cannot begin until every planned file is copied.");
        return this with { State = RestoreTransactionState.Verifying, UpdatedAtUtc = nowUtc.ToUniversalTime() };
    }

    public RestoreTransactionJournal Commit(RestoreExecutionPlan plan, DateTimeOffset nowUtc)
    {
        EnsurePlan(plan);
        EnsureForwardTime(nowUtc);
        if (State != RestoreTransactionState.Verifying)
            throw new InvalidOperationException("Restore can commit only after verification has begun.");
        return this with { State = RestoreTransactionState.Committed, UpdatedAtUtc = nowUtc.ToUniversalTime() };
    }

    public RestoreTransactionJournal RequireRollback(RestoreExecutionPlan plan, DateTimeOffset nowUtc)
    {
        EnsurePlan(plan);
        EnsureForwardTime(nowUtc);
        if (State is RestoreTransactionState.Committed or RestoreTransactionState.RollbackRequired or RestoreTransactionState.RolledBack)
            throw new InvalidOperationException("Committed, already-failed, or rolled-back restore transactions cannot transition to rollback again.");
        return this with { State = RestoreTransactionState.RollbackRequired, UpdatedAtUtc = nowUtc.ToUniversalTime() };
    }

    public RestoreTransactionJournal CompleteRollback(RestoreExecutionPlan plan, DateTimeOffset nowUtc)
    {
        EnsurePlan(plan);
        EnsureForwardTime(nowUtc);
        if (State != RestoreTransactionState.RollbackRequired)
            throw new InvalidOperationException("Restore rollback can complete only from RollbackRequired state.");
        return this with
        {
            State = RestoreTransactionState.RolledBack,
            CompletedRelativePaths = Array.Empty<string>(),
            UpdatedAtUtc = nowUtc.ToUniversalTime(),
        };
    }

    public RestoreExecutionPlan RequirePersistedPlan()
    {
        RestoreExecutionPlan plan = PersistedPlan
            ?? throw new InvalidDataException("Restore recovery journal does not contain the authenticated execution plan required for restart recovery.");
        EnsurePlan(plan);
        return plan;
    }

    public void EnsurePlan(RestoreExecutionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var current = ComputePlanSha256(plan);
        if (!CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(PlanSha256),
            Convert.FromHexString(current)))
            throw new InvalidOperationException("Restore transaction plan changed after the transaction began.");
    }

    private void EnsureForwardTime(DateTimeOffset nowUtc)
    {
        if (nowUtc == default || nowUtc.ToUniversalTime() < UpdatedAtUtc)
            throw new InvalidOperationException("Restore transaction time cannot move backwards.");
    }

    public static string ComputePlanSha256(RestoreExecutionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var canonical = new StringBuilder();
        canonical.Append(plan.RestoreRoot).Append('\n').Append(plan.TotalBytes).Append('\n');
        foreach (var step in plan.Steps.OrderBy(static step => step.RelativePath, StringComparer.Ordinal))
        {
            canonical.Append(step.RelativePath).Append('\n')
                .Append(step.DestinationPath).Append('\n')
                .Append(step.Length).Append('\n')
                .Append(step.Sha256.ToLowerInvariant()).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
    }
}
