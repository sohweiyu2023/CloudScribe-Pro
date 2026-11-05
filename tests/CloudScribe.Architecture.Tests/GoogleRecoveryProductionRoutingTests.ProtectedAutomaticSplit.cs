using System.Net;
using System.Text;
using System.Text.Json;
using CloudScribe.App.Composition;
using CloudScribe.App.ViewModels;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Files;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Architecture.Tests;

public sealed partial class GoogleRecoveryProductionRoutingTests
{
    [Fact]
    public async Task ActualProviderNotSubmitted400KeepsVerifiedCaptureForChildAndGrandchildWithoutChangingRetryPolicy()
    {
        using var f = new Fixture();
        await f.InitializeChirpAsync(string.Join(", ", Enumerable.Repeat("Synthetic clause content", 6)) + "?");
        AdmitRuntimeRecoveryFixture(f);
        var articles = f.CreateArticles(); var persisted = new GoogleGenerationPersistedQueueStateStore(f);
        var spend = new GoogleGenerationSpendAuthorizationStore(f, TimeProvider.System, articles: articles);
        var owner = new GoogleGenerationProductionSubmissionStateOwner(spend);
        var preparation = new ChirpArticlePreparationService(articles, f.ChirpCompiler, owner, TimeProvider.System);
        var requests = new GoogleGenerationProductionRuntimeRequestSource(spend, owner.ResolveCurrentAsync);
        using var diagnostics = new GooglePrivateErrorDiagnostics(Path.Combine(f.WholeRoot, "private-recovery-evidence"), TimeProvider.System);
        using var handler = new ProtectedSplitHandler(); using var http = new HttpClient(handler);
        var transport = new GoogleGenerationHttpTransport(http, new ArticleSyntheticCredential(), new Uri("https://texttospeech.googleapis.com"), privateDiagnostics: diagnostics);
        var responses = new List<(string Key, SubmissionDisposition Disposition)>();
        var native = new WholeNative();
        var exporter = new ArticleAudioExportService(f, new EfDocumentLibrary(f, new DocumentContentStore(f.WholePaths), TimeProvider.System),
            persisted, f.WholePaths, native, chirpArticles: articles);
        string tool = Path.Combine(f.WholeRoot, "ffmpeg.exe"), destination = Path.Combine(f.WholeRoot, "protected-recovered.mp3");
        await File.WriteAllBytesAsync(tool, [0], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(f.WholeRoot, "ffprobe.exe"), [0], TestContext.Current.CancellationToken);
        var review = await articles.PrepareWholeArticleAsync(f.Selection.DocumentId, TestContext.Current.CancellationToken);
        var job = await articles.StartWholeArticleAsync(new(review.PreviewId, review.ReviewSha256, review.MaximumAdditionalMinorUnits, true, true), TestContext.Current.CancellationToken);
        using var capture = new PrivateArticleErrorCaptureScope(diagnostics, TimeProvider.System, job, f.Selection.RevisionId!.Value,
            f.Selection.AccountStableId, f.Selection.ProjectStableId, f.Selection.VoiceStableId,
            review.RemainingRequests + review.MaximumRecoveryRequests, true, allowConfirmedErrorContinuation: true);
        var runtime = new ChirpWholeArticleRuntime(preparation, articles, requests,
            (request, _) => ProtectedSplitContext(articles, persisted, request, transport, responses), exporter, f.Selection,
            destination, tool, privateCapture: capture);
        var done = await articles.RunWholeArticleAsync(job.JobId, runtime, token: TestContext.Current.CancellationToken);
        Assert.Equal(ChirpWholeArticleState.Complete, done.State); Assert.True(native.FullDecode);
        Assert.Equal(5, handler.Calls); Assert.Equal(5, responses.Select(x => x.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(2, responses.Count(x => x.Disposition == SubmissionDisposition.NotSubmitted));
        Assert.Equal(3, responses.Count(x => x.Disposition == SubmissionDisposition.Accepted));
        var errors = diagnostics.ReadRecords().Where(x => x.Response?.HttpStatus == 400).ToArray();
        Assert.Equal(2, errors.Length); Assert.All(errors, x => Assert.Equal(job.JobId, x.ArticleJobId));
        Assert.All(errors, x => { Assert.True(x.Response!.ReadCompleted); Assert.False(x.Truncated); });
        using var db = f.CreateDbContext();
        Assert.Equal(4, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM chirp_article_split_attempts").SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(3, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM chirp_article_split_receipts").SingleAsync(TestContext.Current.CancellationToken));
    }

    private static Task<GoogleGenerationUiExecutionContext> ProtectedSplitContext(ChirpArticleService articles,
        GoogleGenerationPersistedQueueStateStore persisted, GoogleGenerationProductionRuntimeRequest request,
        GoogleGenerationHttpTransport transport, List<(string Key, SubmissionDisposition Disposition)> responses)
    {
        var provider = new GoogleGenerationProvider(new(request.SubmissionEnvelope.AccountId, request.SubmissionEnvelope.CredentialReferenceId,
            new Uri("https://texttospeech.googleapis.com/v1/text:synthesize"), "global"), transport);
        var queue = new GoogleGenerationQueueCoordinator(async (compiled, _, token) =>
        {
            var response = await provider.SubmitAsync(compiled, token).ConfigureAwait(false);
            responses.Add((compiled.IdempotencyKey, response.Disposition)); return response;
        });
        var bound = new GoogleGenerationBoundQueueCoordinator(queue, persisted.LoadAsync, persisted.SaveAsync, (claim, token) =>
        {
            provider.RequirePrivateDiagnosticsReady(request.Snapshot.ProviderRequest);
            return articles.TryClaimAuthorizedAsync(request.SubmissionEnvelope, "synthetic-project", "USD", 2, 1, claim, token);
        }, persisted.SaveOutcomeAsync);
        return Task.FromResult(new GoogleGenerationUiExecutionContext(new(bound), request.Snapshot));
    }

    private sealed class ProtectedSplitHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Calls++;
            byte[] audio = new byte[128]; Encoding.ASCII.GetBytes("ID3").CopyTo(audio, 0);
            bool rejected = Calls <= 2;
            return Task.FromResult(new HttpResponseMessage(rejected ? HttpStatusCode.BadRequest : HttpStatusCode.OK)
            {
                Content = new StringContent(rejected ? JsonSerializer.Serialize(new
                {
                    error = new
                    {
                        status = "INVALID_ARGUMENT",
                        message = "This request contains sentences that are too long. Consider splitting up long sentences with punctuation. Synthetic retained detail."
                    }
                })
                    : JsonSerializer.Serialize(new { audioContent = Convert.ToBase64String(audio) }))
            });
        }
    }
}
