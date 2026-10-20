using CloudScribe.Infrastructure.Configuration;
using Microsoft.Data.Sqlite;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Read-only inspection, never a reconciliation decision or a queue reset.</summary>
public sealed class LocalRequestStatusReader(AppPaths paths)
{
    public async Task<LocalRequestStatus> ReadAsync(CancellationToken cancellationToken = default)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
        };
        using var connection = new SqliteConnection(connectionString.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*), COALESCE(SUM(UnresolvedSubmission),0) FROM google_generation_queue_states;";
        int total;
        int unresolved;
        using (SqliteDataReader counts = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            _ = await counts.ReadAsync(cancellationToken).ConfigureAwait(false);
            total = counts.GetInt32(0); unresolved = counts.GetInt32(1);
        }
        command.CommandText = "SELECT IdempotencyKey, UnresolvedSubmission FROM google_generation_queue_states ORDER BY UnresolvedSubmission DESC, rowid DESC LIMIT 100;";
        using SqliteDataReader rows = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        List<string> details = [];
        while (await rows.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            string key = rows.GetString(0);
            string digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)))[..16];
            details.Add(digest + (rows.GetBoolean(1) ? " - unresolved: may have been charged; do not repeat" : " - no unresolved submission recorded (not proof of completed audio)"));
        }
        return new LocalRequestStatus(total, unresolved, details);
    }
}
