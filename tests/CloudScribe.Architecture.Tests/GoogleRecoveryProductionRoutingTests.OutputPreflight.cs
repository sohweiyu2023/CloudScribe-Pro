using System.Text;
using System.Text.Json;
using CloudScribe.App.Composition;
using CloudScribe.App.ViewModels;
using CloudScribe.Application.Batch;
using CloudScribe.Application.Batch.Authority;
using CloudScribe.Application.Generation;
using CloudScribe.Application.Logging;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Batch;
using CloudScribe.Infrastructure.Files;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Infrastructure.Persistence;
using CloudScribe.Providers.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Architecture.Tests;

public sealed partial class GoogleRecoveryProductionRoutingTests
{
    [Theory]
    [InlineData("file")]
    [InlineData("folder")]
    [InlineData("extension")]
    [InlineData("encoder")]
    [InlineData("probe")]
    public async Task ProductionBatchPreflightRejectsChangesAfterRehearsalBeforeAnyJobClaimOrProvider(string change)
    {
        using var f = new Fixture(); await f.InitializeChirpAsync(); f.AdmitSyntheticWholeArticle();
        var route = await CreateOutputPreflightRouteAsync(f);
        using var store = new FileBatchQueueStore(Path.Combine(f.WholeRoot, "batch-queue"));
        var (batch, narrator) = await CreateOutputPreflightBatchAsync(f, route, store);
        await route.Workflow.PreflightAsync(route.Destination, TestContext.Current.CancellationToken);
        switch (change)
        {
            case "file": File.WriteAllText(route.Destination, "keep original"); break;
            case "folder": Directory.CreateDirectory(route.Destination); break;
            case "extension": batch = await store.SaveAsync(batch.WithItem(batch.Items[0] with { OutputFileName = "new.wav" }), TestContext.Current.CancellationToken); break;
            case "encoder": File.WriteAllText(route.Tool, "changed encoder"); break;
            case "probe": File.Delete(Path.Combine(f.WholeRoot, "ffprobe.exe")); break;
        }
        var result = await new BatchQueueRunner(store, narrator, TimeProvider.System).RunAsync(batch.BatchId, batch.SettingsSha256,
            cancellationToken: TestContext.Current.CancellationToken);
        var row = Assert.Single(result.Items);
        Assert.Equal(BatchItemState.Failed, row.State); Assert.Null(row.JobId); Assert.Equal(0, row.ReservedMinorUnits);
        Assert.Equal("batch.article-output-preflight-nothing-sent", row.ReasonCode); Assert.Empty(route.Sent);
        using var db = f.CreateDbContext();
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM chirp_whole_article_jobs").SingleAsync(TestContext.Current.CancellationToken));
        await AssertOutputPreflightAttemptsAsync(f, 0);
    }

