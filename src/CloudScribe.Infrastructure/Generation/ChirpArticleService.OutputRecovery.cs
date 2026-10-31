using System.Text.Json;
using CloudScribe.Application.Generation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class ChirpArticleService
{
    private async Task<OutputJournal> ReadJournalAsync(SqliteConnection connection, SqliteTransaction transaction, string id, CancellationToken token)
    {
        if (!Guid.TryParseExact(id, "N", out var authorization)) throw new InvalidDataException("Output journal identity is invalid.");
        using var command = ChirpArticleSql.Command(connection, transaction, """
            SELECT j.ReceiptJson,j.ReceiptSha256,a.PlanSha256,a.CoverageOrdinal,a.ChildKey,a.ClaimedLocalAttemptId,a.Consumed,a.EnvelopeJson,a.AccountId,a.ProjectId
            FROM chirp_article_output_journals j JOIN chirp_article_owned_attempts a ON a.AuthorizationId=j.AuthorizationId WHERE j.AuthorizationId=$id;
            """, ("$id", id));
        ChirpSegmentAcceptedAudio receipt; string plan, key, attempt, account, project; int ordinal; GoogleGenerationSubmissionEnvelope envelope;
        using (var row = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
        {
            if (!await row.ReadAsync(token).ConfigureAwait(false) || row.GetInt64(6) != 1) throw new InvalidOperationException("An output journal has no consumed request.");
            receipt = ParseStored<ChirpSegmentAcceptedAudio>(row.GetString(0));
            if (!string.Equals(ChirpPolicyValidation.Hash(receipt), row.GetString(1), StringComparison.Ordinal)) throw new InvalidOperationException("Output journal integrity failed.");
            plan = row.GetString(2); ordinal = row.GetInt32(3); key = row.GetString(4); attempt = row.GetString(5);
            envelope = ParseStored<GoogleGenerationSubmissionEnvelope>(row.GetString(7)); account = row.GetString(8); project = row.GetString(9);
        }
        var stored = await StoredAsync(connection, transaction, plan, token).ConfigureAwait(false);
        var part = stored.Article.Proposal.Parts.Single(x => x.CoverageOrdinal == ordinal && x.Range.ReusedReceiptSha256 is null);
        var audio = receipt.Audio;
        if (receipt.CoverageOrdinal != ordinal || receipt.Range != part.Range || !string.Equals(receipt.PlanSha256, plan, StringComparison.Ordinal)
            || audio.DocumentId != stored.Article.Proposal.DocumentId || audio.RevisionId != stored.Article.Proposal.RevisionId
            || !string.Equals(audio.TextSha256, part.Range.TextSha256, StringComparison.Ordinal) || !string.Equals(audio.PayloadSha256, part.PayloadSha256, StringComparison.Ordinal)
            || !string.Equals(audio.IdempotencyKey, key, StringComparison.Ordinal) || !string.Equals(audio.LocalAttemptId, attempt, StringComparison.Ordinal)
            || !string.Equals(audio.AccountId, account, StringComparison.Ordinal) || !string.Equals(account, stored.AccountId, StringComparison.Ordinal)
            || !string.Equals(project, stored.ProjectId, StringComparison.Ordinal) || envelope.ChirpAuthorizationId != authorization
            || envelope.RecoveryAuthorizationId is not null || !string.Equals(envelope.CompiledPayloadSha256, part.PayloadSha256, StringComparison.Ordinal)
            || !string.Equals(envelope.AccountId, account, StringComparison.Ordinal) || envelope.CompiledPayloadBytes != part.PayloadBytes
            || !string.Equals(audio.OperationId, GoogleGenerationProvider.SynthesizeOperationStableId, StringComparison.Ordinal)
            || !Guid.TryParseExact(audio.LocalAttemptId, "N", out _)
            || !string.Equals(audio.VoiceName, stored.Article.Proposal.VoiceName, StringComparison.Ordinal)
            || !string.Equals(audio.LanguageCode, stored.Article.Proposal.LanguageCode, StringComparison.Ordinal)
            || audio.AudioBytes is < 12 or > 256L * 1024 * 1024 || !ChirpPolicyValidation.IsHash(audio.AudioSha256)
            || !string.Equals(Path.GetFullPath(audio.AudioPath), Path.Combine(MediaRoot, id + ".mp3"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Output journal differs from the exact consumed segment and private output path.");
        ChirpArticleEvidenceResolver.RequireAcceptedQueue(audio, await RecoveryFinancialSnapshot.ReadAsync(connection, transaction, token).ConfigureAwait(false));
        string? mapping = await RequireReservedDispatchAuditAsync(connection, transaction, authorization, envelope, attempt, token).ConfigureAwait(false);
        if (!string.Equals(receipt.ExecutionMappingSha256, mapping, StringComparison.Ordinal)) throw ResumeRejected();
        return new(authorization, receipt);
    }

    private async Task FinishOutputJournalAsync(OutputJournal expected, CancellationToken token)
    {
        await VerifyMediaAsync(expected.Receipt.Audio.AudioPath, expected.Receipt.Audio, token).ConfigureAwait(false);
        using var context = await factory.CreateDbContextAsync(token).ConfigureAwait(false);
        var connection = (SqliteConnection)context.Database.GetDbConnection(); await connection.OpenAsync(token).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var current = await ReadJournalAsync(connection, transaction, expected.AuthorizationId.ToString("N"), token).ConfigureAwait(false);
        if (current != expected) throw new InvalidOperationException("Output journal changed before durable receipt publication.");
        string json = JsonSerializer.Serialize(current.Receipt), hash = ChirpPolicyValidation.Hash(current.Receipt);
        using (var insert = ChirpArticleSql.Command(connection, transaction, """
            INSERT INTO chirp_article_receipts VALUES($plan,$ordinal,$id,$json,$sha)
            ON CONFLICT(PlanSha256,CoverageOrdinal) DO NOTHING;
            """, ("$plan", current.Receipt.PlanSha256), ("$ordinal", current.Receipt.CoverageOrdinal),
            ("$id", current.AuthorizationId.ToString("N")), ("$json", json), ("$sha", hash)))
            await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        using (var verify = ChirpArticleSql.Command(connection, transaction,
            "SELECT ReceiptJson,ReceiptSha256,AuthorizationId FROM chirp_article_receipts WHERE PlanSha256=$plan AND CoverageOrdinal=$ordinal;",
            ("$plan", current.Receipt.PlanSha256), ("$ordinal", current.Receipt.CoverageOrdinal)))
        using (var row = await verify.ExecuteReaderAsync(token).ConfigureAwait(false))
            if (!await row.ReadAsync(token).ConfigureAwait(false) || !string.Equals(row.GetString(0), json, StringComparison.Ordinal)
                || !string.Equals(row.GetString(1), hash, StringComparison.Ordinal) || !string.Equals(row.GetString(2), current.AuthorizationId.ToString("N"), StringComparison.Ordinal))
                throw new InvalidOperationException("A different accepted receipt already exists; originals were retained.");
        token.ThrowIfCancellationRequested(); transaction.Commit();
    }

    public async Task<int> RecoverLocalOutputsAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _outputInFlight, 1, 0) != 0) throw new InvalidOperationException("A local article output operation is already running.");
        try
        {
            int completedOwned = 0;
            if (_pendingAcceptedMedia is { } owned)
            { await PersistOwnedMediaAsync(owned).ConfigureAwait(false); completedOwned = 1; }
            List<OutputJournal> pending = [];
            using (var context = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
            {
                var connection = (SqliteConnection)context.Database.GetDbConnection(); await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                using var transaction = connection.BeginTransaction(deferred: true);
                using var command = ChirpArticleSql.Command(connection, transaction, """
                    SELECT j.AuthorizationId FROM chirp_article_output_journals j
                    LEFT JOIN chirp_article_receipts r ON r.AuthorizationId=j.AuthorizationId WHERE r.AuthorizationId IS NULL LIMIT 10001;
                    """);
                List<string> ids = [];
                using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    { if (ids.Count >= 10_000) throw new InvalidDataException("Local recovery evidence exceeds its bound."); ids.Add(reader.GetString(0)); }
                foreach (string id in ids) pending.Add(await ReadJournalAsync(connection, transaction, id, cancellationToken).ConfigureAwait(false));
            }
            foreach (var journal in pending)
            {
                string file = journal.Receipt.Audio.AudioPath;
                if (!File.Exists(file) && File.Exists(file + ".pending"))
                {
                    await VerifyMediaAsync(file + ".pending", journal.Receipt.Audio, cancellationToken).ConfigureAwait(false);
                    ChirpPolicyFiles.RejectLinks(file); File.Move(file + ".pending", file, overwrite: false);
                }
                await FinishOutputJournalAsync(journal, cancellationToken).ConfigureAwait(false);
            }
            return pending.Count + completedOwned;
        }
        finally { Volatile.Write(ref _outputInFlight, 0); }
    }
}
