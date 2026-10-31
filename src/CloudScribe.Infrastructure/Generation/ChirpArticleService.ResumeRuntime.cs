using CloudScribe.Application.Generation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class ChirpArticleService
{
    private async Task<GoogleGenerationSpendAuthorization> LoadResumeSpendAsync(ChirpReservationResumePolicy policy,
        GoogleGenerationSubmissionEnvelope envelope, CancellationToken token)
    {
        using var context = await factory.CreateDbContextAsync(token).ConfigureAwait(false);
        var connection = (SqliteConnection)context.Database.GetDbConnection(); await connection.OpenAsync(token).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        var state = await ResolveResumeAsync(connection, transaction, policy, envelope, true, token).ConfigureAwait(false);
        await RequireResumeCurrentAsync(policy, token).ConfigureAwait(false);
        return state.Authorized.Spend;
    }

    private async Task<bool> TryClaimResumeAsync(ChirpReservationResumePolicy policy, GoogleGenerationSubmissionEnvelope envelope,
        string project, string currency, int scale, long estimate, GoogleGenerationPersistedQueueState claim, CancellationToken token)
    {
        if (!string.Equals(project, policy.HistoricalPolicy.ProjectId, StringComparison.Ordinal)) throw ResumeRejected();
        using var context = await factory.CreateDbContextAsync(token).ConfigureAwait(false);
        var connection = (SqliteConnection)context.Database.GetDbConnection(); await connection.OpenAsync(token).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var state = await ResolveResumeAsync(connection, transaction, policy, envelope, true, token).ConfigureAwait(false);
        if (!string.Equals(state.Authorized.Key, claim.IdempotencyKey, StringComparison.Ordinal)) throw ResumeRejected();
        state.Authorized.Spend.EnsureStillAuthorized(envelope, currency, scale, estimate);
        await RequireResumeCurrentAsync(policy, token).ConfigureAwait(false);
        using (var bind = ChirpArticleSql.Command(connection, transaction,
            "INSERT INTO chirp_reserved_dispatch_mappings VALUES($id,$mapping,$sha,$attempt,$time);",
            ("$id", policy.AuthorizationId.ToString("N")), ("$mapping", policy.MappingId.ToString("N")),
            ("$sha", ChirpPolicyValidation.Hash(policy)), ("$attempt", claim.LocalAttemptId), ("$time", clock.GetUtcNow().ToUnixTimeMilliseconds())))
            await bind.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        if (!await WriteOwnedClaimAsync(connection, transaction, policy.AuthorizationId, claim, token).ConfigureAwait(false)) return false;
        await RequireResumeCurrentAsync(policy, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested(); transaction.Commit(); return true;
    }

    public async Task ValidateReservedRuntimeBindingAsync(GoogleGenerationSubmissionEnvelope envelope, string runtimeEvidenceSha256,
        string currency, int scale, long estimate, CancellationToken token = default)
    {
        if (envelope.ChirpAuthorizationId is not { } id) return;
        var policy = await ResumePolicyAsync(id, token).ConfigureAwait(false);
        if (policy is null) return; // Exact ordinary spend loading still rejects any existing mapping without current resume authority.
        if (envelope != policy.ExecutionEnvelope || !string.Equals(runtimeEvidenceSha256, policy.RuntimeEvidenceSha256, StringComparison.Ordinal)) throw ResumeRejected();
        var spend = await LoadResumeSpendAsync(policy, envelope, token).ConfigureAwait(false);
        spend.EnsureStillAuthorized(envelope, currency, scale, estimate);
    }
}
