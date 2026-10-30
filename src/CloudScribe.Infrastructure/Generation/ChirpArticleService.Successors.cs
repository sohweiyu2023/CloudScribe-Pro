using System.Text.Json;
using CloudScribe.Domain.Generation;
using Microsoft.Data.Sqlite;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class ChirpArticleService
{
    internal static string SuccessorKey(string plan, int ordinal, Guid authorization) => Key(plan, ordinal) + ":" + authorization.ToString("N");

    private static async Task RequireSuccessorParentAsync(SqliteConnection connection, SqliteTransaction transaction,
        ChirpArticleAdmissionPolicy policy, CurrentFinancial financial, CancellationToken token)
    {
        if (policy.SuccessorGrant is not { } grant) return;
        var parent = financial.Attempts.SingleOrDefault(x => Equals(x[0], grant.ParentAuthorizationId.ToString("N")))
            ?? throw new InvalidOperationException("The independently admitted failed parent is missing.");
        var root = financial.RootAttempts.SingleOrDefault(x => Equals(x[0], grant.RootAuthorizationId.ToString("N")))
            ?? throw new InvalidOperationException("The immutable original article attempt is missing.");
        var queue = financial.Old.Queue.SingleOrDefault(x => Equals(x[0], policy.AccountId)
            && Equals(x[1], GoogleGenerationProvider.SynthesizeOperationStableId) && Equals(x[2], grant.ParentKey));
        var state = financial.Old.QueueStates.SingleOrDefault(x => string.Equals(x.Account, policy.AccountId, StringComparison.Ordinal)
            && string.Equals(x.Operation, GoogleGenerationProvider.SynthesizeOperationStableId, StringComparison.Ordinal)
            && string.Equals(x.Key, grant.ParentKey, StringComparison.Ordinal));
        var article = policy.Articles.Single();
        if (financial.SuccessorAttempts.Any(x => Equals(x[4], policy.SourceApprovalSha256)
            && Equals(x[18], grant.RootAuthorizationId.ToString("N"))
            && (!Equals(x[19], grant.ParentAuthorizationId.ToString("N")) || !Equals(x[21], ChirpPolicyValidation.Hash(grant)))))
            throw new InvalidOperationException("This one-attempt source approval already owns a successor; it cannot renew descendants or rotate grants.");
        if (financial.SuccessorAttempts.Any(x => Equals(x[22], grant.GrantId.ToString("N"))
            && (!Equals(x[21], ChirpPolicyValidation.Hash(grant)) || !Equals(x[4], policy.SourceApprovalSha256))))
            throw new InvalidOperationException("A durable grant identity cannot be reused with another approval or parent.");
        var part = article.Proposal.Parts.Single(x => x.CoverageOrdinal == grant.CoverageOrdinal);
        if (!string.Equals(RecoveryFinancialSnapshot.Hash(parent), grant.ParentAttemptRowSha256, StringComparison.Ordinal)
            || queue is null || queue.Length != 12 || !string.Equals(RecoveryFinancialSnapshot.Hash(queue), grant.ParentQueueRowSha256, StringComparison.Ordinal)
            || !Equals(parent[1], grant.PlanSha256) || !Equals(parent[2], (long)grant.CoverageOrdinal)
            || !Equals(root[1], grant.PlanSha256) || !Equals(root[2], (long)grant.CoverageOrdinal)
            || !Equals(parent[5], policy.AccountId) || !Equals(parent[6], policy.ProjectId) || !Equals(parent[7], grant.ParentKey)
            || !Equals(parent[16], 1L) || !Equals(parent[17], grant.ParentLocalAttemptId)
            || state is null || state.Unresolved != 0 || state.ProviderId is not null || state.Attempted != 1
            || !string.Equals(state.LocalAttempt, grant.ParentLocalAttemptId, StringComparison.Ordinal)
            || !string.Equals(state.DiagnosticCode, "google-http-400", StringComparison.Ordinal)
            || !string.Equals(state.PayloadSha256, part.PayloadSha256, StringComparison.Ordinal) || state.PayloadBytes != part.PayloadBytes
            || !await HasConfirmedInvalidArgumentAsync(connection, transaction, policy.AccountId, grant.ParentKey, token).ConfigureAwait(false))
            throw new InvalidOperationException("Only the exact consumed, resolved HTTP400 invalid-argument parent can have this diagnostic successor.");
        if (grant.ParentAuthorizationId != grant.RootAuthorizationId
            && !financial.SuccessorAttempts.Any(x => Equals(x[0], parent[0]) && Equals(x[18], root[0])))
            throw new InvalidOperationException("The admitted parent is not in the retained original attempt lineage.");
        var envelope = ChirpPolicyFiles.Parse<GoogleGenerationSubmissionEnvelope>(System.Text.Encoding.UTF8.GetBytes((string)parent[9]!));
        if (envelope.ChirpAuthorizationId != grant.ParentAuthorizationId || envelope.RecoveryAuthorizationId is not null
            || !string.Equals(envelope.CompiledPayloadSha256, part.PayloadSha256, StringComparison.Ordinal)
            || envelope.CompiledPayloadBytes != part.PayloadBytes || !string.Equals(envelope.VoiceName, article.Proposal.VoiceName, StringComparison.Ordinal)
            || !string.Equals(envelope.AccountId, policy.AccountId, StringComparison.Ordinal))
            throw new InvalidOperationException("The successor cannot change the failed payload, voice or account.");
        using var accepted = ChirpArticleSql.Command(connection, transaction, """
            SELECT (SELECT COUNT(*) FROM chirp_article_receipts WHERE PlanSha256=$plan AND CoverageOrdinal=$ordinal)
                + (SELECT COUNT(*) FROM chirp_article_output_journals j JOIN chirp_article_owned_attempts a
                   ON a.AuthorizationId=j.AuthorizationId WHERE a.PlanSha256=$plan AND a.CoverageOrdinal=$ordinal);
            """, ("$plan", grant.PlanSha256), ("$ordinal", grant.CoverageOrdinal));
        if (Convert.ToInt64(await accepted.ExecuteScalarAsync(token).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) != 0)
            throw new InvalidOperationException("Accepted coverage or a pending accepted-output journal must be reused, never regenerated.");
    }

    private static async Task<bool> HasConfirmedInvalidArgumentAsync(SqliteConnection connection, SqliteTransaction transaction,
        string account, string key, CancellationToken token)
    {
        using var command = ChirpArticleSql.Command(connection, transaction,
            "SELECT ErrorStatus,OutcomeObservedAtUnixMilliseconds FROM google_generation_queue_states WHERE AccountId=$account AND OperationStableId=$operation AND IdempotencyKey=$key;",
            ("$account", account), ("$operation", GoogleGenerationProvider.SynthesizeOperationStableId), ("$key", key));
        using var row = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        return await row.ReadAsync(token).ConfigureAwait(false) && row.GetInt64(0) == (long)GoogleErrorStatus.InvalidArgument
            && !row.IsDBNull(1) && row.GetInt64(1) > 0;
    }

    private static void ValidateSuccessorRow(ChirpArticleAdmissionPolicy policy, object?[] row)
    {
        var grant = policy.SuccessorGrant ?? throw new InvalidOperationException("No independently admitted successor grant exists.");
        if (row.Length != 23 || !Equals(row[18], grant.RootAuthorizationId.ToString("N"))
            || !Equals(row[19], grant.ParentAuthorizationId.ToString("N")) || !Equals(row[20], grant.ParentQueueRowSha256)
            || !Equals(row[21], ChirpPolicyValidation.Hash(grant)) || !Equals(row[1], grant.PlanSha256)
            || !Equals(row[2], (long)grant.CoverageOrdinal) || !Equals(row[13], 1L) || !Equals(row[22], grant.GrantId.ToString("N")))
            throw new InvalidOperationException("The appended successor differs from its exact one-attempt admission.");
    }

    private static async Task<ChirpApprovedAttempt?> FindSuccessorAsync(SqliteConnection connection, SqliteTransaction transaction,
        ChirpArticleSuccessorGrant grant, string plan, int ordinal, CancellationToken token)
    {
        if (!string.Equals(grant.PlanSha256, plan, StringComparison.Ordinal) || grant.CoverageOrdinal != ordinal)
            throw new InvalidOperationException("This diagnostic grant cannot authorize another coverage range.");
        using var command = ChirpArticleSql.Command(connection, transaction,
            "SELECT AuthorizationId,ChildKey,ReservedMinorUnits,GrantSha256 FROM chirp_article_successor_attempts WHERE ParentAuthorizationId=$parent;",
            ("$parent", grant.ParentAuthorizationId.ToString("N")));
        using var row = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await row.ReadAsync(token).ConfigureAwait(false)) return null;
        if (!string.Equals(row.GetString(3), ChirpPolicyValidation.Hash(grant), StringComparison.Ordinal))
            throw new InvalidOperationException("The parent already has another successor. Inspect the retained latest attempt; never create a sibling.");
        return new(Guid.ParseExact(row.GetString(0), "N"), row.GetString(1), row.GetInt64(2));
    }

    private static async Task<string> AttemptTableAsync(SqliteConnection connection, SqliteTransaction transaction, string id, CancellationToken token)
    {
        using var command = ChirpArticleSql.Command(connection, transaction,
            "SELECT COUNT(*) FROM chirp_article_successor_attempts WHERE AuthorizationId=$id;", ("$id", id));
        return Convert.ToInt64(await command.ExecuteScalarAsync(token).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) == 1
            ? "chirp_article_successor_attempts" : "chirp_article_attempts";
    }
}
