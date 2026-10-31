using System.Text.Json;
using CloudScribe.Application.Generation;
using CloudScribe.Application.Logging;
using CloudScribe.Domain.Generation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class ChirpArticleService
{
    public async Task<GoogleGenerationSpendAuthorization?> LoadSpendAsync(GoogleGenerationSubmissionEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        if (envelope.ChirpAuthorizationId is null || envelope.RecoveryAuthorizationId is not null) return null;
        if (await ResumePolicyAsync(envelope.ChirpAuthorizationId, cancellationToken).ConfigureAwait(false) is { } resume)
            return await LoadResumeSpendAsync(resume, envelope, cancellationToken).ConfigureAwait(false);
        var policy = await PolicyAsync(cancellationToken).ConfigureAwait(false);
        using var context = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var connection = (SqliteConnection)context.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        var result = await LoadAuthorizedAsync(connection, transaction, policy, envelope, cancellationToken).ConfigureAwait(false);
        await RequirePolicyCurrentAsync(policy, cancellationToken).ConfigureAwait(false);
        return result?.Spend;
    }

    public async Task<bool> TryClaimAuthorizedAsync(GoogleGenerationSubmissionEnvelope envelope, string projectId,
        string currency, int scale, long estimate, GoogleGenerationPersistedQueueState claim, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        claim.Validate();
        if (envelope.ChirpAuthorizationId is not { } id || envelope.RecoveryAuthorizationId is not null
            || !claim.SubmissionAttempted || !claim.UnresolvedSubmission || claim.ProviderRequestId is not null
            || !Guid.TryParseExact(claim.LocalAttemptId, "N", out _) || !string.Equals(claim.DiagnosticCode, "google-dispatch-claimed", StringComparison.Ordinal)
            || !string.Equals(claim.AccountId, envelope.AccountId, StringComparison.Ordinal)
            || !string.Equals(claim.OperationStableId, GoogleGenerationProvider.SynthesizeOperationStableId, StringComparison.Ordinal)
            || !string.Equals(claim.PayloadSha256, envelope.CompiledPayloadSha256, StringComparison.Ordinal) || claim.PayloadBytes != envelope.CompiledPayloadBytes)
            throw new InvalidOperationException("The internal request requires an exact owned local dispatch claim.");
        if (await ResumePolicyAsync(envelope.ChirpAuthorizationId, cancellationToken).ConfigureAwait(false) is { } resume)
            return await TryClaimResumeAsync(resume, envelope, projectId, currency, scale, estimate, claim, cancellationToken).ConfigureAwait(false);
        var policy = await PolicyAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(policy.ProjectId, projectId, StringComparison.Ordinal)) return false;
        using var context = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var connection = (SqliteConnection)context.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var approved = await LoadAuthorizedAsync(connection, transaction, policy, envelope, cancellationToken).ConfigureAwait(false);
        if (approved is null || !string.Equals(approved.Key, claim.IdempotencyKey, StringComparison.Ordinal)) return false;
        approved.Spend.EnsureStillAuthorized(envelope, currency, scale, estimate);
        await RequirePolicyCurrentAsync(policy, cancellationToken).ConfigureAwait(false);
        if (!await WriteOwnedClaimAsync(connection, transaction, id, claim, cancellationToken).ConfigureAwait(false)) return false;
        await RequirePolicyCurrentAsync(policy, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested(); transaction.Commit(); return true;
    }

    private async Task<AuthorizedSegment?> LoadAuthorizedAsync(SqliteConnection connection, SqliteTransaction transaction,
        ChirpArticleAdmissionPolicy policy, GoogleGenerationSubmissionEnvelope envelope, CancellationToken token)
    {
        await RequireNoResumeMappingAsync(connection, transaction, envelope.ChirpAuthorizationId!.Value, token).ConfigureAwait(false);
        using var command = ChirpArticleSql.Command(connection, transaction, """
            SELECT PlanSha256,CoverageOrdinal,EnvelopeJson,Currency,Scale,EstimateMinorUnits,ReservedMinorUnits,Consumed,ApprovedFinancialSha256,ChildKey
            FROM chirp_article_owned_attempts WHERE AuthorizationId=$id AND AccountId=$account;
            """, ("$id", envelope.ChirpAuthorizationId!.Value.ToString("N")), ("$account", envelope.AccountId));
        string plan, financialHash, key; int ordinal, scale; string currency; long estimate, reserved;
        using (var row = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
        {
            if (!await row.ReadAsync(token).ConfigureAwait(false) || row.GetInt64(7) != 0) return null;
            if (!string.Equals(row.GetString(2), JsonSerializer.Serialize(envelope), StringComparison.Ordinal)) return null;
            plan = row.GetString(0); ordinal = row.GetInt32(1); currency = row.GetString(3); scale = row.GetInt32(4);
            estimate = row.GetInt64(5); reserved = row.GetInt64(6); financialHash = row.GetString(8); key = row.GetString(9);
        }
        CurrentFinancial financial;
        try { financial = await ReadFinancialAsync(connection, transaction, policy, token).ConfigureAwait(false); }
        catch (InvalidOperationException error)
        { throw LocalRequestDiagnostics.Mark(error, LocalRequestReason.FinancialChanged, admissionExpiresUtc: policy.ExpiresAtUtc); }
        if (!string.Equals(financial.Sha256, financialHash, StringComparison.Ordinal))
            throw LocalRequestDiagnostics.Mark(new InvalidOperationException("Financial state changed after article approval; no dispatch is allowed."), LocalRequestReason.FinancialChanged);
        var article = policy.Articles.Single(x => string.Equals(x.Proposal.PlanSha256, plan, StringComparison.Ordinal));
        var current = await _evidence.ResolveAsync(connection, transaction, policy, article.Proposal.DocumentId, financial.Old, token,
            requirePersistedLegacy: true).ConfigureAwait(false);
        var part = article.Proposal.Parts.Single(x => x.CoverageOrdinal == ordinal);
        string text = ChirpNarrationPlanner.Slice(current.Document.Text, part.Range);
        var compiled = GoogleSpeechPlanCompiler.Compile(new SpeechPlan(article.Proposal.LanguageCode, [new SpeechText(text)], ChirpNarrationPlanner.Version),
            new(article.Proposal.LanguageCode, article.Proposal.VoiceName, "MP3", 5_000));
        _ = await CurrentAsync(connection, transaction, policy, new(article.Proposal.DocumentId, ordinal, policy.ProjectId,
            envelope with { ChirpAuthorizationId = null }, compiled.Payload, currency, scale, estimate), token).ConfigureAwait(false);
        return new(key, GoogleGenerationSpendAuthorization.Create(envelope, currency, scale, estimate, reserved));
    }

    private sealed record AuthorizedSegment(string Key, GoogleGenerationSpendAuthorization Spend);
}
