using CloudScribe.Application.Generation;
using Microsoft.Data.Sqlite;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class ChirpArticleService
{
    private static async Task<ChirpArticlePlanPart> OutputPartAsync(SqliteConnection connection, SqliteTransaction transaction,
        ChirpArticlePlanProposal proposal, Guid authorization, int ordinal, CancellationToken token)
    {
        var original = proposal.Parts.Single(x => x.CoverageOrdinal == ordinal && x.Range.ReusedReceiptSha256 is null);
        var child = await ReadRecoveryChildAsync(connection, transaction, authorization, token).ConfigureAwait(false);
        if (child is null) return original;
        using var command = ChirpArticleSql.Command(connection, transaction,
            "SELECT ReviewSha256,GrantSha256,RootAuthorizationId,ParentAuthorizationId FROM chirp_article_split_attempts WHERE AuthorizationId=$id;", ("$id", authorization.ToString("N")));
        using var row = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await row.ReadAsync(token).ConfigureAwait(false) || !string.Equals(row.GetString(0), ChirpPolicyValidation.Hash(child), StringComparison.Ordinal)
            || !string.Equals(row.GetString(1), child.RecoveryGrantSha256, StringComparison.Ordinal)
            || !string.Equals(row.GetString(2), child.RootAuthorizationId.ToString("N"), StringComparison.Ordinal)
            || !string.Equals(row.GetString(3), child.ParentAuthorizationId.ToString("N"), StringComparison.Ordinal)
            || !string.Equals(child.PlanSha256, proposal.PlanSha256, StringComparison.Ordinal) || child.CoverageOrdinal != ordinal
            || child.Depth < 1 || child.Depth >= original.Range.EndScalarExclusive - original.Range.StartScalar
            || child.Range.ReusedReceiptSha256 is not null || child.Range.StartScalar < original.Range.StartScalar
            || child.Range.EndScalarExclusive > original.Range.EndScalarExclusive || child.Range.EndScalarExclusive <= child.Range.StartScalar
            || child.Range.EndScalarExclusive - child.Range.StartScalar >= original.Range.EndScalarExclusive - original.Range.StartScalar)
            throw new InvalidOperationException("Accepted child output has no exact immutable owned range.");
        return new(ordinal, child.Range, child.PayloadSha256, child.PayloadBytes);
    }

    private static async Task FinishRecoveryReceiptAsync(SqliteConnection connection, SqliteTransaction transaction,
        OutputJournal journal, string json, string hash, CancellationToken token)
    {
        string id = journal.AuthorizationId.ToString("N");
        using (var insert = ChirpArticleSql.Command(connection, transaction, """
            INSERT INTO chirp_article_split_receipts VALUES($id,$json,$hash) ON CONFLICT(AuthorizationId) DO NOTHING;
            """, ("$id", id), ("$json", json), ("$hash", hash)))
            await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        using var verify = ChirpArticleSql.Command(connection, transaction,
            "SELECT ReceiptJson,ReceiptSha256 FROM chirp_article_split_receipts WHERE AuthorizationId=$id;", ("$id", id));
        using var row = await verify.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await row.ReadAsync(token).ConfigureAwait(false) || !string.Equals(row.GetString(0), json, StringComparison.Ordinal)
            || !string.Equals(row.GetString(1), hash, StringComparison.Ordinal))
            throw new InvalidOperationException("Another accepted child receipt already exists; no response was discarded.");
    }

    private async Task<ChirpSegmentAcceptedAudio?> ReadRecoveryReceiptAsync(SqliteConnection connection, SqliteTransaction transaction,
        ChirpArticleRecoveryChild child, CancellationToken token)
    {
        using var command = ChirpArticleSql.Command(connection, transaction,
            "SELECT ReceiptJson,ReceiptSha256 FROM chirp_article_split_receipts WHERE AuthorizationId=$id;", ("$id", child.AuthorizationId.ToString("N")));
        using var row = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await row.ReadAsync(token).ConfigureAwait(false)) return null;
        var receipt = ParseStored<ChirpSegmentAcceptedAudio>(row.GetString(0));
        if (!string.Equals(ChirpPolicyValidation.Hash(receipt), row.GetString(1), StringComparison.Ordinal))
            throw new InvalidOperationException("Accepted child receipt integrity changed.");
        await row.DisposeAsync().ConfigureAwait(false);
        var journal = await ReadJournalAsync(connection, transaction, child.AuthorizationId.ToString("N"), token).ConfigureAwait(false);
        if (journal.Receipt != receipt) throw new InvalidOperationException("Accepted child output differs from its durable publication journal.");
        await VerifyMediaAsync(receipt.Audio.AudioPath, receipt.Audio, token).ConfigureAwait(false);
        return receipt;
    }
}
