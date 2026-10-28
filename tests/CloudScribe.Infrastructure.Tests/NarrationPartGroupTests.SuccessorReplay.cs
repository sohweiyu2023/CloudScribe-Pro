using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("expired")]
    [InlineData("without-link")]
    public async Task RevokedFinalSuccessorReadRollsBackEveryFinancialAndAuditWrite(string mode)
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await RecoveryEvidenceAsync(f);
        var s = await StageSuccessorAsync(f, e);
        var resolver = new FinalReadRevocation(s.Admission, mode);
        var service = new GoogleExplicitRecoveryService(f, TimeProvider.System, s.Budget, resolver);
        var preview = await service.PrepareAsync(s.Intent(e), 3, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApproveAsync(Confirmation(preview), TestContext.Current.CancellationToken));
        using var db = f.CreateDbContext();
        Assert.Equal(1, await db.Database.SqlQueryRaw<long>("SELECT COUNT(*) AS Value FROM google_recovery_attempts").SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, await db.Database.SqlQueryRaw<long>("SELECT COUNT(*) AS Value FROM google_generation_queue_states").SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await db.Database.SqlQueryRaw<long>("SELECT COUNT(*) AS Value FROM google_recovery_grant_transitions").SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(253, await db.Database.SqlQueryRaw<long>("SELECT RetainedFloorMinorUnits AS Value FROM google_recovery_scopes").SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(67, await db.Database.SqlQueryRaw<long>("SELECT AllocationRemainingMinorUnits AS Value FROM google_recovery_scopes").SingleAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SameSourceApprovalCannotBeRotatedIntoAnotherGrantAfterItsOneAttempt(bool uppercaseAlias)
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await RecoveryEvidenceAsync(f);
        var s = await StageSuccessorAsync(f, e); var service = s.Service(f);
        var preview = await service.PrepareAsync(s.Intent(e), 3, TestContext.Current.CancellationToken);
        var approved = await service.ApproveAsync(Confirmation(preview), TestContext.Current.CancellationToken);
        var claim = ClaimRecovery(e, approved);
        Assert.True(await service.TryClaimAuthorizedAsync(e.Intent.Envelope with { RecoveryAuthorizationId = approved.AuthorizationId },
            e.Intent.ProjectId, "USD", 2, 3, claim, TestContext.Current.CancellationToken));
        var failed = claim with
        {
            UnresolvedSubmission = false,
            DiagnosticCode = "google-http-400",
            ErrorStatus = GoogleErrorStatus.InvalidArgument,
            OutcomeObservedAtUnixMilliseconds = 3
        };
        await new GoogleGenerationPersistedQueueStateStore(f).SaveOutcomeAsync(failed, TestContext.Current.CancellationToken);
        var prior = s.Policy.Predecessors.Single();
        var link = new RecoverySuccessorGrant(approved.AuthorizationId, approved.IdempotencyKey, JsonHash(failed), prior.AdmissionSha256,
            await FinancialHashAsync(f), uppercaseAlias ? prior.Successor!.SourceApprovalSha256.ToUpperInvariant() : prior.Successor!.SourceApprovalSha256);
        var rotated = RehashSuccessor(prior with { Successor = link, ExpiresAtUtc = prior.ExpiresAtUtc.AddMinutes(1) });
        var baseline = s.Policy.Budget with
        {
            ProjectCommittedMinorUnits = 256,
            AllocationCommittedMinorUnits = 16,
            QueueRows = await RowPinsAsync(f, "google_generation_queue_states", 3),
            RecoveryRows = await RowPinsAsync(f, "google_recovery_attempts", 1),
            TransitionRows = await RowPinsAsync(f, "google_recovery_grant_transitions", 1)
        };
        WriteSuccessorPolicy(f, s.Policy with { ExpiresAtUtc = rotated.ExpiresAtUtc, Budget = baseline, Predecessors = [rotated] });
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Service(f).PrepareAsync(e.Intent with { PreviousIdempotencyKey = approved.IdempotencyKey },
            3, TestContext.Current.CancellationToken));
        using var db = f.CreateDbContext();
        Assert.Equal(2, await db.Database.SqlQueryRaw<long>("SELECT COUNT(*) AS Value FROM google_recovery_attempts").SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(6, await db.Database.SqlQueryRaw<long>("SELECT SUM(ReservedMinorUnits) AS Value FROM google_recovery_attempts").SingleAsync(TestContext.Current.CancellationToken));
    }

    private sealed class FinalReadRevocation(IGoogleRecoveryPredecessorEvidenceResolver inner, string mode) : IGoogleRecoveryPredecessorEvidenceResolver
    {
        private int _reads;
        public async Task<GoogleRecoveryPredecessorEvidence?> ResolveAsync(string accountId, string operationId, string historicalKey,
            string exactHistoricalStateSha256, CancellationToken cancellationToken = default)
        {
            var value = await inner.ResolveAsync(accountId, operationId, historicalKey, exactHistoricalStateSha256, cancellationToken).ConfigureAwait(false);
            if (++_reads != 3) return value;
            return mode switch
            {
                "missing" => null,
                "expired" => value! with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1) },
                "without-link" => value! with { Successor = null },
                _ => throw new InvalidOperationException()
            };
        }
    }
}
