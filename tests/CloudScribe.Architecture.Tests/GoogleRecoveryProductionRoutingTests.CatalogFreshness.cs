using System.Net;
using System.Text;
using System.Text.Json;
using CloudScribe.App.Composition;
using CloudScribe.App.ViewModels;
using CloudScribe.Application.Logging;
using CloudScribe.Infrastructure.Files;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Architecture.Tests;

public sealed partial class GoogleRecoveryProductionRoutingTests
{
    [Theory]
    [InlineData("ordinary")]
    [InlineData("ordinary-recompile")]
    [InlineData("child")]
    [InlineData("unchanged")]
    [InlineData("changed-voice")]
    [InlineData("still-stale")]
    [InlineData("after-reservation")]
    [InlineData("held-child")]
    public async Task ActualArticleRouteRefreshesStaleCompilationOnlyBeforeAnyReservation(string scenario)
    {
        using var f = new Fixture();
        await f.InitializeChirpAsync("Synthetic first clause, second clause, third clause, final clause.");
        AdmitRuntimeRecoveryFixture(f);
        var articles = f.CreateArticles(); var persisted = new GoogleGenerationPersistedQueueStateStore(f);
        var spend = new GoogleGenerationSpendAuthorizationStore(f, TimeProvider.System, articles: articles);
        var owner = new GoogleGenerationProductionSubmissionStateOwner(spend);
        bool child = scenario is "child" or "held-child";
        var compiler = new StaleArticleCompilation(f.ChirpCompiler, f.Selection.ExactText, scenario);
        var preparation = new ChirpArticlePreparationService(articles, compiler, owner, TimeProvider.System);
        var source = new GoogleGenerationProductionRuntimeRequestSource(spend, owner.ResolveCurrentAsync);
        using var diagnostics = new GooglePrivateErrorDiagnostics(Path.Combine(f.WholeRoot, "freshness-private"), TimeProvider.System);
        using var handler = new FreshnessAcceptHandler(child); using var http = new HttpClient(handler);
        var transport = new GoogleGenerationHttpTransport(http, new ArticleSyntheticCredential(), new Uri("https://texttospeech.googleapis.com"), privateDiagnostics: diagnostics);
        var (exporter, native, tool) = await CreateFreshnessExporterAsync(f, persisted, articles);
        var review = await articles.PrepareWholeArticleAsync(f.Selection.DocumentId, TestContext.Current.CancellationToken);
        var job = await articles.StartWholeArticleAsync(new(review.PreviewId, review.ReviewSha256, review.MaximumAdditionalMinorUnits, true, true), TestContext.Current.CancellationToken);
        using var capture = new PrivateArticleErrorCaptureScope(diagnostics, TimeProvider.System, job, f.Selection.RevisionId!.Value,
            f.Selection.AccountStableId, f.Selection.ProjectStableId, f.Selection.VoiceStableId, 100, true, allowConfirmedErrorContinuation: true);
        int refreshes = 0;
        var runtime = new ChirpWholeArticleRuntime(preparation, articles, source,
            (request, _) => ResolveFreshnessContextAsync(f, articles, persisted, job, request, transport, scenario),
            exporter, f.Selection, Path.Combine(f.WholeRoot, "freshness.mp3"), tool, () => false, async _ =>
            {
                using var db = f.CreateDbContext();
                Assert.Equal(child ? 1 : 0, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM chirp_article_owned_attempts").SingleAsync(TestContext.Current.CancellationToken).ConfigureAwait(false));
                refreshes++; compiler.Refreshed = true;
                return string.Equals(scenario, "unchanged", StringComparison.Ordinal) ? f.Selection : f.Selection with
                {
                    CapturedAtUtc = f.Selection.CapturedAtUtc.AddSeconds(1),
                    CapabilityEvidenceId = "fresh-capability",
                    VoiceStableId = string.Equals(scenario, "changed-voice", StringComparison.Ordinal) ? "other-voice" : f.Selection.VoiceStableId
                };
            }, privateCapture: capture);
        if (scenario is "ordinary" or "ordinary-recompile" or "child")
        {
            var done = await articles.RunWholeArticleAsync(job.JobId, runtime, token: TestContext.Current.CancellationToken);
            Assert.Equal(ChirpWholeArticleState.Complete, done.State); Assert.True(native.FullDecode);
            Assert.Equal(child ? 3 : 1, handler.Calls); Assert.Equal(1, refreshes);
            Assert.Equal(job.JobId, done.JobId); Assert.Equal(job.CatalogRefreshes, done.CatalogRefreshes);
            if (child) Assert.True(Assert.Single(diagnostics.ReadRecords(), x => x.Response?.HttpStatus == 400).Response!.ReadCompleted);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => articles.RunWholeArticleAsync(job.JobId, runtime, token: TestContext.Current.CancellationToken));
            Assert.Equal(scenario is "held-child" ? 1 : 0, handler.Calls);
            Assert.Equal(scenario is "after-reservation" or "held-child" ? 0 : 1, refreshes);
        }
        await AssertFreshnessReservationsAsync(f, scenario);
    }

