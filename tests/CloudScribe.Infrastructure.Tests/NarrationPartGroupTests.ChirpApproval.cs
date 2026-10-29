using CloudScribe.Infrastructure.Generation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Fact]
    public async Task ChirpReviewIsReadOnlyAndRestartReusesExactCommittedReservation()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await ChirpEvidenceAsync(f); var intent = ChirpIntent(e);
        var review = await e.Service.PrepareAsync(intent, TestContext.Current.CancellationToken);
        Assert.Empty(await FinancialRowsAsync(f, "chirp_article_attempts"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => e.Service.ApproveAsync(ChirpConfirmation(review) with { ConfirmedByUser = false }, TestContext.Current.CancellationToken));
        var approved = await e.Service.ApproveAsync(ChirpConfirmation(review), TestContext.Current.CancellationToken);
        Assert.Single(await FinancialRowsAsync(f, "chirp_article_attempts"));
        var restarted = new ChirpArticleService(f, e.Admission, f.Paths, e.Clock);
        review = await restarted.PrepareAsync(intent, TestContext.Current.CancellationToken);
        Assert.Equal(approved.AuthorizationId, review.ExistingAuthorizationId);
        Assert.Equal(149, review.ProjectCommittedMinorUnits);
        Assert.Equal(approved, await restarted.ApproveAsync(ChirpConfirmation(review), TestContext.Current.CancellationToken));
        Assert.Single(await FinancialRowsAsync(f, "chirp_article_attempts"));
        Assert.Equal(e.Historical, await new GoogleGenerationPersistedQueueStateStore(f).LoadAsync(e.Historical.AccountId,
            e.Historical.OperationStableId, e.Historical.IdempotencyKey, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("account")]
    [InlineData("payload")]
    [InlineData("project")]
    [InlineData("estimate")]
    public async Task ChirpMismatchedRequestCannotCreateReservation(string mismatch)
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await ChirpEvidenceAsync(f); var intent = ChirpIntent(e);
        intent = mismatch switch
        {
            "account" => intent with { Envelope = intent.Envelope with { AccountId = "different" } },
            "project" => intent with { ProjectId = "different" },
            "estimate" => intent with { EstimateMinorUnits = 2 },
            _ => intent with { CompiledPayload = "different"u8.ToArray() },
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => e.Service.PrepareAsync(intent, TestContext.Current.CancellationToken));
        Assert.Empty(await FinancialRowsAsync(f, "chirp_article_attempts"));
    }

    [Fact]
    public async Task ChirpInterruptedApprovalRollsBackQueuePlanAndReservationTogether()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await ChirpEvidenceAsync(f);
        var review = await e.Service.PrepareAsync(ChirpIntent(e), TestContext.Current.CancellationToken);
        using (var db = f.CreateDbContext())
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER synthetic_chirp_approval BEFORE INSERT ON chirp_article_attempts BEGIN SELECT RAISE(ABORT,'synthetic-interruption'); END;", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<SqliteException>(() => e.Service.ApproveAsync(ChirpConfirmation(review), TestContext.Current.CancellationToken));
        Assert.Empty(await FinancialRowsAsync(f, "chirp_article_attempts"));
        Assert.Empty(await FinancialRowsAsync(f, "chirp_article_plans"));
        Assert.Single(await FinancialRowsAsync(f, "google_generation_queue_states"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChirpExpiryAndFinalReadRevocationPreventApproval(bool revoke)
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await ChirpEvidenceAsync(f);
        var review = await e.Service.PrepareAsync(ChirpIntent(e), TestContext.Current.CancellationToken);
        if (revoke) e.Admission.RevokeAt = e.Admission.Reads + 2;
        else e.Clock.Now = e.Clock.Now.AddHours(2);
        await Assert.ThrowsAnyAsync<Exception>(() => e.Service.ApproveAsync(ChirpConfirmation(review), TestContext.Current.CancellationToken));
        Assert.Empty(await FinancialRowsAsync(f, "chirp_article_attempts"));
        Assert.Single(await FinancialRowsAsync(f, "google_generation_queue_states"));
    }
}
