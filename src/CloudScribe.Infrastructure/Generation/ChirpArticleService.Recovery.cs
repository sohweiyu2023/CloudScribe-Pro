using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class ChirpArticleService
{
    public static string RecoveryKey(ChirpArticleRecoveryChild child)
    {
        ArgumentNullException.ThrowIfNull(child);
        return SuccessorKey(child.PlanSha256, child.CoverageOrdinal, child.AuthorizationId);
    }
    public async Task<IReadOnlyList<ChirpArticleRecoveryChild>> PrepareRecoveryChildrenAsync(Guid jobId,
        Guid parentAuthorizationId, CancellationToken token = default)
    {
        var policy = await PolicyAsync(token).ConfigureAwait(false);
        using var context = await factory.CreateDbContextAsync(token).ConfigureAwait(false);
        var connection = (SqliteConnection)context.Database.GetDbConnection(); await connection.OpenAsync(token).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        var job = await ReadWholeArticleJobAsync(connection, transaction, jobId, token).ConfigureAwait(false);
        RequireRecoveryJob(policy, job);
        var rows = await ChirpArticleSql.ReadAsync(connection, transaction, "chirp_article_owned_attempts", token).ConfigureAwait(false);
        var parent = rows.Single(x => Equals(x[0], parentAuthorizationId.ToString("N")));
        if (!Equals(parent[1], job.PlanSha256) || !Equals(parent[2], (long)job.ActiveOrdinal!.Value))
            throw new InvalidOperationException("The failed parent is not this executing article range.");
        _ = await ReadFinancialAsync(connection, transaction, policy, token).ConfigureAwait(false);
        return await DeriveRecoveryChildrenAsync(connection, transaction, policy, jobId, parent, token).ConfigureAwait(false);
    }

    public async Task<ChirpResolvedSegment> ResolveRecoveryChildAsync(ChirpArticleRecoveryChild child, CancellationToken token = default)
    {
        var policy = await PolicyAsync(token).ConfigureAwait(false);
        using var context = await factory.CreateDbContextAsync(token).ConfigureAwait(false);
        var connection = (SqliteConnection)context.Database.GetDbConnection(); await connection.OpenAsync(token).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        var job = await ReadWholeArticleJobAsync(connection, transaction, child.JobId, token).ConfigureAwait(false);
        RequireRecoveryJob(policy, job);
        RequireRecoveryChildJob(job, child);
        var financial = await ReadFinancialAsync(connection, transaction, policy, token).ConfigureAwait(false);
        var article = policy.Articles.Single(x => Equals(x.Proposal.PlanSha256, child.PlanSha256));
        var current = await _evidence.ResolveAsync(connection, transaction, policy, article.Proposal.DocumentId, financial.Old, token).ConfigureAwait(false);
        var part = await RecoveryPartAsync(connection, transaction, policy, child, token).ConfigureAwait(false);
        var owned = await ChirpArticleSql.ReadAsync(connection, transaction, "chirp_article_owned_attempts", token).ConfigureAwait(false);
        var parent = owned.Single(x => Equals(x[0], child.ParentAuthorizationId.ToString("N")));
        if (!(await DeriveRecoveryChildrenAsync(connection, transaction, policy, child.JobId, parent, token).ConfigureAwait(false)).Contains(child))
            throw new InvalidOperationException("The requested recovery child differs from its native deterministic proposal.");
        return new(current.Document, current.Article.Proposal, part, ChirpNarrationPlanner.Slice(current.Document.Text, part.Range));
    }

    public async Task<ChirpApprovedAttempt> ApproveRecoveryChildAsync(ChirpArticleRecoveryChild child,
        ChirpRequestIntent intent, CancellationToken token = default)
    {
        var policy = await PolicyAsync(token).ConfigureAwait(false);
        using var context = await factory.CreateDbContextAsync(token).ConfigureAwait(false);
        var connection = (SqliteConnection)context.Database.GetDbConnection(); await connection.OpenAsync(token).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var job = await ReadWholeArticleJobAsync(connection, transaction, child.JobId, token).ConfigureAwait(false);
        RequireRecoveryJob(policy, job);
        RequireRecoveryChildJob(job, child);
        if (intent.CoverageOrdinal != child.CoverageOrdinal || intent.DocumentId != job.DocumentId)
            throw new InvalidOperationException("The compiled child belongs to another article range.");
        var part = await RecoveryPartAsync(connection, transaction, policy, child, token).ConfigureAwait(false);
        _ = await CurrentAsync(connection, transaction, policy, intent, token, part).ConfigureAwait(false);
        var financial = await ReadFinancialAsync(connection, transaction, policy, token).ConfigureAwait(false);
        var existing = await ReadRecoveryChildAsync(connection, transaction, child.AuthorizationId, token).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing != child) throw new InvalidOperationException("This child identity already owns another range.");
            await RequireExistingAsync(connection, transaction, policy, intent, child.AuthorizationId, financial, token).ConfigureAwait(false);
            return new(child.AuthorizationId, SuccessorKey(child.PlanSha256, child.CoverageOrdinal, child.AuthorizationId), Math.Max(1, intent.EstimateMinorUnits));
        }
        await RequireRecoveryReadyAsync(connection, transaction, policy, financial, child.RootAuthorizationId, token).ConfigureAwait(false);
        RequireCapacity(policy, financial, Math.Max(1, intent.EstimateMinorUnits));
        await InsertRecoveryChildAsync(connection, transaction, policy, child, intent, financial, token).ConfigureAwait(false);
        var after = await ReadFinancialAsync(connection, transaction, policy, token).ConfigureAwait(false);
        using var finalize = ChirpArticleSql.Command(connection, transaction,
            "UPDATE chirp_article_split_attempts SET ApprovedFinancialSha256=$financial WHERE AuthorizationId=$id AND ApprovedFinancialSha256='';",
            ("$financial", after.Sha256), ("$id", child.AuthorizationId.ToString("N")));
        if (await finalize.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) throw new InvalidOperationException("The split reservation could not be finalized.");
        await RequirePolicyCurrentAsync(policy, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested(); transaction.Commit();
        return new(child.AuthorizationId, SuccessorKey(child.PlanSha256, child.CoverageOrdinal, child.AuthorizationId), Math.Max(1, intent.EstimateMinorUnits));
    }

    private void RequireRecoveryJob(ChirpArticleAdmissionPolicy policy, ChirpWholeArticleJob job)
    {
        if (policy.SentenceRecovery is null || job.MaximumRecoveryRequests < 2 || job.MaximumRecoveryMinorUnits < 2
            || job.State != ChirpWholeArticleState.Executing || job.ActiveOrdinal is null
            || !string.Equals(job.PolicySha256, ChirpPolicyValidation.Hash(policy), StringComparison.Ordinal)
            || job.CompletionBound != ChirpPolicyValidation.IsCompletionBound(policy)
            || clock.GetUtcNow() < job.LastObservedAtUtc || ChirpPolicyValidation.IsJobExpired(job, clock.GetUtcNow()) || job.ExpiresAtUtc > policy.ExpiresAtUtc)
            throw new InvalidOperationException("Bounded recovery requires current durable whole-article consent and its original deadline.");
    }

    private static void RequireRecoveryChildJob(ChirpWholeArticleJob job, ChirpArticleRecoveryChild child)
    {
        if (job.JobId != child.JobId || !string.Equals(job.PlanSha256, child.PlanSha256, StringComparison.Ordinal)
            || job.ActiveOrdinal != child.CoverageOrdinal)
            throw new InvalidOperationException("The child does not match the durable executing article range.");
    }

    private static async Task<IReadOnlyList<ChirpArticleRecoveryChild>> DeriveRecoveryChildrenAsync(SqliteConnection connection,
        SqliteTransaction transaction, ChirpArticleAdmissionPolicy policy, Guid jobId, object?[] parent, CancellationToken token)
    {
        var grant = policy.SentenceRecovery ?? throw new InvalidOperationException("No independently admitted automatic recovery allowance exists.");
        await RequireSentenceRejectionAsync(connection, transaction, policy, parent, token).ConfigureAwait(false);
        var article = policy.Articles.Single(x => Equals(x.Proposal.PlanSha256, parent[1]));
        int ordinal = checked((int)(long)parent[2]!);
        var original = article.Proposal.Parts.Single(x => x.CoverageOrdinal == ordinal && x.Range.ReusedReceiptSha256 is null);
        var id = Guid.ParseExact((string)parent[0]!, "N");
        var prior = await ReadRecoveryChildAsync(connection, transaction, id, token).ConfigureAwait(false);
        var range = prior?.Range ?? original.Range;
        if (prior is not null && prior.JobId != jobId) throw new InvalidOperationException("A child cannot move to another article job.");
        var document = await ChirpArticleEvidenceResolver.ReadDocumentAsync(connection, transaction, article.Proposal.DocumentId, token).ConfigureAwait(false);
        var envelope = ParseStored<GoogleGenerationSubmissionEnvelope>((string)parent[9]!);
        var snapshot = await RecoveryFinancialSnapshot.ReadAsync(connection, transaction, token).ConfigureAwait(false);
        var queue = snapshot.Queue.Single(x => Equals(x[0], parent[5]) && Equals(x[1], GoogleGenerationProvider.SynthesizeOperationStableId) && Equals(x[2], parent[7]));
        Guid root = prior?.RootAuthorizationId ?? id;
        var evidence = new ChirpSentenceSplitEvidence(id, root, envelope.CompiledPayloadSha256, envelope.CompiledPayloadBytes,
            ChirpPolicyValidation.Hash(parent), ChirpPolicyValidation.Hash(queue), 400, GoogleErrorStatus.InvalidArgument,
            GoogleValidationMessageHint.SentenceLengthReported, true, true, false, false, false);
        var shape = ChirpBoundedSentenceSplit.Compile(document.Text, range, new(article.Proposal.LanguageCode, article.Proposal.VoiceName, "MP3", 5000), evidence,
            new(1, grant.GrantId, root, article.Proposal.SourceSha256, policy.SourceApprovalSha256, grant.MaximumDepth,
                grant.MaximumChildAttemptsPerRange, grant.MaximumAdditionalMinorUnits), new(prior?.Depth ?? 0, 0, 0), _ => 1);
        var proposed = shape.Parts.Select((part, index) => new ChirpArticleRecoveryChild(RecoveryChildId(grant.GrantId, jobId, id, index + 1),
            jobId, root, id, article.Proposal.PlanSha256, ordinal, index + 1, shape.ChildDepth,
            part.Range, part.Compilation.PayloadSha256, part.Compilation.Payload.Length, ChirpPolicyValidation.Hash(grant))).ToArray();
        for (int index = 0; index < proposed.Length; index++)
        {
            var retained = await ReadRecoveryChildAsync(connection, transaction, proposed[index].AuthorizationId, token).ConfigureAwait(false);
            if (retained is null) continue;
            if (!ChirpPolicyValidation.IsReviewedRecoveryGrant(policy, retained.RecoveryGrantSha256)
                || (retained with { RecoveryGrantSha256 = proposed[index].RecoveryGrantSha256 }) != proposed[index])
                throw new InvalidOperationException("An existing child mapping cannot be replaced or rebound by a limit revision.");
            proposed[index] = retained;
        }
        return proposed;
    }

    private static Guid RecoveryChildId(Guid grant, Guid job, Guid parent, int slot)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(ChirpPolicyValidation.Hash(new { grant, job, parent, slot })));
        return new Guid(hash.AsSpan(0, 16));
    }
}
