using System.Text.Json;
using CloudScribe.Application.Providers;
using CloudScribe.Infrastructure.Persistence;
using CloudScribe.Infrastructure.Persistence.Entities;
using CloudScribe.Providers.Abstractions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Only private repair metadata and one atomic account-binding transition; no credential or financial writes.</summary>
public sealed partial class GoogleCredentialRepairJournalStore(IDbContextFactory<CloudScribeDbContext> factory, TimeProvider clock)
{
    internal Task<GoogleCredentialRepairState> ReadAsync(string account, CancellationToken token) =>
        TransactionAsync((context, connection, transaction) => ReadStateAsync(context, connection, transaction, account, token), token);

    internal Task<string> StatusAsync(string account, CancellationToken token) =>
        TransactionAsync(async (_, connection, transaction) =>
        {
            using var command = ChirpArticleSql.Command(connection, transaction, """
                SELECT r.Phase,r.StagedReference,r.ExpectedAccountJson,a.CredentialTargetName,a.Revision
                FROM google_credential_repairs r JOIN provider_accounts a ON a.AccountId=r.AccountId AND a.ProviderStableId=$provider
                WHERE r.AccountId=$account ORDER BY r.rowid DESC LIMIT 1;
                """, ("$account", account), ("$provider", GoogleGenerationProvider.StableProviderId));
            using var row = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await row.ReadAsync(token).ConfigureAwait(false)) return "No local credential repair recorded.";
            string phase = row.GetString(0);
            if (string.Equals(phase, "Committed", StringComparison.Ordinal))
            {
                var original = JsonSerializer.Deserialize<ProviderAccountSnapshot>(row.GetString(2))
                    ?? throw new InvalidDataException("Repair lineage is unreadable.");
                if (row.IsDBNull(3) || !string.Equals(row.GetString(1), row.GetString(3), StringComparison.Ordinal) || row.GetInt64(4) != original.Revision + 1)
                    return "A committed repair is retained, but the current account changed; reconcile before further use.";
            }
            return phase switch
            {
                "Committed" => "Local credential reconnection committed. Restart and refresh real voices; earlier paid approvals remain invalid.",
                "Prepared" or "Staging" or "Staged" => "An interrupted local repair is retained. Re-select the same candidate and explicitly review resume; no automatic cleanup or retry.",
                _ => "Repair state requires manual reconciliation; no automatic write or cleanup.",
            };
        }, token);

    private async Task<T> TransactionAsync<T>(Func<CloudScribeDbContext, SqliteConnection, SqliteTransaction, Task<T>> operation, CancellationToken token)
    {
        using var context = await factory.CreateDbContextAsync(token).ConfigureAwait(false);
        var connection = (SqliteConnection)context.Database.GetDbConnection();
        GoogleCredentialRepairPaths.RequirePhysical(connection.DataSource);
        await connection.OpenAsync(token).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        _ = context.Database.UseTransaction(transaction);
        T value = await operation(context, connection, transaction).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return value;
    }

    private static async Task<GoogleCredentialRepairState> ReadStateAsync(CloudScribeDbContext context, SqliteConnection connection,
        SqliteTransaction transaction, string account, CancellationToken token)
    {
        var entity = await context.ProviderAccounts.AsNoTracking().SingleAsync(
            x => x.ProviderStableId == GoogleGenerationProvider.StableProviderId && x.AccountId == account, token).ConfigureAwait(false);
        var snapshot = Map(entity);
        GoogleCredentialRepairVerifier.RequireBinding(snapshot);
        var financial = await RecoveryFinancialSnapshot.ReadAsync(connection, transaction, token).ConfigureAwait(false);
        if (financial.QueueStates.Any(x => string.Equals(x.Account, account, StringComparison.Ordinal) && x.Unresolved != 0))
            throw new InvalidOperationException("An uncertain provider attempt requires reconciliation before credential repair.");
        using var pending = ChirpArticleSql.Command(connection, transaction, """
            SELECT (SELECT COUNT(*) FROM google_recovery_attempts WHERE AccountId=$account AND Consumed=0)
                 + (SELECT COUNT(*) FROM chirp_article_attempts WHERE AccountId=$account AND Consumed=0);
            """, ("$account", account));
        if (Convert.ToInt64(await pending.ExecuteScalarAsync(token).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) != 0)
            throw new InvalidOperationException("A reserved request must be reconciled before credential repair.");
        using var project = ChirpArticleSql.Command(connection, transaction,
            "SELECT DISTINCT ProjectId FROM google_generation_project_authorizations WHERE AccountId=$account LIMIT 2;", ("$account", account));
        using var rows = await project.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await rows.ReadAsync(token).ConfigureAwait(false)) throw new InvalidOperationException("No retained project binding is available.");
        string projectId = rows.GetString(0);
        if (await rows.ReadAsync(token).ConfigureAwait(false)) throw new InvalidOperationException("Ambiguous retained project binding.");
        await rows.DisposeAsync().ConfigureAwait(false);
        string financialSha = RecoveryFinancialSnapshot.Hash(new
        {
            financial.Queue,
            financial.Spend,
            financial.Recovery,
            financial.Scopes,
            financial.Transitions,
            Chirp = await ChirpArticleSql.ReadAsync(connection, transaction, "chirp_article_attempts", token).ConfigureAwait(false)
        });
        return new(snapshot, projectId, financialSha, await ActiveAsync(connection, transaction, account, token).ConfigureAwait(false));
    }

    private static ProviderAccountSnapshot Map(ProviderAccountEntity value) => new(
        new ProviderAccountReference(value.ProviderStableId, value.AccountId, value.DisplayName,
            value.CredentialTargetName is null ? null : new CredentialReference(value.CredentialTargetName),
            value.EndpointId, value.RegionId, value.EndpointOrigin is null ? null : new Uri(value.EndpointOrigin)),
        value.IsEnabled, value.Revision, DateTimeOffset.FromUnixTimeMilliseconds(value.CreatedAtUnixMilliseconds),
        DateTimeOffset.FromUnixTimeMilliseconds(value.UpdatedAtUnixMilliseconds));

    private static async Task<GoogleCredentialRepairJournal?> ActiveAsync(SqliteConnection connection, SqliteTransaction transaction,
        string account, CancellationToken token)
    {
        using var command = ChirpArticleSql.Command(connection, transaction,
            "SELECT RepairId,ExpectedAccountJson,FinancialSha256,IdentityJson,StagedReference,Phase,ApprovalSha256 FROM google_credential_repairs WHERE AccountId=$account AND Phase NOT IN ('Committed','Abandoned');",
            ("$account", account));
        using var row = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await row.ReadAsync(token).ConfigureAwait(false)) return null;
        return new(Guid.ParseExact(row.GetString(0), "N"), account, row.GetString(1), row.GetString(2),
            JsonSerializer.Deserialize<GoogleCredentialRepairIdentity>(row.GetString(3)) ?? throw new InvalidDataException("Repair identity unavailable."),
            row.GetString(4), row.GetString(5), row.GetString(6));
    }
}
