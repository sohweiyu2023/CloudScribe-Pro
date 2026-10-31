using CloudScribe.Application.Logging;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TenMinuteLocalReviewBoundaryIsUnchangedAndExpiryIsDiagnosableWithoutReservation(bool expired)
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await ChirpEvidenceAsync(f);
        var review = await e.Service.PrepareAsync(ChirpIntent(e), TestContext.Current.CancellationToken);
        e.Clock.Now = e.Clock.Now.AddMinutes(10).AddTicks(expired ? 1 : 0);
        if (expired)
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => e.Service.ApproveAsync(ChirpConfirmation(review), TestContext.Current.CancellationToken));
            var details = LocalRequestDiagnostics.Describe(error, e.Clock.Now);
            Assert.Equal(LocalRequestReason.ReviewExpired, details.Reason);
            Assert.Equal(review.ExpiresAtUtc, details.AdmissionExpiresUtc);
            Assert.True(details.ReviewExpiresUtc < details.AdmissionExpiresUtc);
            Assert.Empty(await FinancialRowsAsync(f, "chirp_article_attempts"));
            Assert.Single(await FinancialRowsAsync(f, "google_generation_queue_states"));
            var fresh = await e.Service.PrepareAsync(ChirpIntent(e), TestContext.Current.CancellationToken);
            _ = await e.Service.ApproveAsync(ChirpConfirmation(fresh), TestContext.Current.CancellationToken);
            Assert.Single(await FinancialRowsAsync(f, "chirp_article_attempts"));
        }
        else
        {
            _ = await e.Service.ApproveAsync(ChirpConfirmation(review), TestContext.Current.CancellationToken);
            Assert.Single(await FinancialRowsAsync(f, "chirp_article_attempts"));
        }
    }
}
