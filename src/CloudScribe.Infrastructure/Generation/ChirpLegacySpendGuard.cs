using System.Data.Common;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Legacy spend paths cannot omit separately retained article reservations. Local historical import/export is unaffected.</summary>
internal static class ChirpLegacySpendGuard
{
    internal static async Task RequireNoArticleReservationsAsync(DbConnection connection, DbTransaction? transaction,
        string account, CancellationToken token, IChirpArticleAdmissionStore? admission = null)
    {
        if (admission is not null)
        {
            if (admission is IChirpArticleAdmissionCatalog catalogStore)
            {
                var catalog = await catalogStore.LoadCatalogVerifiedAsync(token).ConfigureAwait(false);
                if (catalog.Families.Any(x => string.Equals(x.Policy.AccountId, account, StringComparison.Ordinal)))
                    throw new InvalidOperationException("An independent article family allowance is active; ordinary spend cannot compete with its retained budget.");
            }
            var policy = await admission.LoadVerifiedAsync(token).ConfigureAwait(false);
            if (policy is not null && string.Equals(policy.AccountId, account, StringComparison.Ordinal))
                throw new InvalidOperationException("A reviewed internal article allocation is active; use its dedicated approval path so legacy spend cannot compete with its retained budget.");
        }
        using var exists = connection.CreateCommand(); exists.Transaction = transaction;
        exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='chirp_article_attempts';";
        if (Convert.ToInt64(await exists.ExecuteScalarAsync(token).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) == 0) return;
        using var rows = connection.CreateCommand(); rows.Transaction = transaction;
        rows.CommandText = "SELECT COUNT(*) FROM chirp_article_attempts WHERE AccountId=@account;";
        var parameter = rows.CreateParameter(); parameter.ParameterName = "@account"; parameter.Value = account; rows.Parameters.Add(parameter);
        if (Convert.ToInt64(await rows.ExecuteScalarAsync(token).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) != 0)
            throw new InvalidOperationException("Retained internal article reservations require the dedicated article approval path; legacy spend cannot exclude those holds.");
    }
}
