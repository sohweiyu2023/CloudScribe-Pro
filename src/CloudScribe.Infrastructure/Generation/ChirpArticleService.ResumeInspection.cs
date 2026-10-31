using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class ChirpArticleService
{
    public async Task<ChirpReservationResumeInspection> InspectReservedResumeAdmissionAsync(Guid authorization,
        ChirpArticleAdmissionPolicy historicalPolicy, Guid catalogId, DateTimeOffset catalogExpiry, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(historicalPolicy);
        var history = ChirpPolicyFiles.Parse<ChirpArticleAdmissionPolicy>(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(historicalPolicy));
        if (!string.Equals(history.ProfileRootSha256, InstalledRecoveryAdmissionStore.ProfileHash(paths.RootDirectory), StringComparison.Ordinal)) throw ResumeRejected();
        using var context = await factory.CreateDbContextAsync(token).ConfigureAwait(false);
        var connection = (SqliteConnection)context.Database.GetDbConnection(); await connection.OpenAsync(token).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        var rows = await ChirpArticleSql.ReadAsync(connection, transaction, "chirp_article_owned_attempts", token).ConfigureAwait(false);
        var row = rows.SingleOrDefault(x => Equals(x[0], authorization.ToString("N")));
        if (row is not { Length: 18 } || !Equals(row[16], 0L) || row[17] is not null
            || !Equals(row[3], ChirpPolicyValidation.Hash(history))) throw ResumeRejected();
        var approvedAt = DateTimeOffset.FromUnixTimeMilliseconds((long)row[15]!);
        ChirpPolicyValidation.RequireValid(history, approvedAt);
        var financial = await ReadFinancialAsync(connection, transaction, history, token).ConfigureAwait(false);
        if (!Equals(row[14], financial.Sha256)) throw ResumeRejected();
        await RequireReadyAsync(connection, transaction, financial, authorization, token).ConfigureAwait(false);
        await RequireNoReservedOutcomeAsync(connection, transaction, row, token).ConfigureAwait(false);
        var queue = financial.Old.Queue.Single(x => Equals(x[0], row[5]) && Equals(x[1], GoogleGenerationProvider.SynthesizeOperationStableId) && Equals(x[2], row[7]));
        var envelope = ChirpPolicyFiles.Parse<GoogleGenerationSubmissionEnvelope>(Encoding.UTF8.GetBytes((string)row[9]!));
        string provider = await ResumeProviderEvidenceAsync(connection, transaction, envelope, history.ProjectId, catalogId, catalogExpiry, token).ConfigureAwait(false);
        return new(authorization, RecoveryFinancialSnapshot.Hash(row), RecoveryFinancialSnapshot.Hash(queue), financial.Sha256,
            provider, envelope, approvedAt, (long)row[13]!, financial.ProjectCommitted);
    }
}
