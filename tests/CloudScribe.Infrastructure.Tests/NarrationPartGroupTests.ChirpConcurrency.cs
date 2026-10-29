using CloudScribe.Infrastructure.Generation;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Fact]
    public async Task ChirpConcurrentReviewsCannotCreateTwoReservationsAndOnlyOneClaimWins()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await ChirpEvidenceAsync(f);
        var other = new ChirpArticleService(f, e.Admission, f.Paths, e.Clock); var intent = ChirpIntent(e);
        var first = await e.Service.PrepareAsync(intent, TestContext.Current.CancellationToken);
        var second = await other.PrepareAsync(intent, TestContext.Current.CancellationToken);
        var results = await Task.WhenAll(Task.Run(() => TryApproveChirpAsync(e.Service, first), TestContext.Current.CancellationToken),
            Task.Run(() => TryApproveChirpAsync(other, second), TestContext.Current.CancellationToken));
        var approval = Assert.Single(results, x => x is not null)!;
        var envelope = intent.Envelope with { ChirpAuthorizationId = approval.AuthorizationId };
        var claims = await Task.WhenAll(Task.Run(() => e.Service.TryClaimAuthorizedAsync(envelope, intent.ProjectId, "USD", 2, 1,
            ChirpClaim(intent, approval), TestContext.Current.CancellationToken), TestContext.Current.CancellationToken),
            Task.Run(() => other.TryClaimAuthorizedAsync(envelope, intent.ProjectId, "USD", 2, 1,
                ChirpClaim(intent, approval), TestContext.Current.CancellationToken), TestContext.Current.CancellationToken));
        Assert.Single(claims, x => x);
        using var db = f.CreateDbContext();
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM chirp_article_attempts").SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT SUM(ReservedMinorUnits) AS Value FROM chirp_article_attempts").SingleAsync(TestContext.Current.CancellationToken));
    }

    private static async Task<ChirpApprovedAttempt?> TryApproveChirpAsync(ChirpArticleService service, ChirpRequestReview review)
    {
        try { return await service.ApproveAsync(ChirpConfirmation(review), TestContext.Current.CancellationToken).ConfigureAwait(false); }
        catch (InvalidOperationException) { return null; }
    }

    [Fact]
    public async Task ChirpRevocationAfterApprovalWritesRollsBackTheWholeReservation()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await ChirpEvidenceAsync(f);
        var review = await e.Service.PrepareAsync(ChirpIntent(e), TestContext.Current.CancellationToken);
        e.Admission.RevokeAt = e.Admission.Reads + 3;
        await Assert.ThrowsAsync<InvalidOperationException>(() => e.Service.ApproveAsync(ChirpConfirmation(review), TestContext.Current.CancellationToken));
        Assert.Empty(await FinancialRowsAsync(f, "chirp_article_attempts"));
        Assert.Empty(await FinancialRowsAsync(f, "chirp_article_plans"));
        Assert.Single(await FinancialRowsAsync(f, "google_generation_queue_states"));
    }
}
