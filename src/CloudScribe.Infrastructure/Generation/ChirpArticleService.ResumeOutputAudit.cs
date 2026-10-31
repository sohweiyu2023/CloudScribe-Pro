using System.Text;
using Microsoft.Data.Sqlite;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class ChirpArticleService
{
    // Historical output validation grants no new execution authority and remains available after expiry.
    private static async Task<string?> RequireReservedDispatchAuditAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid authorization, GoogleGenerationSubmissionEnvelope original, string localAttempt, CancellationToken token)
    {
        var chain = await ReadResumeChainAsync(connection, transaction, authorization, token).ConfigureAwait(false);
        if (chain.Count == 0) return null;
        using var command = ChirpArticleSql.Command(connection, transaction, """
            SELECT m.MappingId,m.PolicyJson,m.PolicySha256,m.CreatedAtUnixMilliseconds,
                d.MappingId,d.PolicySha256,d.ClaimedLocalAttemptId,d.ClaimedAtUnixMilliseconds,a.PolicySha256,a.Consumed,a.ClaimedLocalAttemptId,length(m.PolicyJson)
            FROM chirp_reserved_dispatch_mappings d JOIN chirp_reserved_catalog_mappings m ON m.MappingId=d.MappingId AND m.AuthorizationId=d.AuthorizationId
            JOIN chirp_article_owned_attempts a ON a.AuthorizationId=m.AuthorizationId WHERE m.AuthorizationId=$id;
            """, ("$id", authorization.ToString("N")));
        using var row = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await row.ReadAsync(token).ConfigureAwait(false)) throw ResumeRejected();
        if (row.IsDBNull(4) || row.IsDBNull(7) || row.GetInt64(11) is < 1 or > 8 * 1024 * 1024) throw ResumeRejected();
        var policy = ChirpPolicyFiles.Parse<ChirpReservationResumePolicy>(Encoding.UTF8.GetBytes(row.GetString(1)));
        var created = DateTimeOffset.FromUnixTimeMilliseconds(row.GetInt64(3));
        var claimed = DateTimeOffset.FromUnixTimeMilliseconds(row.GetInt64(7));
        ChirpReservationResumeValidation.RequireValid(policy, created);
        ChirpReservationResumeValidation.RequireValid(policy, claimed);
        if (claimed < created || policy.AuthorizationId != authorization || policy.OriginalEnvelope != original
            || !chain.Any(x => string.Equals(x.Sha256, row.GetString(2), StringComparison.Ordinal))
            || !string.Equals(policy.MappingId.ToString("N"), row.GetString(0), StringComparison.Ordinal)
            || !string.Equals(row.GetString(0), row.GetString(4), StringComparison.Ordinal)
            || !string.Equals(ChirpPolicyValidation.Hash(policy), row.GetString(2), StringComparison.Ordinal)
            || !string.Equals(row.GetString(2), row.GetString(5), StringComparison.Ordinal)
            || !string.Equals(localAttempt, row.GetString(6), StringComparison.Ordinal)
            || !string.Equals(ChirpPolicyValidation.Hash(policy.HistoricalPolicy), row.GetString(8), StringComparison.Ordinal)
            || row.GetInt64(9) != 1 || row.IsDBNull(10) || !string.Equals(localAttempt, row.GetString(10), StringComparison.Ordinal)) throw ResumeRejected();
        return row.GetString(2);
    }
}
