using System.Text;
using CloudScribe.Domain.Generation;
using Microsoft.Data.Sqlite;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class ChirpArticleService
{
    private static async Task ValidateSplitAttemptAsync(SqliteConnection connection, SqliteTransaction transaction,
        ChirpArticleAdmissionPolicy policy, object?[] row, IReadOnlyList<RecoveryFinancialSnapshot.QueueState> queues, CancellationToken token)
    {
        if (row.Length != 23 || policy.SentenceRecovery is not { } grant || row[0] is not string id
            || !Guid.TryParseExact(id, "N", out var authorization) || !Equals(row[3], ChirpPolicyValidation.Hash(policy))
            || !Equals(row[4], policy.SourceApprovalSha256) || !Equals(row[5], policy.AccountId) || !Equals(row[6], policy.ProjectId)
            || !Equals(row[10], policy.Currency) || !Equals(row[11], (long)policy.Scale) || row[12] is not long estimate
            || row[13] is not long reserved || estimate < 0 || reserved != Math.Max(1, estimate)
            || !Equals(row[21], ChirpPolicyValidation.Hash(grant)) || !Equals(row[22], grant.GrantId.ToString("N")))
            throw new InvalidOperationException("A split reservation differs from its separately admitted cumulative allowance.");
        var child = await ReadRecoveryChildAsync(connection, transaction, authorization, token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("A split reservation lacks its exact native mapping.");
        var part = await RecoveryPartAsync(connection, transaction, policy, child, token).ConfigureAwait(false);
        var envelope = ParseStored<GoogleGenerationSubmissionEnvelope>((string)row[9]!);
        if (!Equals(row[1], child.PlanSha256) || !Equals(row[2], (long)child.CoverageOrdinal)
            || !Equals(row[8], ChirpPolicyValidation.Hash(child)) || !Equals(row[18], child.RootAuthorizationId.ToString("N"))
            || !Equals(row[19], child.ParentAuthorizationId.ToString("N"))
            || !Equals(row[7], SuccessorKey(child.PlanSha256, child.CoverageOrdinal, authorization))
            || envelope.ChirpAuthorizationId != authorization || envelope.RecoveryAuthorizationId is not null
            || !Equals(envelope.AccountId, policy.AccountId) || !Equals(envelope.AudioEncoding, "MP3")
            || !Equals(envelope.CompiledPayloadSha256, part.PayloadSha256) || envelope.CompiledPayloadBytes != part.PayloadBytes
            || !Equals(envelope.VoiceName, policy.Articles.Single(x => Equals(x.Proposal.PlanSha256, child.PlanSha256)).Proposal.VoiceName))
            throw new InvalidOperationException("Split wire, range, parent or envelope identity changed.");
        var q = queues.SingleOrDefault(x => Equals(x.Account, policy.AccountId) && Equals(x.Operation, GoogleGenerationProvider.SynthesizeOperationStableId) && Equals(x.Key, row[7]));
        if (row[16] is not long consumed || consumed is < 0 or > 1 || q is null
            || consumed == 0 && (q.Attempted != 0 || q.Unresolved != 0 || q.LocalAttempt is not null || q.ProviderId is not null)
            || consumed == 1 && (q.Attempted != 1 || !Equals(q.LocalAttempt, row[17]) || !Equals(q.PayloadSha256, part.PayloadSha256) || q.PayloadBytes != part.PayloadBytes))
            throw new InvalidOperationException("The split request no longer owns its durable queue barrier.");
        await RequireChildParentBindingAsync(connection, transaction, policy, child, row, token).ConfigureAwait(false);
    }

    private static async Task<object?[][]> RequireSplitLimitsAsync(SqliteConnection connection, SqliteTransaction transaction,
        ChirpArticleAdmissionPolicy policy, IReadOnlyList<object?[]> all, CancellationToken token)
    {
        if (policy.SentenceRecovery is not { } grant) return [];
        List<object?[]> current = [];
        Dictionary<Guid, (ChirpWholeArticleJob Job, int Count, long Reserved)> jobs = [];
        Dictionary<Guid, int> documents = [];
        foreach (var row in all.Where(x => Equals(x[5], policy.AccountId) && Equals(x[6], policy.ProjectId)))
        {
            if (Equals(row[22], grant.GrantId.ToString("N")) && !ChirpPolicyValidation.IsReviewedRecoveryGrant(policy, (string)row[21]!))
                throw new InvalidOperationException("A recovery grant identity cannot be rebound to new limits.");
            var child = await ReadRecoveryChildAsync(connection, transaction, Guid.ParseExact((string)row[0]!, "N"), token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("A child reservation has no native job binding.");
            var job = await ReadWholeArticleJobAsync(connection, transaction, child.JobId, token).ConfigureAwait(false);
            if (!Equals(row[8], ChirpPolicyValidation.Hash(child)) || !Equals(row[1], job.PlanSha256)
                || !Equals(row[3], job.PolicySha256) && !await HasWholeJobPolicyHistoryAsync(connection, transaction, job, (string)row[3]!, token).ConfigureAwait(false)
                || !Equals(row[18], child.RootAuthorizationId.ToString("N")) || !Equals(row[19], child.ParentAuthorizationId.ToString("N")))
                throw new InvalidOperationException("Recovery history cannot be reassigned to reset its cumulative counters.");
            if (job.ExpiresAtUtc != policy.ExpiresAtUtc) continue;
            current.Add(row);
            var prior = jobs.GetValueOrDefault(job.JobId);
            jobs[job.JobId] = (job, prior.Count + 1, checked(prior.Reserved + (long)row[13]!));
            documents[job.DocumentId] = documents.GetValueOrDefault(job.DocumentId) + 1;
        }
        RequireRecoveryScopeLimits(grant, current, jobs.Values, documents.Values);
        return current.ToArray();
    }

    private static void RequireRecoveryScopeLimits(ChirpWholeArticleRecoveryGrant grant, List<object?[]> current,
        IEnumerable<(ChirpWholeArticleJob Job, int Count, long Reserved)> jobs, IEnumerable<int> documents)
    {
        if (current.Count > grant.MaximumChildAttemptsTotal || current.Sum(x => (long)x[13]!) > grant.MaximumAdditionalMinorUnits
            || current.GroupBy(x => x[18]).Any(x => x.Count() > grant.MaximumChildAttemptsPerRange))
            throw new InvalidOperationException("The cumulative recovery count or retained cost allowance is exhausted.");
        if (documents.Any(x => x > grant.MaximumChildAttemptsPerJob))
            throw new InvalidOperationException("The admitted per-job recovery allowance is exhausted.");
        foreach (var item in jobs)
            if (item.Count > item.Job.MaximumRecoveryRequests || item.Reserved > item.Job.MaximumRecoveryMinorUnits)
                throw new InvalidOperationException("Recovery exceeds the exact total displayed and confirmed for this job.");
    }

    private static async Task RequireChildParentBindingAsync(SqliteConnection connection, SqliteTransaction transaction,
        ChirpArticleAdmissionPolicy policy, ChirpArticleRecoveryChild child, object?[] row, CancellationToken token)
    {
        var owned = await ChirpArticleSql.ReadAsync(connection, transaction, "chirp_article_owned_attempts", token).ConfigureAwait(false);
        var parent = owned.Single(x => Equals(x[0], child.ParentAuthorizationId.ToString("N")));
        await RequireSentenceRejectionAsync(connection, transaction, policy, parent, token).ConfigureAwait(false);
        var snapshot = await RecoveryFinancialSnapshot.ReadAsync(connection, transaction, token).ConfigureAwait(false);
        var queue = snapshot.Queue.Single(x => Equals(x[0], parent[5]) && Equals(x[2], parent[7]) && Equals(x[1], GoogleGenerationProvider.SynthesizeOperationStableId));
        if (!Equals(row[20], ChirpPolicyValidation.Hash(queue)))
            throw new InvalidOperationException("A child changed its authenticated terminal parent queue.");
        var children = await DeriveRecoveryChildrenAsync(connection, transaction, policy, child.JobId, parent, token).ConfigureAwait(false);
        if (children.Count(x => x == child) != 1) throw new InvalidOperationException("The split is not the exact pre-admitted natural binary child.");
    }
}
