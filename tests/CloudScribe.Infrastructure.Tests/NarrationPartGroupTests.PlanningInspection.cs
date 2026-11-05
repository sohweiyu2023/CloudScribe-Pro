using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoredArticlePlanningReviewWorksWithoutPaidAuthorityAndDoesNotReserve(bool expiredInsteadOfAbsent)
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await ChirpEvidenceAsync(f);
        var accepted = await AcceptChirpAsync(f, e.Service, ChirpIntent(e), 1);
        await e.Service.PersistAcceptedAsync(accepted.Request, accepted.Outcome, TestContext.Current.CancellationToken);
        if (expiredInsteadOfAbsent) e.Clock.Now += TimeSpan.FromHours(2); else e.Admission.Policy = null;
        var before = await FinancialRowsAsync(f, "chirp_article_attempts");
        var inspection = await e.Service.InspectStoredArticleAsync(e.Document.Id, e.Article.Proposal.VoiceName,
            e.Article.Proposal.LanguageCode, 30m, TestContext.Current.CancellationToken);
        Assert.Equal(e.Article.Proposal.PlanSha256, inspection.PlanSha256);
        Assert.Equal(1, inspection.SavedParts);
        Assert.Equal(inspection.TotalParts - 1, inspection.RemainingRequests);
        Assert.True(inspection.RemainingScalars < inspection.SourceScalars);
        Assert.Equal(inspection.RemainingScalars * 30m / 1_000_000m, inspection.PlanningListPriceUsd);
        Assert.Empty(inspection.RecoveryRequiredOrdinals);
        Assert.Equal(before.Count, (await FinancialRowsAsync(f, "chirp_article_attempts")).Count);
        await Assert.ThrowsAnyAsync<Exception>(() => e.Service.PrepareWholeArticleAsync(e.Document.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PlanningReviewDoesNotSilentlySwitchVoiceOrIgnoreChangedAcceptedMedia()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await ChirpEvidenceAsync(f);
        var accepted = await AcceptChirpAsync(f, e.Service, ChirpIntent(e), 1);
        string output = await e.Service.PersistAcceptedAsync(accepted.Request, accepted.Outcome, TestContext.Current.CancellationToken);
        e.Admission.Policy = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => e.Service.InspectStoredArticleAsync(e.Document.Id, "different-voice",
            e.Article.Proposal.LanguageCode, 30m, TestContext.Current.CancellationToken));
        await File.WriteAllBytesAsync(output, [0], TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<Exception>(() => e.Service.InspectStoredArticleAsync(e.Document.Id, e.Article.Proposal.VoiceName,
            e.Article.Proposal.LanguageCode, 30m, TestContext.Current.CancellationToken));
        Assert.Single(await FinancialRowsAsync(f, "chirp_article_attempts"));
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("uncertain")]
    public async Task PlanningReviewShowsFailedOrUncertainCoverageWithoutClearingOrRetryingIt(string outcome)
    {
        using var f = new Fixture(); await f.InitializeAsync(); var failed = await CreateSuccessorFixtureAsync(f, outcome);
        var e = failed.Evidence; e.Admission.Policy = null;
        var inspection = await e.Service.InspectStoredArticleAsync(e.Document.Id, e.Article.Proposal.VoiceName,
            e.Article.Proposal.LanguageCode, 30m, TestContext.Current.CancellationToken);
        Assert.Contains(1, inspection.RecoveryRequiredOrdinals);
        Assert.Equal(0, inspection.SavedParts);
        Assert.Single(await FinancialRowsAsync(f, "chirp_article_attempts"));
        Assert.Empty(await FinancialRowsAsync(f, "chirp_article_successor_attempts"));
    }
}
