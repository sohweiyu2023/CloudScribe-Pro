using CloudScribe.Infrastructure.Generation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MappedReservationRejectsActualLegacyConsumeAndQueueSqlButAllowsAtomicMappedClaim(bool successor)
    {
        using var f = new Fixture(); var r = await ReservedResumeAsync(f, successor);
        var review = await ResumeReviewAsync(r); await r.Service.ApproveReservedResumeAsync(ChirpConfirmation(review), TestContext.Current.CancellationToken);
        string before = await CatalogRowsAsync(f), attempt = Guid.NewGuid().ToString("N");
        using (var db = f.CreateDbContext())
        {
            await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync(
                successor ? "UPDATE chirp_article_successor_attempts SET Consumed=1,ClaimedLocalAttemptId={0} WHERE AuthorizationId={1} AND Consumed=0;"
                    : "UPDATE chirp_article_attempts SET Consumed=1,ClaimedLocalAttemptId={0} WHERE AuthorizationId={1} AND Consumed=0;",
                [attempt, r.Reserved.AuthorizationId.ToString("N")], TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync(
                "UPDATE google_generation_queue_states SET SubmissionAttempted=1,UnresolvedSubmission=1,LocalAttemptId={0},DiagnosticCode='google-dispatch-claimed',PayloadSha256={1},PayloadBytes={2},OutcomeObservedAtUnixMilliseconds=1 WHERE IdempotencyKey={3};",
                [attempt, r.Intent.Envelope.CompiledPayloadSha256, r.Intent.Envelope.CompiledPayloadBytes, r.Reserved.IdempotencyKey], TestContext.Current.CancellationToken));
        }
        Assert.Equal(before, await CatalogRowsAsync(f));
        Assert.True(await r.Service.TryClaimAuthorizedAsync(r.Admission.Policy!.ExecutionEnvelope, r.Intent.ProjectId, "USD", 2, 1,
            ChirpClaim(r.Intent, r.Reserved), TestContext.Current.CancellationToken));
        using var final = f.CreateDbContext();
        Assert.Equal(1, await final.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM chirp_reserved_dispatch_mappings").SingleAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CommittedOrphanBindingIsReconciliationStateAndNeverAutomaticResume()
    {
        using var f = new Fixture(); var r = await ReservedResumeAsync(f);
        var review = await ResumeReviewAsync(r); await r.Service.ApproveReservedResumeAsync(ChirpConfirmation(review), TestContext.Current.CancellationToken);
        using (var db = f.CreateDbContext()) await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO chirp_reserved_dispatch_mappings VALUES({0},{1},{2},{3},{4});",
            [r.Reserved.AuthorizationId.ToString("N"), r.Admission.Policy!.MappingId.ToString("N"), ChirpPolicyValidation.Hash(r.Admission.Policy),
                Guid.NewGuid().ToString("N"), r.Old.Clock.Now.ToUnixTimeMilliseconds()], TestContext.Current.CancellationToken);
        string before = await CatalogRowsAsync(f);
        await Assert.ThrowsAsync<InvalidOperationException>(() => r.Service.LoadSpendAsync(r.Admission.Policy!.ExecutionEnvelope, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ResumeReviewAsync(r));
        Assert.Equal(before, await CatalogRowsAsync(f));
        using var final = f.CreateDbContext();
        Assert.Equal(0, await final.Database.SqlQueryRaw<int>("SELECT Consumed AS Value FROM chirp_article_attempts").SingleAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RawLegacyClaimBeforeAnyMappingCannotLaterBeReinterpretedAsUnsubmitted()
    {
        using var f = new Fixture(); var r = await ReservedResumeAsync(f); string attempt = Guid.NewGuid().ToString("N");
        using (var db = f.CreateDbContext())
        {
            using var transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
            await db.Database.ExecuteSqlRawAsync("UPDATE chirp_article_attempts SET Consumed=1,ClaimedLocalAttemptId={0} WHERE AuthorizationId={1};",
                [attempt, r.Reserved.AuthorizationId.ToString("N")], TestContext.Current.CancellationToken);
            await db.Database.ExecuteSqlRawAsync("UPDATE google_generation_queue_states SET SubmissionAttempted=1,UnresolvedSubmission=1,LocalAttemptId={0},DiagnosticCode='google-dispatch-claimed',PayloadSha256={1},PayloadBytes={2},OutcomeObservedAtUnixMilliseconds=1 WHERE IdempotencyKey={3};",
                [attempt, r.Intent.Envelope.CompiledPayloadSha256, r.Intent.Envelope.CompiledPayloadBytes, r.Reserved.IdempotencyKey], TestContext.Current.CancellationToken);
            await transaction.CommitAsync(TestContext.Current.CancellationToken);
        }
        string before = await CatalogRowsAsync(f);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ResumeReviewAsync(r));
        Assert.Equal(before, await CatalogRowsAsync(f));
        using var final = f.CreateDbContext();
        Assert.Equal(0, await final.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM chirp_reserved_catalog_mappings").SingleAsync(TestContext.Current.CancellationToken));
    }
}
