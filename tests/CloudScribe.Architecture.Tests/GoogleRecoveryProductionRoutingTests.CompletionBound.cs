using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudScribe.App.Composition;
using CloudScribe.App.ViewModels;
using CloudScribe.Application.Generation;
using CloudScribe.Infrastructure.Files;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Architecture.Tests;

public sealed partial class GoogleRecoveryProductionRoutingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletionModeActualAppRouteCrossesOldCutoffAndReconcilesInFlightStop(bool stopInFlight)
    {
        using var f = new Fixture(); await f.InitializeChirpAsync(); f.AdmitSyntheticWholeArticle();
        var (articles, job) = await PrepareCompletionRouteAsync(f);
        var persisted = new GoogleGenerationPersistedQueueStateStore(f);
        var spend = new GoogleGenerationSpendAuthorizationStore(f, TimeProvider.System, articles: articles);
        var owner = new GoogleGenerationProductionSubmissionStateOwner(spend);
        var preparation = new ChirpArticlePreparationService(articles, f.ChirpCompiler, owner, TimeProvider.System);
        var source = new GoogleGenerationProductionRuntimeRequestSource(spend, owner.ResolveCurrentAsync);
        using var stop = new CancellationTokenSource();
        using var diagnostics = new GooglePrivateErrorDiagnostics(Path.Combine(f.WholeRoot, "completion-private"), TimeProvider.System);
        using var handler = new CompletionAcceptHandler(stopInFlight ? stop : null); using var http = new HttpClient(handler);
        var transport = new GoogleGenerationHttpTransport(http, new ArticleSyntheticCredential(), new Uri("https://texttospeech.googleapis.com"), privateDiagnostics: diagnostics);
        var native = new WholeNative();
        var exporter = new ArticleAudioExportService(f, new EfDocumentLibrary(f, new DocumentContentStore(f.WholePaths), TimeProvider.System),
            persisted, f.WholePaths, native, chirpArticles: articles);
        string tool = Path.Combine(f.WholeRoot, "ffmpeg.exe");
        await File.WriteAllBytesAsync(tool, [0], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(f.WholeRoot, "ffprobe.exe"), [0], TestContext.Current.CancellationToken);
        using var capture = new PrivateArticleErrorCaptureScope(diagnostics, TimeProvider.System, job, f.Selection.RevisionId!.Value,
            f.Selection.AccountStableId, f.Selection.ProjectStableId, f.Selection.VoiceStableId, 100, true);
        Assert.Null(capture.ExpiresUtc); Assert.True(job.ExpiresAtUtc < DateTimeOffset.UtcNow);
        var runtime = new ChirpWholeArticleRuntime(preparation, articles, source,
            (request, _) => CompletionContext(articles, persisted, request, transport), exporter, f.Selection,
            Path.Combine(f.WholeRoot, "completion-output.mp3"), tool, privateCapture: capture);
        if (stopInFlight)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => articles.RunWholeArticleAsync(job.JobId, runtime, token: stop.Token));
            Assert.Equal(1, handler.Calls);
            using var db = f.CreateDbContext();
            Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM chirp_article_receipts").SingleAsync(TestContext.Current.CancellationToken));
            Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM chirp_article_attempts").SingleAsync(TestContext.Current.CancellationToken));
            Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM google_generation_queue_states WHERE UnresolvedSubmission<>0").SingleAsync(TestContext.Current.CancellationToken));
        }
        else
        {
            Assert.Equal(ChirpWholeArticleState.Complete, (await articles.RunWholeArticleAsync(job.JobId, runtime, token: TestContext.Current.CancellationToken)).State);
            Assert.True(native.FullDecode); Assert.True(handler.Calls > 0);
        }
    }

    private static Task<GoogleGenerationUiExecutionContext> CompletionContext(ChirpArticleService articles,
        GoogleGenerationPersistedQueueStateStore persisted, GoogleGenerationProductionRuntimeRequest request, GoogleGenerationHttpTransport transport)
    {
        GoogleGenerationProductionUiSnapshotValidator.Validate(request.Snapshot);
        var provider = new GoogleGenerationProvider(new(request.SubmissionEnvelope.AccountId, request.SubmissionEnvelope.CredentialReferenceId,
            new Uri("https://texttospeech.googleapis.com/v1/text:synthesize"), "global"), transport);
        var queue = new GoogleGenerationQueueCoordinator((compiled, _, token) => provider.SubmitAsync(compiled, token));
        var bound = new GoogleGenerationBoundQueueCoordinator(queue, persisted.LoadAsync, persisted.SaveAsync,
            (claim, token) => articles.TryClaimAuthorizedAsync(request.SubmissionEnvelope, "synthetic-project", "USD", 2, 1, claim, token), persisted.SaveOutcomeAsync);
        return Task.FromResult(new GoogleGenerationUiExecutionContext(new(bound), request.Snapshot));
    }

    private static async Task<(ChirpArticleService Articles, ChirpWholeArticleJob Job)> PrepareCompletionRouteAsync(Fixture f)
    {
        string path = Path.Combine(f.WholeRoot, "install", "chirp-article-admission-policy.json");
        var previous = JsonSerializer.Deserialize<ChirpArticleAdmissionPolicy>(await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken).ConfigureAwait(false))!
            with
        { ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1) };
        SaveCompletionFixturePolicy(f, previous);
        var clock = new CompletionRouteClock { Now = previous.ExpiresAtUtc.AddMinutes(-1) };
        var store = new InstalledChirpArticleAdmissionStore(Path.Combine(f.WholeRoot, "install", "app"), f.WholePaths, clock);
        var articles = new ChirpArticleService(f, store, f.WholePaths, clock);
        var review = await articles.PrepareWholeArticleAsync(f.Selection.DocumentId, TestContext.Current.CancellationToken).ConfigureAwait(false);
        var oldJob = await articles.StartWholeArticleAsync(new(review.PreviewId, review.ReviewSha256, review.MaximumAdditionalMinorUnits, true, true), TestContext.Current.CancellationToken).ConfigureAwait(false);
        var next = previous with { SourceApprovalSha256 = new string('f', 64), CompletionContinuity = new(1, oldJob.JobId, ChirpPolicyValidation.Hash(oldJob), previous) };
        SaveCompletionFixturePolicy(f, next); clock.Now = DateTimeOffset.UtcNow;
        review = await articles.PrepareWholeArticleAsync(f.Selection.DocumentId, TestContext.Current.CancellationToken).ConfigureAwait(false);
        var job = await articles.StartWholeArticleAsync(new(review.PreviewId, review.ReviewSha256, review.MaximumAdditionalMinorUnits, true, true), TestContext.Current.CancellationToken).ConfigureAwait(false);
        Assert.True(job.CompletionBound); Assert.Equal(oldJob.JobId, job.JobId);
        return (articles, job);
    }

    private static void SaveCompletionFixturePolicy(Fixture f, ChirpArticleAdmissionPolicy policy)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(policy);
        File.WriteAllBytes(Path.Combine(f.WholeRoot, "install", "chirp-article-admission-policy.json"), bytes);
        File.WriteAllText(Path.Combine(f.WholeRoot, "install", "cloudscribe-install.json"), JsonSerializer.Serialize(new
        { Product = "CloudScribe Pro", AppSha256 = policy.AppSha256, ChirpArticlePolicySha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) }));
    }

    private sealed class CompletionRouteClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class CompletionAcceptHandler(CancellationTokenSource? stop) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; stop?.Cancel(); Assert.False(cancellationToken.IsCancellationRequested);
            byte[] audio = new byte[128]; Encoding.ASCII.GetBytes("ID3").CopyTo(audio, 0);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(new { audioContent = Convert.ToBase64String(audio) })) });
        }
    }
}
