using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class ChirpArticleService
{
    private static async Task AppendLegacyAssociationsAsync(SqliteConnection connection, SqliteTransaction transaction,
        ChirpArticleAdmissionPolicy policy, string plan, CancellationToken token)
    {
        foreach (var a in (policy.LegacyAssociations ?? []).Where(x => string.Equals(x.PlanSha256, plan, StringComparison.Ordinal)))
        {
            using var insert = ChirpArticleSql.Command(connection, transaction, """
                INSERT INTO chirp_article_legacy_associations VALUES($plan,$key,$json,$hash,$policy,$source)
                ON CONFLICT(PlanSha256,IdempotencyKey) DO NOTHING;
                """, ("$plan", plan), ("$key", a.IdempotencyKey), ("$json", JsonSerializer.Serialize(a)),
                ("$hash", ChirpPolicyValidation.Hash(a)), ("$policy", ChirpPolicyValidation.Hash(policy)), ("$source", policy.SourceApprovalSha256));
            await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
    }

    private static async Task<IReadOnlyList<object?[]>> AuditRowsAsync(SqliteConnection connection, SqliteTransaction transaction,
        ChirpArticleAdmissionPolicy policy, CancellationToken token)
    {
        var rows = await ChirpArticleSql.ReadAsync(connection, transaction, "chirp_article_legacy_associations", token).ConfigureAwait(false);
        var extra = ChirpArticleSql.Extra(rows, policy.LegacyAssociationRows ?? [], 2);
        foreach (var row in extra)
        {
            if (row.Length != 6 || row[2] is not string json || json.Length > 16384
                || !Equals(row[4], ChirpPolicyValidation.Hash(policy)) || !Equals(row[5], policy.SourceApprovalSha256))
                throw new InvalidOperationException("Unreviewed historical association audit appeared.");
            var a = ParseStored<ChirpLegacyAttemptAssociation>(json);
            if (!(policy.LegacyAssociations ?? []).Contains(a) || !Equals(row[0], a.PlanSha256)
                || !Equals(row[1], a.IdempotencyKey) || !Equals(row[3], ChirpPolicyValidation.Hash(a)))
                throw new InvalidOperationException("Historical association audit differs from independently admitted evidence.");
        }
        return rows;
    }
}
