using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class ChirpArticleService
{
    public async Task<ChirpApprovedAttempt> ApproveAsync(ChirpApprovalConfirmation confirmation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(confirmation);
        if (!confirmation.ConfirmedByUser || !confirmation.AcknowledgedHistoricalHolds
            || !_pending.TryRemove(confirmation.PreviewId, out var pending) || clock.GetUtcNow() - pending.CreatedAt > TimeSpan.FromMinutes(10)
            || !string.Equals(confirmation.DisplayedReviewSha256, pending.Review.ReviewSha256, StringComparison.Ordinal)
            || confirmation.MaximumMinorUnits != pending.Review.MaximumMinorUnits)
            throw new InvalidOperationException("A fresh exact article review and explicit retained-hold acknowledgement are required.");
        var policy = await PolicyAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(ChirpPolicyValidation.Hash(policy), pending.PolicySha256, StringComparison.Ordinal))
            throw new InvalidOperationException("Independent article admission changed; prepare a fresh review.");
        using var context = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var connection = (SqliteConnection)context.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var current = await CurrentAsync(connection, transaction, policy, pending.Intent, cancellationToken).ConfigureAwait(false);
        var financial = await ReadFinancialAsync(connection, transaction, policy, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(financial.Sha256, pending.FinancialSha256, StringComparison.Ordinal))
            throw new InvalidOperationException("Financial evidence changed after review; all reservations remain retained.");
        var existing = await FindExistingAsync(connection, transaction, current.Article.Proposal.PlanSha256, pending.Intent.CoverageOrdinal, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            await RequireExistingAsync(connection, transaction, policy, pending.Intent, existing.AuthorizationId, financial, cancellationToken).ConfigureAwait(false);
            await RequirePolicyCurrentAsync(policy, cancellationToken).ConfigureAwait(false);
            return existing;
        }
        if (pending.Review.ExistingAuthorizationId is not null) throw new InvalidOperationException("The previously reserved segment disappeared.");
        await RequireReadyAsync(connection, transaction, financial, null, cancellationToken).ConfigureAwait(false);
        RequireCapacity(policy, financial, confirmation.MaximumMinorUnits);
        await RequirePolicyCurrentAsync(policy, cancellationToken).ConfigureAwait(false);
        await AppendPlanAsync(connection, transaction, policy, current.Article, cancellationToken).ConfigureAwait(false);
        var approved = new ChirpApprovedAttempt(Guid.NewGuid(), Key(current.Article.Proposal.PlanSha256, pending.Intent.CoverageOrdinal), confirmation.MaximumMinorUnits);
        await AppendAttemptAsync(connection, transaction, policy, pending, approved, cancellationToken).ConfigureAwait(false);
        var after = await ReadFinancialAsync(connection, transaction, policy, cancellationToken).ConfigureAwait(false);
        using (var update = ChirpArticleSql.Command(connection, transaction,
            "UPDATE chirp_article_attempts SET ApprovedFinancialSha256=$sha WHERE AuthorizationId=$id AND ApprovedFinancialSha256='';",
            ("$sha", after.Sha256), ("$id", approved.AuthorizationId.ToString("N"))))
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1) throw new InvalidOperationException("Article reservation audit could not be finalized.");
        await RequirePolicyCurrentAsync(policy, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested(); transaction.Commit();
        return approved;
    }

    private static void RequireCapacity(ChirpArticleAdmissionPolicy policy, CurrentFinancial financial, long maximum)
    {
        if (financial.Extra.Length >= policy.MaximumNewAttempts || maximum < 1
            || maximum > policy.MaximumAdditionalReservationMinorUnits - financial.Additional
            || maximum > policy.Budget.ProjectCeilingMinorUnits - financial.ProjectCommitted || maximum > financial.AllocationRemaining)
            throw new InvalidOperationException("The explicit article grant or retained project/allocation budget cannot reserve another segment.");
    }

    private async Task RequireReadyAsync(SqliteConnection connection, SqliteTransaction transaction, CurrentFinancial financial,
        Guid? resuming, CancellationToken token)
    {
        if (financial.Attempts.Any(x => Equals(x[16], 0L) && !Equals(x[0], resuming?.ToString("N"))))
            throw new InvalidOperationException("Resume or reconcile the existing reserved segment before another article approval.");
        foreach (var row in financial.Extra.Where(x => Equals(x[16], 1L)))
        {
            var q = financial.Old.QueueStates.Single(x => Equals(x.Account, row[5]) && Equals(x.Operation, GoogleGenerationProvider.SynthesizeOperationStableId) && Equals(x.Key, row[7]));
            if (q.Unresolved != 0 || !string.Equals(q.DiagnosticCode, "google-accepted", StringComparison.Ordinal))
                throw new InvalidOperationException("An article request failed or is uncertain. Stop; this grant cannot be used for another request.");
            using var receipt = ChirpArticleSql.Command(connection, transaction,
                "SELECT ReceiptJson,ReceiptSha256 FROM chirp_article_receipts WHERE AuthorizationId=$id;", ("$id", row[0]));
            using (var reader = await receipt.ExecuteReaderAsync(token).ConfigureAwait(false))
                if (!await reader.ReadAsync(token).ConfigureAwait(false) || reader.GetString(0).Length > 32768
                    || !string.Equals(ChirpPolicyValidation.Hash(ChirpPolicyFiles.Parse<ChirpSegmentAcceptedAudio>(System.Text.Encoding.UTF8.GetBytes(reader.GetString(0)))), reader.GetString(1), StringComparison.Ordinal))
                    throw new InvalidOperationException("Accepted audio provenance is incomplete. Recover its local output before approving another segment; never regenerate it.");
            var journal = await ReadJournalAsync(connection, transaction, (string)row[0]!, token).ConfigureAwait(false);
            await VerifyMediaAsync(journal.Receipt.Audio.AudioPath, journal.Receipt.Audio, token).ConfigureAwait(false);
        }
    }

    private static async Task<ChirpApprovedAttempt?> FindExistingAsync(SqliteConnection connection, SqliteTransaction transaction,
        string plan, int ordinal, CancellationToken token)
    {
        using var command = ChirpArticleSql.Command(connection, transaction,
            "SELECT AuthorizationId,ChildKey,ReservedMinorUnits FROM chirp_article_attempts WHERE PlanSha256=$plan AND CoverageOrdinal=$ordinal;",
            ("$plan", plan), ("$ordinal", ordinal));
        using var row = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        return await row.ReadAsync(token).ConfigureAwait(false) ? new(Guid.ParseExact(row.GetString(0), "N"), row.GetString(1), row.GetInt64(2)) : null;
    }

    private static async Task RequireExistingAsync(SqliteConnection connection, SqliteTransaction transaction, ChirpArticleAdmissionPolicy policy,
        ChirpRequestIntent intent, Guid id, CurrentFinancial financial, CancellationToken token)
    {
        using var command = ChirpArticleSql.Command(connection, transaction,
            "SELECT PolicySha256,EnvelopeJson,Consumed,ApprovedFinancialSha256 FROM chirp_article_attempts WHERE AuthorizationId=$id;", ("$id", id.ToString("N")));
        using var row = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await row.ReadAsync(token).ConfigureAwait(false) || row.GetInt64(2) != 0
            || !string.Equals(row.GetString(0), ChirpPolicyValidation.Hash(policy), StringComparison.Ordinal)
            || !string.Equals(row.GetString(1), JsonSerializer.Serialize(intent.Envelope with { ChirpAuthorizationId = id }), StringComparison.Ordinal)
            || !string.Equals(row.GetString(3), financial.Sha256, StringComparison.Ordinal))
            throw new InvalidOperationException("The segment was already dispatched, its exact reservation changed or admission expired. No replay is allowed.");
    }
}
