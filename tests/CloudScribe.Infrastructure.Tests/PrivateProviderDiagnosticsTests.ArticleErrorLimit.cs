using System.Globalization;
using System.Text.Json;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class PrivateProviderDiagnosticsTests
{
    [Fact]
    public async Task ArticleHistoryCrossesEightAndThirtyTwoRetainsAllAfterRestartAndExpiresByAgeOnly()
    {
        using var f = new Fixture(); var job = ArticleJob(f);
        using var capture = BoundedErrorCapture(f, job);
        Guid first = Guid.Empty;
        for (int ordinal = 1; ordinal <= 64; ordinal++)
        {
            await CaptureBoundedErrorAsync(f, job, capture, ordinal).ConfigureAwait(true);
            Assert.True(capture.EvidenceComplete);
            if (ordinal == 1)
            {
                first = Assert.Single(f.Store.ReadRecords()).Id;
                File.Move(Assert.Single(Directory.GetFiles(f.Root, "article-*.pdiag")), Path.Combine(f.Root, first.ToString("N") + ".pdiag"));
            }
            if (ordinal is 9 or 33) Assert.Equal(ordinal, f.Store.ReadRecords().Count);
            capture.RequireReady(job);
        }
        Assert.Equal(64, f.Handler.Calls); capture.Dispose();
        using var restarted = new GooglePrivateErrorDiagnostics(f.Root, f.Clock);
        var retained = restarted.ReadRecords();
        Assert.Equal(64, retained.Count); Assert.Contains(retained, x => x.Id == first);
        Assert.All(retained, x => Assert.Equal(job.JobId, x.ArticleJobId));
        Assert.Equal(64, Directory.GetFiles(f.Root, "*.pdiag").Length); // Includes one pre-upgrade GUID-named article record.
        Assert.False(restarted.IsArmed);
        for (int index = 0; index < 10; index++) { f.Arm(); await f.SendAsync("Synthetic legacy error").ConfigureAwait(true); }
        Assert.Equal(64, restarted.ReadRecords().Count(x => x.ArticleJobId == job.JobId));
        Assert.Equal(10, restarted.ReadRecords().Count(x => x.ArticleJobId is null));
        f.Clock.Now += TimeSpan.FromHours(24);
        Assert.Empty(restarted.ReadRecords()); Assert.Empty(Directory.GetFiles(f.Root));
    }

    [Fact]
    public async Task LargerExplicitErrorLimitStillStopsOnMissingNinthProtectedReadback()
    {
        var persistence = new ChildReadbackStore("missing"); using var f = new Fixture(persistence);
        var job = ArticleJob(f); using var capture = BoundedErrorCapture(f, job);
        for (int ordinal = 1; ordinal <= 8; ordinal++)
        {
            await CaptureBoundedErrorAsync(f, job, capture, ordinal).ConfigureAwait(true);
            Assert.True(capture.EvidenceComplete); capture.RequireReady(job);
        }
        persistence.MutateNext = true;
        await CaptureBoundedErrorAsync(f, job, capture, 9).ConfigureAwait(true);
        Assert.False(capture.EvidenceComplete);
        Assert.Throws<PrivateDiagnosticNotReadyException>(() => capture.RequireReady(job));
        Assert.Equal(9, f.Handler.Calls);
        Assert.Equal(GoogleExplanationRetentionState.StorageFailed, f.Store.PersistenceOutcome);
    }

    [Fact]
    public async Task DefaultArticleCaptureStillStopsAfterFirstFailure()
    {
        using var f = new Fixture(); var job = ArticleJob(f);
        using var capture = ArticleCapture(f, job, true);
        await CaptureBoundedErrorAsync(f, job, capture, 1).ConfigureAwait(true);
        Assert.Throws<PrivateDiagnosticNotReadyException>(() => capture.RequireReady(job));
    }

    private static PrivateArticleErrorCaptureScope BoundedErrorCapture(Fixture f, ChirpWholeArticleJob job) =>
        new(f.Store, f.Clock, job, f.Binding.RevisionId, f.Binding.AccountId, f.Binding.ProjectId, f.Binding.VoiceId,
            65, true, allowConfirmedErrorContinuation: true);

    private static async Task CaptureBoundedErrorAsync(Fixture f, ChirpWholeArticleJob job,
        PrivateArticleErrorCaptureScope capture, int ordinal)
    {
        f.Clock.Now += TimeSpan.FromSeconds(1);
        f.Handler.Body = JsonSerializer.Serialize(new
        {
            error = new
            {
                status = "INVALID_ARGUMENT",
                message = "This request contains sentences that are too long. Synthetic error " + ordinal.ToString(CultureInfo.InvariantCulture)
            }
        });
        var request = ArticleRequest(f, job, ordinal);
        capture.Attach(job, ordinal, request, ArticleEnvelope(f));
        var response = await f.Provider.SubmitAsync(request, TestContext.Current.CancellationToken).ConfigureAwait(false);
        Assert.Equal("google-http-400", response.DiagnosticCode);
        await capture.CompleteAsync(false, TestContext.Current.CancellationToken, continueAfterConfirmedSentenceRejection: true).ConfigureAwait(false);
    }
}