    [Fact]
    public async Task ProductionBatchCompletedJobReconcilesExistingOutputWithNoToolsAndNoNewSpeech()
    {
        using var f = new Fixture(); await f.InitializeChirpAsync(); f.AdmitSyntheticWholeArticle();
        var route = await CreateOutputPreflightRouteAsync(f);
        using var store = new FileBatchQueueStore(Path.Combine(f.WholeRoot, "batch-queue"));
        var (batch, narrator) = await CreateOutputPreflightBatchAsync(f, route, store);
        var runner = new BatchQueueRunner(store, narrator, TimeProvider.System);
        var done = await runner.RunAsync(batch.BatchId, batch.SettingsSha256, cancellationToken: TestContext.Current.CancellationToken);
        var row = Assert.Single(done.Items); Assert.Equal(BatchItemState.Completed, row.State);
        int sent = route.Sent.Count; Assert.True(sent > 1); Assert.Equal(f.Selection.ExactText, string.Concat(route.Sent));
        File.Delete(route.Tool); File.Delete(Path.Combine(f.WholeRoot, "ffprobe.exe"));
        // Model a lost batch acknowledgement after the article already completed. The saved job must be read, not restarted.
        var interrupted = await store.SaveAsync(done.WithItem(row with { State = BatchItemState.Ready }) with { State = BatchQueueState.Paused }, TestContext.Current.CancellationToken);
        var reconciled = await runner.RunAsync(interrupted.BatchId, batch.SettingsSha256, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(BatchItemState.Completed, reconciled.Items[0].State); Assert.Equal(row.JobId, reconciled.Items[0].JobId);
        Assert.Equal(sent, route.Sent.Count); Assert.True(File.Exists(route.Destination));
        await AssertOutputPreflightAttemptsAsync(f, sent);
    }

    [Fact]
    public async Task ProductionRuntimeRechecksAfterJobStartBeforeAnyReservation()
    {
        using var f = new Fixture(); await f.InitializeChirpAsync(); f.AdmitSyntheticWholeArticle();
        var route = await CreateOutputPreflightRouteAsync(f);
        await route.Workflow.PreflightAsync(route.Destination, TestContext.Current.CancellationToken);
        var job = await StartOutputPreflightJobAsync(route.Articles, f.Selection.DocumentId);
        File.WriteAllText(route.Tool, "changed after start");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => route.Articles.RunWholeArticleAsync(job.JobId, route.Runtime,
            token: TestContext.Current.CancellationToken));
        Assert.Equal(LocalRequestReason.ArticleOutputPreflight, LocalRequestDiagnostics.Describe(error, DateTimeOffset.UtcNow).Reason);
        var stopped = await route.Articles.ReadWholeArticleJobAsync(job.JobId, TestContext.Current.CancellationToken);
        Assert.Equal("article.guard-rejected-reconcile", stopped.ReasonCode); Assert.Empty(route.Sent);
        await AssertOutputPreflightAttemptsAsync(f, 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProductionRuntimePreservesAcceptedPartsAndRequiresAdmittedContinuationAfterRepair(bool completionBound)
    {
        using var f = new Fixture(); await f.InitializeChirpAsync(); f.AdmitSyntheticWholeArticle();
        var admitted = completionBound ? await PrepareCompletionRouteAsync(f) : (Articles: f.CreateArticles(), Job: (ChirpWholeArticleJob?)null);
        bool breakSecond = true;
        var route = await CreateOutputPreflightRouteAsync(f, (stage, ordinal) =>
        {
            if (breakSecond && stage == ArticleExecutionStage.Preparing && ordinal == 2)
                File.WriteAllText(Path.Combine(f.WholeRoot, "ffprobe.exe"), "changed before second part");
        }, admitted.Articles);
        using var store = new FileBatchQueueStore(Path.Combine(f.WholeRoot, "batch-queue"));
        var (batch, narrator) = await CreateOutputPreflightBatchAsync(f, route, store);
        if (admitted.Job is { } existing)
            batch = await store.SaveAsync(batch.WithItem(batch.Items[0] with { JobId = existing.JobId }), TestContext.Current.CancellationToken);
        var runner = new BatchQueueRunner(store, narrator, TimeProvider.System);
        var stopped = await runner.RunAsync(batch.BatchId, batch.SettingsSha256, cancellationToken: TestContext.Current.CancellationToken);
        var row = Assert.Single(stopped.Items);
        Assert.Equal(BatchItemState.Uncertain, row.State); Assert.Equal("batch.article-output-preflight-reconcile", row.ReasonCode);
        Assert.NotNull(row.JobId); Assert.Single(route.Sent);
        Assert.Equal(1, (await route.Articles.ProgressAsync(f.Selection.DocumentId, TestContext.Current.CancellationToken)).SavedCoverageParts);
        await AssertOutputPreflightAttemptsAsync(f, 1);
        breakSecond = false; File.WriteAllBytes(Path.Combine(f.WholeRoot, "ffprobe.exe"), [0]);
        await store.SaveAsync(BatchQueueRunner.CheckAgain(stopped, row.ItemId), TestContext.Current.CancellationToken);
        var done = await runner.RunAsync(batch.BatchId, batch.SettingsSha256, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(completionBound ? BatchItemState.Completed : BatchItemState.Uncertain, done.Items[0].State);
        Assert.Equal(row.JobId, done.Items[0].JobId);
        Assert.True(done.Items[0].ReservedMinorUnits >= row.ReservedMinorUnits);
        if (completionBound) Assert.Equal(f.Selection.ExactText, string.Concat(route.Sent));
        else
        {
            Assert.Equal("batch.article-needs-reconciliation", done.Items[0].ReasonCode);
            Assert.Single(route.Sent); Assert.False(File.Exists(route.Destination));
        }
        await AssertOutputPreflightAttemptsAsync(f, route.Sent.Count);
    }

    private static async Task AssertOutputPreflightAttemptsAsync(Fixture f, int count)
    {
        using var db = f.CreateDbContext();
        Assert.Equal(count, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM chirp_article_attempts").SingleAsync(TestContext.Current.CancellationToken).ConfigureAwait(false));
        Assert.Equal(count, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM chirp_article_owned_attempts").SingleAsync(TestContext.Current.CancellationToken).ConfigureAwait(false));
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM chirp_article_split_attempts").SingleAsync(TestContext.Current.CancellationToken).ConfigureAwait(false));
    }

    private static async Task<ChirpWholeArticleJob> StartOutputPreflightJobAsync(ChirpArticleService articles, Guid document)
    {
        var review = await articles.PrepareWholeArticleAsync(document, TestContext.Current.CancellationToken).ConfigureAwait(false);
        return await articles.StartWholeArticleAsync(new(review.PreviewId, review.ReviewSha256, review.MaximumAdditionalMinorUnits, true, true),
            TestContext.Current.CancellationToken).ConfigureAwait(false);
    }

    private static async Task<OutputPreflightRoute> CreateOutputPreflightRouteAsync(Fixture f, Action<ArticleExecutionStage, int>? observe = null,
        ChirpArticleService? admittedArticles = null)
    {
        var articles = admittedArticles ?? f.CreateArticles(); var persisted = new GoogleGenerationPersistedQueueStateStore(f);
        var spend = new GoogleGenerationSpendAuthorizationStore(f, TimeProvider.System, articles: articles);
        var owner = new GoogleGenerationProductionSubmissionStateOwner(spend);
        var preparation = new ChirpArticlePreparationService(articles, f.ChirpCompiler, owner, TimeProvider.System);
        var source = new GoogleGenerationProductionRuntimeRequestSource(spend, owner.ResolveCurrentAsync);
        var library = new EfDocumentLibrary(f, new DocumentContentStore(f.WholePaths), TimeProvider.System);
        var exporter = new ArticleAudioExportService(f, library, persisted, f.WholePaths, new WholeNative(), chirpArticles: articles);
        string tool = Path.Combine(f.WholeRoot, "ffmpeg.exe"), destination = Path.Combine(f.WholeRoot, "batch.mp3");
        await File.WriteAllBytesAsync(tool, [0], TestContext.Current.CancellationToken).ConfigureAwait(false);
        await File.WriteAllBytesAsync(Path.Combine(f.WholeRoot, "ffprobe.exe"), [0], TestContext.Current.CancellationToken).ConfigureAwait(false);
        var approved = await TrustedMediaToolsStore.CaptureAsync(tool).ConfigureAwait(false);
        List<string> sent = [];
        Task<GoogleGenerationUiExecutionContext> ResolveAsync(GoogleGenerationProductionRuntimeRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var queue = new GoogleGenerationQueueCoordinator((compiled, _, _) =>
            {
                using var payload = JsonDocument.Parse(compiled.CompiledPayload);
                sent.Add(payload.RootElement.GetProperty("input").GetProperty("text").GetString()!);
                byte[] audio = new byte[128]; Encoding.ASCII.GetBytes("ID3").CopyTo(audio, 0);
                return Task.FromResult(new GenerationProviderResponse(SubmissionDisposition.Accepted, null, audio, "audio/mpeg", null, "google-accepted"));
            });
            var bound = new GoogleGenerationBoundQueueCoordinator(queue, persisted.LoadAsync, persisted.SaveAsync,
                (claim, ct) => articles.TryClaimAuthorizedAsync(request.SubmissionEnvelope, f.Selection.ProjectStableId, "USD", 2, 1, claim, ct), persisted.SaveOutcomeAsync);
            return Task.FromResult(new GoogleGenerationUiExecutionContext(new(bound), request.Snapshot));
        }
        ChirpWholeArticleRuntime Runtime(string output) => new(preparation, articles, source, ResolveAsync, exporter, f.Selection,
            output, tool, observeStage: observe, approvedMediaTools: approved);
        var workflow = new BatchArticleWorkflow(articles, library, (_, output, _) => Runtime(output), f.Selection, approved);
        return new(articles, workflow, Runtime(destination), destination, tool, sent);
    }

    private static async Task<(NarrationBatch Batch, CompleteArticleBatchNarrator Narrator)> CreateOutputPreflightBatchAsync(
        Fixture f, OutputPreflightRoute route, FileBatchQueueStore store)
    {
        var s = f.Selection; var now = DateTimeOffset.UtcNow;
        var settings = new BatchSettings(s.LanguageCode, s.VoiceStableId, "Synthetic voice", BatchEngines.CompleteArticle, BatchFailurePolicy.StopBatch);
        var review = await route.Articles.PrepareWholeArticleAsync(s.DocumentId, TestContext.Current.CancellationToken).ConfigureAwait(false);
        var item = new BatchItem(Guid.NewGuid(), 1, "synthetic.txt", new string('a', 64), 100, s.ExactText.Length, Path.GetFileName(route.Destination),
            BatchItemState.Ready, DocumentId: s.DocumentId, RevisionId: s.RevisionId, Engine: BatchEngines.CompleteArticle);
        var batch = await store.SaveAsync(new(NarrationBatch.CurrentVersion, Guid.NewGuid(), "Synthetic batch", 0, BatchQueueState.Ready, settings,
            settings.Sha256(), f.WholeRoot, "USD", 2, [item], now), TestContext.Current.CancellationToken).ConfigureAwait(false);
        var profile = new BatchAuthorityProfile(BatchAuthorityProfile.CurrentSchemaVersion, BatchAuthorityMode.Reviewed,
            InstalledRecoveryAdmissionStore.ProfileHash(f.WholeRoot), s.AccountStableId, s.ProjectStableId, [], [BatchEngines.CompleteArticle],
            settings.FailurePolicy, "USD", 2, 1000, 1000, 30, 10, TimeSpan.FromHours(1), now.AddHours(1), "USD", 1, 3000);
        var consent = BatchConsent.Create(Guid.NewGuid(), batch, BatchConsentSource.ReviewedInstall, profile, s.AccountStableId, s.ProjectStableId,
            [new(item.ItemId, item.SourceSha256, s.DocumentId, s.RevisionId!.Value, review.PlanSha256, review.MaximumAdditionalMinorUnits)], now);
        var authority = new OutputPreflightAuthority(profile, consent);
        return (batch, new(new BatchAuthorityGate(authority, store, TimeProvider.System), route.Workflow, () => (s.AccountStableId, s.ProjectStableId)));
    }

    private sealed record OutputPreflightRoute(ChirpArticleService Articles, BatchArticleWorkflow Workflow, ChirpWholeArticleRuntime Runtime,
        string Destination, string Tool, List<string> Sent);

    private sealed class OutputPreflightAuthority(BatchAuthorityProfile profile, BatchConsent consent) : IBatchAuthorityStore
    {
        public Task<BatchAuthorityProfile?> LoadProfileAsync(CancellationToken cancellationToken = default) => Task.FromResult<BatchAuthorityProfile?>(profile);
        public Task<BatchConsent?> LoadConsentAsync(Guid batchId, CancellationToken cancellationToken = default) => Task.FromResult<BatchConsent?>(batchId == consent.BatchId ? consent : null);
        public Task SaveInAppConsentAsync(BatchConsent approved, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