    private static async Task<(ArticleAudioExportService Exporter, WholeNative Native, string Tool)> CreateFreshnessExporterAsync(
        Fixture f, GoogleGenerationPersistedQueueStateStore persisted, ChirpArticleService articles)
    {
        var native = new WholeNative();
        var exporter = new ArticleAudioExportService(f, new EfDocumentLibrary(f, new DocumentContentStore(f.WholePaths), TimeProvider.System),
            persisted, f.WholePaths, native, chirpArticles: articles);
        string tool = Path.Combine(f.WholeRoot, "ffmpeg.exe");
        await File.WriteAllBytesAsync(tool, [0], TestContext.Current.CancellationToken).ConfigureAwait(false);
        await File.WriteAllBytesAsync(Path.Combine(f.WholeRoot, "ffprobe.exe"), [0], TestContext.Current.CancellationToken).ConfigureAwait(false);
        return (exporter, native, tool);
    }

    private static async Task AssertFreshnessReservationsAsync(Fixture f, string scenario)
    {
        using var final = f.CreateDbContext();
        Assert.Equal(scenario is "ordinary" or "ordinary-recompile" ? 1 : scenario is "child" ? 3 : scenario is "held-child" ? 2 : scenario is "after-reservation" ? 1 : 0,
            await final.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM chirp_article_owned_attempts").SingleAsync(TestContext.Current.CancellationToken).ConfigureAwait(false));
        Assert.Equal(0, await final.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM google_generation_queue_states WHERE UnresolvedSubmission<>0").SingleAsync(TestContext.Current.CancellationToken).ConfigureAwait(false));
    }

    private static async Task<GoogleGenerationUiExecutionContext> ResolveFreshnessContextAsync(Fixture f, ChirpArticleService articles,
        GoogleGenerationPersistedQueueStateStore persisted, ChirpWholeArticleJob job, GoogleGenerationProductionRuntimeRequest request,
        GoogleGenerationHttpTransport transport, string scenario)
    {
        if (string.Equals(scenario, "after-reservation", StringComparison.Ordinal))
        {
            var executing = await articles.ReadWholeArticleJobAsync(job.JobId, TestContext.Current.CancellationToken).ConfigureAwait(false);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => articles.RequireUnreservedCatalogRefreshAsync(executing,
                executing.ActiveOrdinal!.Value, null, TestContext.Current.CancellationToken)).ConfigureAwait(false);
            Assert.Equal("Existing reserved or uncertain requests must be reconciled before a preparation refresh.", error.Message);
            throw StaleArticleCompilation.Stale();
        }
        if (string.Equals(scenario, "held-child", StringComparison.Ordinal) && request.Snapshot.ProviderRequest.IdempotencyKey.Split(':').Length == 4)
        {
            using var db = f.CreateDbContext();
            string json = await db.Database.SqlQueryRaw<string>("SELECT MappingJson AS Value FROM chirp_article_split_mappings").SingleAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
            var child = JsonSerializer.Deserialize<ChirpArticleRecoveryChild>(json)!;
            var executing = await articles.ReadWholeArticleJobAsync(job.JobId, TestContext.Current.CancellationToken).ConfigureAwait(false);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => articles.RequireUnreservedCatalogRefreshAsync(executing, child.CoverageOrdinal, child,
                TestContext.Current.CancellationToken)).ConfigureAwait(false);
            Assert.Equal("Existing reserved or uncertain requests must be reconciled before a preparation refresh.", error.Message);
            throw StaleArticleCompilation.Stale();
        }
        return await CompletionContext(articles, persisted, request, transport).ConfigureAwait(false);
    }

    private sealed class StaleArticleCompilation(IGoogleRecoveryCurrentCompilation inner, string original, string scenario) : IGoogleRecoveryCurrentCompilation
    {
        public bool Refreshed { get; set; }
        private int _calls;
        public Task<GoogleGenerationProductionPendingApprovalStateOwner.PendingState> CompileReadonlyAsync(
            ShellViewModel.GoogleTtsStudioRequestSelection selection, CancellationToken token) => inner.CompileReadonlyAsync(selection, token);
        public Task<GoogleGenerationProductionPendingApprovalStateOwner.PendingState> CompileArticleReadonlyAsync(
            ShellViewModel.GoogleTtsStudioRequestSelection selection, ChirpResolvedSegment part, CancellationToken token)
        {
            _calls++;
            bool target = string.Equals(scenario, "child", StringComparison.Ordinal)
                ? !string.Equals(part.ExactText, original, StringComparison.Ordinal) : scenario is not ("after-reservation" or "held-child");
            if (string.Equals(scenario, "ordinary-recompile", StringComparison.Ordinal)) target = _calls == 2;
            if (target && (!Refreshed || string.Equals(scenario, "still-stale", StringComparison.Ordinal))) throw Stale();
            return inner.CompileArticleReadonlyAsync(selection, part, token);
        }
        public static InvalidOperationException Stale() => LocalRequestDiagnostics.Mark(
            new InvalidOperationException("Synthetic expired capability."), LocalRequestReason.CapabilityStale,
            capabilityExpiresUtc: DateTimeOffset.UtcNow.AddSeconds(-1));
    }

    private sealed class FreshnessAcceptHandler(bool rejectRoot) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; bool rejected = rejectRoot && Calls == 1;
            byte[] audio = new byte[128]; Encoding.ASCII.GetBytes("ID3").CopyTo(audio, 0);
            return Task.FromResult(new HttpResponseMessage(rejected ? HttpStatusCode.BadRequest : HttpStatusCode.OK)
            {
                Content = new StringContent(rejected ? JsonSerializer.Serialize(new { error = new { status = "INVALID_ARGUMENT", message = "This request contains sentences that are too long." } })
                : JsonSerializer.Serialize(new { audioContent = Convert.ToBase64String(audio) }))
            });
        }
    }
}
