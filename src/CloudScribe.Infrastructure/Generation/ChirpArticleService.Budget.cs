using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class ChirpArticleService
{
    private static async Task<CurrentFinancial> ReadFinancialAsync(SqliteConnection connection, SqliteTransaction transaction,
        ChirpArticleAdmissionPolicy policy, CancellationToken token)
    {
        var old = await RecoveryFinancialSnapshot.ReadAsync(connection, transaction, token).ConfigureAwait(false);
        var attempts = await ChirpArticleSql.ReadAsync(connection, transaction, "chirp_article_attempts", token).ConfigureAwait(false);
        var queueExtra = ChirpArticleSql.Extra(old.Queue, policy.Budget.QueueRows, 3);
        ChirpArticleSql.Exact(old.Spend, policy.Budget.SpendRows, 1);
        ChirpArticleSql.Exact(old.Recovery, policy.Budget.RecoveryRows, 1);
        ChirpArticleSql.Exact(old.Scopes, policy.ScopeRows, 4);
        ChirpArticleSql.Exact(old.Transitions, policy.Budget.TransitionRows!, 1);
        var extra = ChirpArticleSql.Extra(attempts, policy.PriorArticleAttemptRows, 1);
        long additional = 0;
        HashSet<string> keys = new(StringComparer.Ordinal);
        foreach (var row in extra)
        {
            ValidateAttempt(policy, row, old.QueueStates);
            if (!keys.Add((string)row[7]!)) throw new InvalidOperationException("Article reservations have duplicate identities.");
            additional = checked(additional + (long)row[13]!);
        }
        if (extra.Length > policy.MaximumNewAttempts || additional > policy.MaximumAdditionalReservationMinorUnits
            || !queueExtra.Select(x => (string)x[2]!).ToHashSet(StringComparer.Ordinal).SetEquals(keys)
            || queueExtra.Any(x => !Equals(x[0], policy.AccountId) || !Equals(x[1], GoogleGenerationProvider.SynthesizeOperationStableId)))
            throw new InvalidOperationException("Unreviewed queue writes or article grant limits prevent dispatch.");
        long committed = checked(policy.Budget.ProjectCommittedMinorUnits + additional);
        long remaining = checked(policy.Budget.AllocationCeilingMinorUnits - policy.Budget.AllocationCommittedMinorUnits - additional);
        if (committed > policy.Budget.ProjectCeilingMinorUnits || remaining < 0)
            throw new InvalidOperationException("Retained project or article allocation ceiling would be exceeded.");
        string digest = FinancialDigest(old, attempts);
        return new(old, attempts, extra, digest, committed, remaining, additional);
    }

    private static void ValidateAttempt(ChirpArticleAdmissionPolicy policy, object?[] row,
        IReadOnlyList<RecoveryFinancialSnapshot.QueueState> queues)
    {
        if (row.Length != 18 || row[0] is not string id || !Guid.TryParseExact(id, "N", out var authorization)
            || !Equals(row[3], ChirpPolicyValidation.Hash(policy)) || !Equals(row[4], policy.SourceApprovalSha256)
            || !Equals(row[5], policy.AccountId) || !Equals(row[6], policy.ProjectId) || !Equals(row[10], policy.Currency)
            || !Equals(row[11], (long)policy.Scale) || row[12] is not long estimate || row[13] is not long reserved
            || estimate < 0 || reserved < estimate || row[16] is not long consumed || consumed is < 0 or > 1
            || row[9] is not string json || json.Length > 16384)
            throw new InvalidOperationException("Article attempt metadata differs from independent admission.");
        var article = policy.Articles.SingleOrDefault(x => Equals(x.Proposal.PlanSha256, row[1]))
            ?? throw new InvalidOperationException("An unadmitted article attempt appeared.");
        int ordinal = checked((int)(long)row[2]!);
        var allowance = article.AllowedSegments.SingleOrDefault(x => x.CoverageOrdinal == ordinal);
        var part = article.Proposal.Parts.SingleOrDefault(x => x.CoverageOrdinal == ordinal);
        var envelope = ChirpPolicyFiles.Parse<GoogleGenerationSubmissionEnvelope>(System.Text.Encoding.UTF8.GetBytes(json));
        if (allowance is null || part is null || part.Range.ReusedReceiptSha256 is not null || reserved != allowance.MaximumMinorUnits
            || !Equals(row[7], Key(article.Proposal.PlanSha256, ordinal)) || envelope.ChirpAuthorizationId != authorization
            || envelope.RecoveryAuthorizationId is not null || !Equals(envelope.AccountId, policy.AccountId)
            || !Equals(envelope.VoiceName, article.Proposal.VoiceName) || !Equals(envelope.AudioEncoding, "MP3")
            || !Equals(envelope.CompiledPayloadSha256, part.PayloadSha256) || envelope.CompiledPayloadBytes != part.PayloadBytes)
            throw new InvalidOperationException("Article attempt payload, reservation or exact segment identity changed.");
        var q = queues.SingleOrDefault(x => Equals(x.Account, policy.AccountId) && Equals(x.Operation, GoogleGenerationProvider.SynthesizeOperationStableId) && Equals(x.Key, row[7]));
        if (q is null || consumed == 0 && (q.Attempted != 0 || q.Unresolved != 0 || q.LocalAttempt is not null || q.ProviderId is not null)
            || consumed == 1 && (q.Attempted != 1 || !Equals(q.LocalAttempt, row[17]) || !Equals(q.PayloadSha256, part.PayloadSha256) || q.PayloadBytes != part.PayloadBytes))
            throw new InvalidOperationException("The article attempt no longer owns its durable queue barrier.");
    }

    private static string FinancialDigest(RecoveryFinancialSnapshot old, IReadOnlyList<object?[]> attempts) =>
        ChirpPolicyValidation.Hash(new
        {
            old.Queue,
            old.Spend,
            old.Recovery,
            old.Scopes,
            old.Transitions,
            ArticleAttempts = attempts.Select(x => x.Select((value, i) => i == 14 ? null : value).ToArray()).ToArray()
        });

    private sealed record CurrentFinancial(RecoveryFinancialSnapshot Old, IReadOnlyList<object?[]> Attempts, object?[][] Extra,
        string Sha256, long ProjectCommitted, long AllocationRemaining, long Additional);
}
