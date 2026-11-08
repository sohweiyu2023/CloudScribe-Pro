using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Fact]
    public async Task AdditiveTwoVoicesShareOneAtomicCapAndCannotBorrowLegacyHeadroom()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await AdditiveEvidenceAsync(f, 60);
        var other = new ChirpArticleService(f, e.Store, f.Paths, e.Legacy.Clock);
        var firstJob = await AdditionalExecutingJobAsync(f, e.Service, e.First.Document.Id);
        var secondJob = await AdditionalExecutingJobAsync(f, other, e.Second.Document.Id);
        var first = await e.Service.PrepareAsync(FullDocumentIntent(e.First, 1), TestContext.Current.CancellationToken);
        var second = await other.PrepareAsync(FullDocumentIntent(e.Second, 1), TestContext.Current.CancellationToken);
        var approvals = await Task.WhenAll(Task.Run(() => TryApproveAdditionalAsync(e.Service, firstJob.JobId, first), TestContext.Current.CancellationToken),
            Task.Run(() => TryApproveAdditionalAsync(other, secondJob.JobId, second), TestContext.Current.CancellationToken));
        Assert.Single(approvals, x => x is not null);
        var row = Assert.Single(await FinancialRowsAsync(f, "chirp_article_attempts"));
        Assert.Equal(60L, row[13]);
        Assert.Equal(99, e.Store.Catalog.Families[0].Policy.MaximumAdditionalReservationMinorUnits);
        Assert.Equal(300, e.Store.Catalog.LegacyPolicy!.Budget.ProjectCeilingMinorUnits);
    }

    [Fact]
    public async Task AdditiveExpiredSiblingKeepsItsChargeWithoutBlockingAnUnchangedLegacyReservation()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await AdditiveEvidenceAsync(f, 2);
        var family = e.Store.Catalog.Families[0];
        family = family with { Policy = family.Policy with { ExpiresAtUtc = e.Legacy.Clock.Now.AddMinutes(10) } };
        e.Store.Catalog = e.Store.Catalog with { Families = [family] };
        var oldIntent = ChirpIntent(e.Legacy);
        var oldReview = await e.Service.PrepareAsync(oldIntent, TestContext.Current.CancellationToken);
        var old = await e.Service.ApproveAsync(ChirpConfirmation(oldReview), TestContext.Current.CancellationToken);
        var job = await AdditionalExecutingJobAsync(f, e.Service, e.First.Document.Id);
        var intent = FullDocumentIntent(e.First, 1);
        var review = await e.Service.PrepareAsync(intent, TestContext.Current.CancellationToken);
        var held = await e.Service.ApproveWholeArticleRequestAsync(job.JobId, review.PreviewId, TestContext.Current.CancellationToken);
        // Only the family expires; its immutable admission stays present for full financial validation.
        e.Legacy.Clock.Now = e.Store.Catalog.Families[0].Policy.ExpiresAtUtc.AddTicks(1);
        Assert.NotNull(await e.Service.LoadSpendAsync(oldIntent.Envelope with { ChirpAuthorizationId = old.AuthorizationId }, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => e.Service.PrepareAsync(intent, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => e.Service.TryClaimAuthorizedAsync(intent.Envelope with { ChirpAuthorizationId = held.AuthorizationId },
            intent.ProjectId, "USD", 2, intent.EstimateMinorUnits, ChirpClaim(intent, held), TestContext.Current.CancellationToken));
        Assert.Equal(2, (await FinancialRowsAsync(f, "chirp_article_attempts")).Count);
        Assert.All(await FinancialRowsAsync(f, "chirp_article_attempts"), row => Assert.Equal(0L, row[16]));
        Assert.All((await e.Service.ProgressAsync(e.First.Document.Id, TestContext.Current.CancellationToken)).Parts, part => Assert.False(part.CurrentlyAdmitted));
    }

    private static async Task<ChirpApprovedAttempt?> TryApproveAdditionalAsync(ChirpArticleService service, Guid job, ChirpRequestReview review)
    {
        try { return await service.ApproveWholeArticleRequestAsync(job, review.PreviewId, TestContext.Current.CancellationToken).ConfigureAwait(false); }
        catch (InvalidOperationException) { return null; }
    }
}
