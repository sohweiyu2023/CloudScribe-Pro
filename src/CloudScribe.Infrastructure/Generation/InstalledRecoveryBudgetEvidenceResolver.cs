using CloudScribe.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Uses independently installer-admitted totals and exact baseline rows. Unknown financial changes fail closed.</summary>
public sealed partial class InstalledRecoveryBudgetEvidenceResolver(InstalledRecoveryAdmissionStore admission,
    IDbContextFactory<CloudScribeDbContext> factory) : IGoogleRecoveryBudgetEvidenceResolver
{
    public async Task<GoogleRecoveryBudgetEvidence?> ResolveAsync(string accountId, string projectId, string currency, int scale,
        string currentFinancialStateSha256, CancellationToken cancellationToken = default)
    {
        var policy = await admission.LoadVerifiedPolicyAsync(cancellationToken).ConfigureAwait(false);
        if (policy is null || !string.Equals(policy.AccountId, accountId, StringComparison.Ordinal)
            || !string.Equals(policy.ProjectId, projectId, StringComparison.Ordinal)
            || !string.Equals(policy.Currency, currency, StringComparison.Ordinal) || policy.Scale != scale) return null;
        using var context = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var connection = (SqliteConnection)context.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        // Read-only deferred snapshot. The service's outer BEGIN IMMEDIATE excludes competing writers;
        // this connection never attempts a write or waits for its owner's reserved write lock.
        using var transaction = connection.BeginTransaction(deferred: true);
        var snapshot = await RecoveryFinancialSnapshot.ReadAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(snapshot.FinancialSha256, currentFinancialStateSha256, StringComparison.Ordinal)) return null;
        var budget = DeriveBudget(policy, snapshot);
        return budget is null ? null : new(accountId, projectId, currency, scale, currentFinancialStateSha256, budget, policy.ExpiresAtUtc);
    }

    private static GoogleRecoveryBudgetReview? DeriveBudget(RecoveryAdmissionPolicy policy, RecoveryFinancialSnapshot snapshot)
    {
        var baseline = policy.Budget;
        var queueExtra = CheckBaseline(snapshot.Queue, baseline.QueueRows, 3);
        var spendExtra = CheckBaseline(snapshot.Spend, baseline.SpendRows, 1);
        var recoveryExtra = CheckBaseline(snapshot.Recovery, baseline.RecoveryRows, 1);
        if (queueExtra is null || spendExtra is not { Length: 0 } || recoveryExtra is null) return null;
        long additional = 0;
        HashSet<string> children = new(StringComparer.Ordinal);
        foreach (var attempt in recoveryExtra)
        {
            var reservation = ValidateAdditionalReservation(policy, attempt, snapshot.QueueStates);
            if (reservation is null || !children.Add(reservation.Value.Child)) return null;
            additional = checked(additional + reservation.Value.Reserved);
        }
        var extraQueueIdentities = queueExtra.Select(x => RecoveryFinancialSnapshot.Identity(x, 3)).ToHashSet(StringComparer.Ordinal);
        if (!extraQueueIdentities.SetEquals(children.Select(child => RecoveryFinancialSnapshot.Hash(new object?[]
            { policy.AccountId, GoogleGenerationProvider.SynthesizeOperationStableId, child })))) return null;
        long committed = checked(baseline.ProjectCommittedMinorUnits + additional);
        long remaining = checked(baseline.AllocationCeilingMinorUnits - baseline.AllocationCommittedMinorUnits - additional);
        if (committed > baseline.ProjectCeilingMinorUnits || remaining < 0) return null;
        var scopes = snapshot.Scopes.Where(x => Equals(x[0], policy.AccountId) && Equals(x[1], policy.ProjectId)
            && Equals(x[2], policy.Currency) && Equals(x[3], (long)policy.Scale)).ToArray();
        if (scopes.Length > 1 || (scopes.Length == 0 && recoveryExtra.Length != 0)) return null;
        if (scopes.Length == 1 && (scopes[0][4] is not long floor || floor > committed
            || !Equals(scopes[0][5], baseline.ProjectCeilingMinorUnits) || scopes[0][6] is not long allocation || remaining > allocation)) return null;
        return new(committed, baseline.ProjectCeilingMinorUnits, remaining, baseline.ReviewedEvidenceSha256);
    }

    private static object?[][]? CheckBaseline(IReadOnlyList<object?[]> current,
        IReadOnlyList<RecoveryFinancialRowPin> pins, int identityColumns)
    {
        var indexed = current.ToDictionary(x => RecoveryFinancialSnapshot.Identity(x, identityColumns), StringComparer.Ordinal);
        foreach (var pin in pins)
            if (!indexed.Remove(pin.Identity, out var row)
                || !string.Equals(RecoveryFinancialSnapshot.Hash(row), pin.Sha256, StringComparison.Ordinal)) return null;
        return indexed.Values.ToArray();
    }
}
