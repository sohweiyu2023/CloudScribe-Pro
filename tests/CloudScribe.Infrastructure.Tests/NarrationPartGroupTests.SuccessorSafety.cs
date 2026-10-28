using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Theory]
    [InlineData("unresolved")]
    [InlineData("pending")]
    [InlineData("accepted")]
    [InlineData("unknown")]
    [InlineData("nonce")]
    public async Task SuccessorCannotReauthorizeAnActiveUncertainOrAcceptedChild(string outcome)
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await RecoveryEvidenceAsync(f);
        var s = await StageSuccessorAsync(f, e);
        var q = outcome switch
        {
            "unresolved" => s.Failed with { UnresolvedSubmission = true },
            "accepted" => s.Failed with { DiagnosticCode = "google-accepted", ProviderRequestId = "synthetic-accepted-id" },
            "unknown" => s.Failed with { ErrorStatus = GoogleErrorStatus.Unknown },
            "nonce" => s.Failed with { LocalAttemptId = "different-attempt" },
            _ => s.Failed
        };
        // Synthetic corruption/outcome fixtures only; the application never rewrites historical rows.
        using (var db = f.CreateDbContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE google_generation_queue_states SET UnresolvedSubmission={(q.UnresolvedSubmission ? 1 : 0)},ProviderRequestId={q.ProviderRequestId},DiagnosticCode={q.DiagnosticCode},ErrorStatus={(int)q.ErrorStatus},LocalAttemptId={q.LocalAttemptId} WHERE IdempotencyKey={q.IdempotencyKey}", TestContext.Current.CancellationToken);
            if (outcome is "pending") await db.Database.ExecuteSqlRawAsync("UPDATE google_recovery_attempts SET Consumed=0", TestContext.Current.CancellationToken);
        }
        var predecessor = s.Policy.Predecessors.Single();
        predecessor = RehashSuccessor(predecessor with
        {
            Successor = predecessor.Successor! with
            { PriorStateSha256 = JsonHash(q), FinancialStateSha256 = await FinancialHashAsync(f) }
        });
        var baseline = s.Policy.Budget with { QueueRows = await RowPinsAsync(f, "google_generation_queue_states", 3), RecoveryRows = await RowPinsAsync(f, "google_recovery_attempts", 1) };
        WriteSuccessorPolicy(f, s.Policy with { Budget = baseline, Predecessors = [predecessor] });
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Service(f).PrepareAsync(s.Intent(e), 3, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SuccessorConcurrentApprovalAndAuditInterruptionReserveExactlyOnce()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await RecoveryEvidenceAsync(f);
        var s = await StageSuccessorAsync(f, e);
        var a = s.Service(f); var first = await a.PrepareAsync(s.Intent(e), 3, TestContext.Current.CancellationToken);
        using (var db = f.CreateDbContext())
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER synthetic_successor_interruption BEFORE INSERT ON google_recovery_grant_transitions BEGIN SELECT RAISE(ABORT,'synthetic interruption'); END;", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<SqliteException>(() => a.ApproveAsync(Confirmation(first), TestContext.Current.CancellationToken));
        using (var db = f.CreateDbContext())
        {
            Assert.Equal(1, await db.Database.SqlQueryRaw<long>("SELECT COUNT(*) AS Value FROM google_recovery_attempts").SingleAsync(TestContext.Current.CancellationToken));
            Assert.Equal(253, await db.Database.SqlQueryRaw<long>("SELECT RetainedFloorMinorUnits AS Value FROM google_recovery_scopes").SingleAsync(TestContext.Current.CancellationToken));
            Assert.Equal(67, await db.Database.SqlQueryRaw<long>("SELECT AllocationRemainingMinorUnits AS Value FROM google_recovery_scopes").SingleAsync(TestContext.Current.CancellationToken));
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER synthetic_successor_interruption", TestContext.Current.CancellationToken);
        }
        var b = s.Service(f);
        var pa = await a.PrepareAsync(s.Intent(e), 3, TestContext.Current.CancellationToken);
        var pb = await b.PrepareAsync(s.Intent(e), 3, TestContext.Current.CancellationToken);
        async Task<bool> Approve(GoogleExplicitRecoveryService service, GoogleRecoveryPreview p)
        { try { await service.ApproveAsync(Confirmation(p), TestContext.Current.CancellationToken).ConfigureAwait(false); return true; } catch (InvalidOperationException) { return false; } }
        Assert.Single(await Task.WhenAll(Approve(a, pa), Approve(b, pb)), result => result);
        using var final = f.CreateDbContext();
        Assert.Equal(2, await final.Database.SqlQueryRaw<long>("SELECT COUNT(*) AS Value FROM google_recovery_attempts").SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await final.Database.SqlQueryRaw<long>("SELECT COUNT(*) AS Value FROM google_recovery_grant_transitions").SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(6, await final.Database.SqlQueryRaw<long>("SELECT SUM(ReservedMinorUnits) AS Value FROM google_recovery_attempts").SingleAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("audit")]
    [InlineData("expiry")]
    [InlineData("scope")]
    [InlineData("canonical")]
    public async Task ChangedSuccessorEvidenceCannotClaimOrRenewAnExistingReservation(string change)
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await RecoveryEvidenceAsync(f);
        var s = await StageSuccessorAsync(f, e); var service = s.Service(f);
        var preview = await service.PrepareAsync(s.Intent(e), 3, TestContext.Current.CancellationToken);
        var approved = await service.ApproveAsync(Confirmation(preview), TestContext.Current.CancellationToken);
        using (var db = f.CreateDbContext())
        {
            if (change is "audit") await db.Database.ExecuteSqlRawAsync("UPDATE google_recovery_grant_transitions SET EvidenceJson='changed'", TestContext.Current.CancellationToken);
            if (change is "scope") await db.Database.ExecuteSqlRawAsync("UPDATE google_recovery_scopes SET RetainedFloorMinorUnits=999", TestContext.Current.CancellationToken);
        }
        if (change is "expiry") WriteSuccessorPolicy(f, s.Policy with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1) });
        if (change is "canonical")
        {
            var p = s.Policy.Predecessors.Single();
            WriteSuccessorPolicy(f, s.Policy with { Predecessors = [p with { Successor = p.Successor! with { SourceApprovalSha256 = new string('f', 64) } }] });
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Service(f).LoadExecutionAuthorizationAsync(
            e.Intent.Envelope with { RecoveryAuthorizationId = approved.AuthorizationId }, TestContext.Current.CancellationToken));
        using var final = f.CreateDbContext();
        Assert.Equal(1, await final.Database.SqlQueryRaw<long>("SELECT SUM(Consumed) AS Value FROM google_recovery_attempts").SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(6, await final.Database.SqlQueryRaw<long>("SELECT SUM(ReservedMinorUnits) AS Value FROM google_recovery_attempts").SingleAsync(TestContext.Current.CancellationToken));
    }
}
