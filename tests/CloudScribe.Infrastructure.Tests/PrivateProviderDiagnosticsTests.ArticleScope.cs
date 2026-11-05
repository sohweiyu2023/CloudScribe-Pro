using System.Globalization;
using System.Net;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class PrivateProviderDiagnosticsTests
{
    [Fact]
    public void ArticleConsentCannotReplaceExistingOptInAndUncheckingCannotRearmIt()
    {
        using var f = new Fixture(); var job = ArticleJob(f);
        Assert.Throws<InvalidOperationException>(() => ArticleCapture(f, job, false));
        f.Arm(); Assert.Throws<InvalidOperationException>(() => ArticleCapture(f, job, true));
        f.Store.Disarm(); using var capture = ArticleCapture(f, job, true);
        Assert.Throws<InvalidOperationException>(() => f.Store.Arm(f.Binding, true));
        f.Store.Disarm(); Assert.Throws<PrivateDiagnosticNotReadyException>(() => capture.RequireReady(job));
        Assert.Equal(0, f.Handler.Calls);
    }

    [Fact]
    public async Task ArticleScopeCannotTreatPreviousPersistenceAsNewRequestEvidence()
    {
        using var f = new Fixture(); var job = ArticleJob(f); using var capture = ArticleCapture(f, job, true);
        var first = ArticleRequest(f, job, 1); capture.Attach(job, 1, first, ArticleEnvelope(f));
        f.Handler.Status = HttpStatusCode.OK; f.Handler.Body = "{\"audioContent\":\"SUQzAAAAAAAAAAAAAAAA\"}";
        _ = await f.Provider.SubmitAsync(first, TestContext.Current.CancellationToken);
        await capture.CompleteAsync(true, TestContext.Current.CancellationToken); Assert.True(capture.EvidenceComplete);
        var second = ArticleRequest(f, job, 2); capture.Attach(job, 2, second, ArticleEnvelope(f));
        await capture.CompleteAsync(true, TestContext.Current.CancellationToken); // No second transport occurred.
        Assert.False(capture.EvidenceComplete); Assert.Equal(1, f.Handler.Calls);
        Assert.Throws<PrivateDiagnosticNotReadyException>(() => capture.RequireReady(job));
    }

    [Fact]
    public async Task AuthorizedInFlightArticleResponseIsRetainedPastDeadlineWithoutAllowingAnotherRequest()
    {
        using var f = new Fixture(); var job = ArticleJob(f) with { ExpiresAtUtc = f.Clock.Now.AddMinutes(1) };
        using var capture = ArticleCapture(f, job, true);
        var request = ArticleRequest(f, job, 1); capture.Attach(job, 1, request, ArticleEnvelope(f));
        f.Handler.Body = "{\"error\":{\"status\":\"INVALID_ARGUMENT\",\"message\":\"This request contains sentences that are too long. Late synthetic article response\"}}";
        f.Handler.BeforeResponse = () => f.Clock.Now += TimeSpan.FromMinutes(2);
        _ = await f.Provider.SubmitAsync(request, TestContext.Current.CancellationToken);
        await capture.CompleteAsync(false, TestContext.Current.CancellationToken);
        Assert.Single(f.Store.ReadRecords()); Assert.True(capture.EvidenceComplete); Assert.Equal(1, f.Handler.Calls);
        Assert.Throws<PrivateDiagnosticNotReadyException>(() => capture.RequireReady(job));
    }

    [Fact]
    public async Task CompletionResponseAfterTenMinuteTicketAgeRetainsOnlyItsExactAlreadyDispatchedOutcome()
    {
        using var f = new Fixture(); var job = ArticleJob(f) with { CompletionBound = true };
        using var capture = new PrivateArticleErrorCaptureScope(f.Store, f.Clock, job, f.Binding.RevisionId,
            f.Binding.AccountId, f.Binding.ProjectId, f.Binding.VoiceId, 3, true, allowConfirmedErrorContinuation: true);
        var request = ArticleRequest(f, job, 1); capture.Attach(job, 1, request, ArticleEnvelope(f));
        f.Handler.Body = "{\"error\":{\"status\":\"INVALID_ARGUMENT\",\"message\":\"This request contains sentences that are too long. Late synthetic article response\"}}";
        f.Handler.BeforeResponse = () => { f.Clock.Now += TimeSpan.FromMinutes(11); f.Clock.Timestamp += TimeSpan.FromMinutes(11).Ticks; };
        var response = await f.Provider.SubmitAsync(request, TestContext.Current.CancellationToken);
        await capture.CompleteAsync(false, TestContext.Current.CancellationToken, continueAfterConfirmedSentenceRejection: true);
        Assert.Equal("google-http-400", response.DiagnosticCode); Assert.True(capture.EvidenceComplete);
        Assert.Contains("Late synthetic article response", Assert.Single(f.Store.ReadRecords()).Response!.RedactedBody, StringComparison.Ordinal);
        capture.RequireReady(job);
        Assert.Throws<PrivateDiagnosticNotReadyException>(() => f.Provider.RequirePrivateDiagnosticsReady(request));
        Assert.Equal(1, f.Handler.Calls);
    }

    [Theory]
    [InlineData("expiry")]
    [InlineData("rollback")]
    [InlineData("job")]
    public void ArticleCaptureRejectsExpiryRollbackAndChangedJobBeforeProvider(string change)
    {
        using var f = new Fixture(); var job = ArticleJob(f); using var capture = ArticleCapture(f, job, true);
        if (string.Equals(change, "expiry", StringComparison.Ordinal)) f.Clock.Now += TimeSpan.FromHours(7);
        else if (string.Equals(change, "rollback", StringComparison.Ordinal)) f.Clock.Now -= TimeSpan.FromSeconds(1);
        else job = job with { JobId = Guid.NewGuid() };
        Assert.Throws<PrivateDiagnosticNotReadyException>(() => capture.RequireReady(job)); Assert.Equal(0, f.Handler.Calls);
    }

    [Fact]
    public void CompletionCaptureHasNoRunTimerButRetainsExactJobIdentity()
    {
        using var f = new Fixture(); var job = ArticleJob(f) with { CompletionBound = true };
        using var capture = ArticleCapture(f, job, true);
        Assert.Null(capture.ExpiresUtc);
        f.Clock.Now += TimeSpan.FromHours(7); f.Clock.Timestamp += TimeSpan.FromHours(7).Ticks;
        capture.RequireReady(job);
        Assert.Throws<PrivateDiagnosticNotReadyException>(() => capture.RequireReady(job with { JobId = Guid.NewGuid() }));
        Assert.Throws<PrivateDiagnosticNotReadyException>(() => capture.RequireReady(job with { CompletionBound = false }));
        Assert.Equal(0, f.Handler.Calls);
    }

    private static ChirpWholeArticleJob ArticleJob(Fixture f) => new(Guid.NewGuid(), f.Binding.DocumentId,
        new string('a', 64), new string('b', 64), new string('c', 64), 3, f.Clock.Now.AddHours(1), f.Clock.Now,
        ChirpWholeArticleState.Ready, null, "article.ready");
    private static PrivateArticleErrorCaptureScope ArticleCapture(Fixture f, ChirpWholeArticleJob job, bool confirmed) =>
        new(f.Store, f.Clock, job, f.Binding.RevisionId, f.Binding.AccountId, f.Binding.ProjectId, f.Binding.VoiceId, 3, confirmed);
    private static GenerationProviderRequest ArticleRequest(Fixture f, ChirpWholeArticleJob job, int ordinal) =>
        new(f.Request.ProviderStableId, f.Request.OperationStableId, f.Request.AccountId,
            ChirpArticleService.KeyPrefix + job.PlanSha256 + ":" + ordinal.ToString(CultureInfo.InvariantCulture), f.Request.CompiledPayload, "mp3");
    private static GoogleGenerationSubmissionEnvelope ArticleEnvelope(Fixture f) => new(f.Binding.AccountId, f.Binding.CredentialReferenceId,
        "synthetic-capability", "synthetic-price", 1, f.Binding.VoiceId, "MP3", f.Binding.PayloadSha256, f.Request.CompiledPayload.Length,
        ChirpAuthorizationId: Guid.NewGuid());
}
