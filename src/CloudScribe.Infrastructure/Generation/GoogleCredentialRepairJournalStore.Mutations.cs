using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class GoogleCredentialRepairJournalStore
{
    internal Task<bool> StartAsync(GoogleCredentialRepairState expected, GoogleCredentialRepairReview review, CancellationToken token) =>
        TransactionAsync(async (context, connection, transaction) =>
        {
            var current = await ReadStateAsync(context, connection, transaction, review.AccountId, token).ConfigureAwait(false);
            RequireExact(expected, current);
            RequireCurrent(review);
            using var command = ChirpArticleSql.Command(connection, transaction, """
                INSERT INTO google_credential_repairs VALUES($id,$account,$expected,$financial,$identity,$reference,'Prepared',$approval,$expires,$now)
                ON CONFLICT(RepairId) DO NOTHING;
                """, ("$id", review.RepairId.ToString("N")), ("$account", review.AccountId),
                ("$expected", JsonSerializer.Serialize(expected.Account)), ("$financial", expected.FinancialSha256),
                ("$identity", JsonSerializer.Serialize(review.Identity)), ("$reference", review.NewCredentialReference),
                ("$approval", review.ReviewSha256), ("$expires", review.ExpiresAtUtc.ToUnixTimeMilliseconds()),
                ("$now", clock.GetUtcNow().ToUnixTimeMilliseconds()));
            bool created = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
            var active = await ActiveAsync(connection, transaction, review.AccountId, token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Repair journal was not established.");
            RequireJournal(active, expected, review, checkApproval: false);
            await SetPhaseAsync(connection, transaction, review, active.Phase, token).ConfigureAwait(false);
            return created;
        }, token);

    internal Task<bool> PhaseAsync(GoogleCredentialRepairState expected, GoogleCredentialRepairReview review, string phase, CancellationToken token) =>
        TransactionAsync(async (context, connection, transaction) =>
        {
            var current = await ReadStateAsync(context, connection, transaction, review.AccountId, token).ConfigureAwait(false);
            RequireExact(expected, current);
            RequireCurrent(review);
            RequireJournal(current.Active ?? throw new InvalidOperationException("Repair journal is absent."), expected, review);
            if (phase is not ("Staging" or "Staged")) throw new InvalidOperationException("Unsupported repair phase.");
            await SetPhaseAsync(connection, transaction, review, phase, token).ConfigureAwait(false);
            return true;
        }, token);

    internal Task<bool> CommitAsync(GoogleCredentialRepairState expected, GoogleCredentialRepairReview review, CancellationToken token) =>
        TransactionAsync(async (context, connection, transaction) =>
        {
            var current = await ReadStateAsync(context, connection, transaction, review.AccountId, token).ConfigureAwait(false);
            RequireExact(expected, current);
            RequireCurrent(review);
            var journal = current.Active ?? throw new InvalidOperationException("Repair journal is absent.");
            RequireJournal(journal, expected, review);
            if (!string.Equals(journal.Phase, "Staged", StringComparison.Ordinal)) throw new InvalidOperationException("Repair generation is not verified.");
            using var update = ChirpArticleSql.Command(connection, transaction, """
                UPDATE provider_accounts SET CredentialTargetName=$new,Revision=Revision+1,UpdatedAtUnixMilliseconds=$now
                WHERE ProviderStableId=$provider AND AccountId=$account AND Revision=$revision AND CredentialTargetName=$old;
                """, ("$new", review.NewCredentialReference), ("$now", clock.GetUtcNow().ToUnixTimeMilliseconds()),
                ("$provider", GoogleGenerationProvider.StableProviderId), ("$account", review.AccountId),
                ("$revision", expected.Account.Revision), ("$old", review.OldCredentialReference));
            if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("Account binding changed; repair was not committed.");
            await SetPhaseAsync(connection, transaction, review, "Committed", token).ConfigureAwait(false);
            return true;
        }, token);

    private static void RequireExact(GoogleCredentialRepairState expected, GoogleCredentialRepairState current)
    {
        if (!string.Equals(JsonSerializer.Serialize(expected.Account), JsonSerializer.Serialize(current.Account), StringComparison.Ordinal)
            || !string.Equals(expected.ProjectId, current.ProjectId, StringComparison.Ordinal)
            || !string.Equals(expected.FinancialSha256, current.FinancialSha256, StringComparison.Ordinal))
            throw new InvalidOperationException("Account, project or financial evidence changed; repair stopped without resetting history.");
    }

    private static void RequireJournal(GoogleCredentialRepairJournal value, GoogleCredentialRepairState state, GoogleCredentialRepairReview review, bool checkApproval = true)
    {
        if (value.RepairId != review.RepairId || !string.Equals(value.AccountId, review.AccountId, StringComparison.Ordinal) || value.Identity != review.Identity
            || !string.Equals(value.StagedReference, review.NewCredentialReference, StringComparison.Ordinal)
            || !string.Equals(value.ExpectedAccountJson, JsonSerializer.Serialize(state.Account), StringComparison.Ordinal)
            || !string.Equals(value.FinancialSha256, state.FinancialSha256, StringComparison.Ordinal)
            || (checkApproval && !string.Equals(value.ApprovalSha256, review.ReviewSha256, StringComparison.Ordinal)))
            throw new InvalidOperationException("Existing repair lineage does not match this candidate and account.");
    }

    private void RequireCurrent(GoogleCredentialRepairReview review)
    {
        if (clock.GetUtcNow() >= review.ExpiresAtUtc) throw new InvalidOperationException("Repair review expired; no further credential write is approved.");
    }

    private async Task SetPhaseAsync(SqliteConnection connection, SqliteTransaction transaction, GoogleCredentialRepairReview review, string phase, CancellationToken token)
    {
        using var update = ChirpArticleSql.Command(connection, transaction,
            "UPDATE google_credential_repairs SET Phase=$phase,ApprovalSha256=$approval,ExpiresAt=$expires WHERE RepairId=$id;",
            ("$phase", phase), ("$approval", review.ReviewSha256), ("$expires", review.ExpiresAtUtc.ToUnixTimeMilliseconds()), ("$id", review.RepairId.ToString("N")));
        if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) throw new InvalidOperationException("Repair journal changed.");
        using var audit = ChirpArticleSql.Command(connection, transaction,
            "INSERT INTO google_credential_repair_events(RepairId,Phase,ApprovalSha256,ApprovalJson,ObservedAt) VALUES($id,$phase,$approval,$json,$now);",
            ("$id", review.RepairId.ToString("N")), ("$phase", phase), ("$approval", review.ReviewSha256),
            ("$json", JsonSerializer.Serialize(review)), ("$now", clock.GetUtcNow().ToUnixTimeMilliseconds()));
        await audit.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }
}
