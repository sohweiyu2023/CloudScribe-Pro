using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudScribe.App.Composition;
using CloudScribe.App.ViewModels;
using CloudScribe.Application.Generation;
using CloudScribe.Infrastructure.Configuration;
using CloudScribe.Infrastructure.Diagnostics;
using CloudScribe.Infrastructure.Files;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudScribe.Architecture.Tests;

public sealed partial class GoogleRecoveryProductionRoutingTests
{
    [Fact]
    public async Task ActualWholeArticleRuntimeRetainsLaterFailedChunkExplanationAndIdentityAfterRestart()
    {
        using var f = new Fixture(); await f.InitializeChirpAsync(); f.AdmitSyntheticWholeArticle();
        var articles = f.CreateArticles(); var persisted = new GoogleGenerationPersistedQueueStateStore(f);
        var spend = new GoogleGenerationSpendAuthorizationStore(f, TimeProvider.System, articles: articles);
        var owner = new GoogleGenerationProductionSubmissionStateOwner(spend);
        var preparation = new ChirpArticlePreparationService(articles, f.ChirpCompiler, owner, TimeProvider.System);
        var source = new GoogleGenerationProductionRuntimeRequestSource(spend, owner.ResolveCurrentAsync);
        var options = Options.Create(new CloudScribeOptions { AppDataDirectoryOverride = f.WholeRoot });
        using var sink = new BoundedJsonFileLoggerProvider(f.WholePaths, options, TimeProvider.System);
        using var loggers = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Information).AddProvider(sink));
        string privateRoot = Path.Combine(f.WholeRoot, "private-article-evidence");
        using var diagnostics = new GooglePrivateErrorDiagnostics(privateRoot, TimeProvider.System,
            logger: loggers.CreateLogger<GooglePrivateErrorDiagnostics>());
        using var handler = new LaterArticleErrorHandler(); using var http = new HttpClient(handler);
        var transport = new GoogleGenerationHttpTransport(http, new ArticleSyntheticCredential(), new Uri("https://texttospeech.googleapis.com"),
            loggers.CreateLogger<GoogleGenerationHttpTransport>(), diagnostics);
        Task<GoogleGenerationUiExecutionContext> ResolveAsync(GoogleGenerationProductionRuntimeRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var provider = new GoogleGenerationProvider(new(request.SubmissionEnvelope.AccountId, request.SubmissionEnvelope.CredentialReferenceId,
                new Uri("https://texttospeech.googleapis.com/v1/text:synthesize"), "global"), transport);
            var queue = new GoogleGenerationQueueCoordinator((compiled, _, ct) => provider.SubmitAsync(compiled, ct));
            var bound = new GoogleGenerationBoundQueueCoordinator(queue, persisted.LoadAsync, persisted.SaveAsync, (claim, ct) =>
            {
                provider.RequirePrivateDiagnosticsReady(request.Snapshot.ProviderRequest);
                return articles.TryClaimAuthorizedAsync(request.SubmissionEnvelope, f.Selection.ProjectStableId, "USD", 2, 1, claim, ct);
            }, persisted.SaveOutcomeAsync);
            return Task.FromResult(new GoogleGenerationUiExecutionContext(new(bound), request.Snapshot));
        }
        var exporter = new ArticleAudioExportService(f, new EfDocumentLibrary(f, new DocumentContentStore(f.WholePaths), TimeProvider.System),
            persisted, f.WholePaths, new WholeNative(), chirpArticles: articles);
        var review = await articles.PrepareWholeArticleAsync(f.Selection.DocumentId, TestContext.Current.CancellationToken);
        Assert.True(review.RemainingRequests > 2);
        var job = await articles.StartWholeArticleAsync(new(review.PreviewId, review.ReviewSha256, review.MaximumAdditionalMinorUnits, true, true), TestContext.Current.CancellationToken);
        using var capture = new PrivateArticleErrorCaptureScope(diagnostics, TimeProvider.System, job, f.Selection.RevisionId!.Value,
            f.Selection.AccountStableId, f.Selection.ProjectStableId, f.Selection.VoiceStableId, review.RemainingRequests, explicitlyConfirmed: true);
        string tool = Path.Combine(f.WholeRoot, "ffmpeg.exe");
        await File.WriteAllBytesAsync(tool, [0], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(f.WholeRoot, "ffprobe.exe"), [0], TestContext.Current.CancellationToken);
        var runtime = new ChirpWholeArticleRuntime(preparation, articles, source, ResolveAsync, exporter, f.Selection,
            Path.Combine(f.WholeRoot, "not-complete.mp3"), tool, privateCapture: capture,
            diagnosticLogger: loggers.CreateLogger<ChirpWholeArticleRuntime>());
        await Assert.ThrowsAsync<InvalidOperationException>(() => articles.RunWholeArticleAsync(job.JobId, runtime, token: TestContext.Current.CancellationToken));
        await AssertStoppedArticleAndPrivateEvidenceAsync(f, articles, job, runtime, handler, diagnostics, privateRoot, loggers, sink);
    }

    private static async Task AssertStoppedArticleAndPrivateEvidenceAsync(Fixture f, ChirpArticleService articles, ChirpWholeArticleJob job,
        ChirpWholeArticleRuntime runtime, LaterArticleErrorHandler handler, GooglePrivateErrorDiagnostics diagnostics,
        string privateRoot, ILoggerFactory loggers, BoundedJsonFileLoggerProvider sink)
    {
        await diagnostics.WaitForPendingWriteAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        using var restarted = new GooglePrivateErrorDiagnostics(privateRoot, TimeProvider.System);
        var failure = AssertArticleErrorEvidence(restarted.ReadRecords(), job, handler);
        Assert.Equal(PrivateProviderErrorReadStatus.Success, restarted.ReadStatus);
        Assert.False(restarted.IsArmed); Assert.Equal(2, handler.Calls);
        var progress = await articles.ProgressAsync(job.DocumentId, TestContext.Current.CancellationToken).ConfigureAwait(false);
        Assert.Equal(1, progress.SavedCoverageParts);
        Assert.Equal(ChirpWholeArticleState.Stopped, (await articles.ReadWholeArticleJobAsync(job.JobId, TestContext.Current.CancellationToken).ConfigureAwait(false)).State);
        _ = await articles.RunWholeArticleAsync(job.JobId, runtime, token: TestContext.Current.CancellationToken).ConfigureAwait(false);
        Assert.Equal(2, handler.Calls);
        using var db = f.CreateDbContext();
        Assert.Equal(2, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM chirp_article_attempts").SingleAsync(TestContext.Current.CancellationToken).ConfigureAwait(false));
        loggers.Dispose(); sink.Dispose();
        string log = await File.ReadAllTextAsync(sink.CurrentLogPath, TestContext.Current.CancellationToken).ConfigureAwait(false);
        Assert.Contains(failure.TransportCorrelation, log, StringComparison.Ordinal);
        Assert.Contains("\"EventId\":1613", log, StringComparison.Ordinal);
        Assert.Contains("\"EventId\":1614", log, StringComparison.Ordinal);
        Assert.Contains("\"EventId\":1615", log, StringComparison.Ordinal);
        Assert.DoesNotContain(LaterArticleErrorHandler.CausalDetail, log, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE_ARTICLE_SECRET", log, StringComparison.Ordinal);
    }

    private static PrivateProviderErrorRecord AssertArticleErrorEvidence(IReadOnlyList<PrivateProviderErrorRecord> records,
        ChirpWholeArticleJob job, LaterArticleErrorHandler handler)
    {
        var failure = Assert.Single(records, x => x.Response?.HttpStatus == 400);
        Assert.Equal(job.JobId, failure.ArticleJobId); Assert.Equal(2, failure.ArticleOrdinal);
        Assert.True(Guid.TryParseExact(failure.TransportCorrelation, "N", out _));
        Assert.Contains(LaterArticleErrorHandler.CausalDetail, failure.Response!.RedactedBody, StringComparison.Ordinal);
        Assert.Contains("[input fragment redacted]", failure.Response.RedactedBody, StringComparison.Ordinal);
        Assert.True(failure.Response.RedactionApplied);
        Assert.DoesNotContain("PRIVATE_ARTICLE_SECRET", failure.Response.RedactedBody, StringComparison.Ordinal);
        Assert.Equal(handler.PayloadHashes[1], failure.PayloadSha256); return failure;
    }

    private sealed class ArticleSyntheticCredential : ITransientCredentialResolver
    {
        public ValueTask<string> ResolveAccessTokenAsync(string credentialReferenceId, CancellationToken cancellationToken) => ValueTask.FromResult("PRIVATE_ARTICLE_SECRET");
    }

    private sealed class LaterArticleErrorHandler : HttpMessageHandler
    {
        internal const string Explanation = "Explicit synthetic provider explanation: clause at boundary eleven is unsupported.";
        internal const string CausalDetail = "clause at boundary eleven is unsupported.";
        public int Calls { get; private set; }
        public List<string> PayloadHashes { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; var payload = await request.Content!.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            PayloadHashes.Add(Convert.ToHexStringLower(SHA256.HashData(payload)));
            byte[] audio = new byte[128]; Encoding.ASCII.GetBytes("ID3").CopyTo(audio, 0);
            return new(Calls == 1 ? HttpStatusCode.OK : HttpStatusCode.BadRequest)
            {
                Content = new StringContent(Calls == 1 ? JsonSerializer.Serialize(new { audioContent = Convert.ToBase64String(audio) })
                    : JsonSerializer.Serialize(new { error = new { status = "INVALID_ARGUMENT", details = new[] { new { nested = new { explanation = Explanation, client_secret = "PRIVATE_ARTICLE_SECRET" } } } } }))
            };
        }
    }
}
