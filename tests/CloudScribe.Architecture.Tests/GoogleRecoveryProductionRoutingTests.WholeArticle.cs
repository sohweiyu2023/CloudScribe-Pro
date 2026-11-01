using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudScribe.App.Composition;
using CloudScribe.App.ViewModels;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Files;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Infrastructure.Persistence;
using CloudScribe.Providers.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Architecture.Tests;

public sealed partial class GoogleRecoveryProductionRoutingTests
{
    [Theory]
    [InlineData(SubmissionDisposition.Accepted, false)]
    [InlineData(SubmissionDisposition.RejectedSafeToRetry, false)]
    [InlineData(SubmissionDisposition.UnknownRequiresReconciliation, false)]
    [InlineData(SubmissionDisposition.Accepted, true)]
    public async Task WholeArticleActualRuntimeCompilesClaimsPersistsAndAssemblesOrStopsAtFirstFakeFailure(SubmissionDisposition disposition, bool refresh)
    {
        using var f = new Fixture(); await f.InitializeChirpAsync(); f.AdmitSyntheticWholeArticle(refresh ? 1 : 0);
        var articles = f.CreateArticles();
        var spend = new GoogleGenerationSpendAuthorizationStore(f, TimeProvider.System, articles: articles);
        var owner = new GoogleGenerationProductionSubmissionStateOwner(spend);
        var preparation = new ChirpArticlePreparationService(articles, f.ChirpCompiler, owner, TimeProvider.System);
        var source = new GoogleGenerationProductionRuntimeRequestSource(spend, owner.ResolveCurrentAsync);
        var persisted = new GoogleGenerationPersistedQueueStateStore(f);
        List<string> sent = [];
        Task<GoogleGenerationUiExecutionContext> ResolveAsync(GoogleGenerationProductionRuntimeRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var queue = new GoogleGenerationQueueCoordinator((compiled, _, _) =>
            {
                using var payload = JsonDocument.Parse(compiled.CompiledPayload);
                sent.Add(payload.RootElement.GetProperty("input").GetProperty("text").GetString()!);
                byte[] bytes = new byte[128]; Encoding.ASCII.GetBytes("ID3").CopyTo(bytes, 0);
                return Task.FromResult(disposition != SubmissionDisposition.Accepted ? new GenerationProviderResponse(disposition, null, ReadOnlyMemory<byte>.Empty, null, null, "google-http-400")
                    : new GenerationProviderResponse(SubmissionDisposition.Accepted, null, bytes, "audio/mpeg", null, "google-accepted"));
            });
            var bound = new GoogleGenerationBoundQueueCoordinator(queue, persisted.LoadAsync, persisted.SaveAsync,
                (claim, ct) => articles.TryClaimAuthorizedAsync(request.SubmissionEnvelope, f.Selection.ProjectStableId, "USD", 2, 1, claim, ct), persisted.SaveOutcomeAsync);
            return Task.FromResult(new GoogleGenerationUiExecutionContext(new(bound), request.Snapshot));
        }
        var native = new WholeNative();
        var exporter = new ArticleAudioExportService(f, new EfDocumentLibrary(f, new DocumentContentStore(f.WholePaths), TimeProvider.System),
            persisted, f.WholePaths, native, chirpArticles: articles);
        string tool = Path.Combine(f.WholeRoot, "ffmpeg.exe"), destination = Path.Combine(f.WholeRoot, "whole.mp3");
        await File.WriteAllBytesAsync(tool, [0], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(f.WholeRoot, "ffprobe.exe"), [0], TestContext.Current.CancellationToken);
        bool expired = refresh;
        var runtime = new ChirpWholeArticleRuntime(preparation, articles, source, ResolveAsync, exporter, f.Selection, destination, tool,
            () => expired, token =>
            {
                token.ThrowIfCancellationRequested(); expired = false; f.ChirpCompiler.Capability = "fresh-synthetic-capability";
                return Task.FromResult(f.Selection with { CapabilityEvidenceId = f.ChirpCompiler.Capability });
            });
        var review = await articles.PrepareWholeArticleAsync(f.Selection.DocumentId, TestContext.Current.CancellationToken);
        var job = await articles.StartWholeArticleAsync(new(review.PreviewId, review.ReviewSha256, review.MaximumAdditionalMinorUnits, true, true), TestContext.Current.CancellationToken);
        if (disposition != SubmissionDisposition.Accepted)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => articles.RunWholeArticleAsync(job.JobId, runtime, token: TestContext.Current.CancellationToken));
            Assert.Single(sent); Assert.False(File.Exists(destination));
            Assert.Equal(ChirpWholeArticleState.Stopped, (await articles.ReadWholeArticleJobAsync(job.JobId, TestContext.Current.CancellationToken)).State);
        }
        else
        {
            await AssertWholeRuntimeSuccessAsync(f, articles, job, runtime, review, native, sent, destination);
        }
        using var db = f.CreateDbContext();
        Assert.Equal(sent.Count, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM chirp_article_attempts").SingleAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("voice")]
    [InlineData("project")]
    [InlineData("text")]
    [InlineData("cancel")]
    public async Task WholeArticleActualRuntimeRefreshMismatchOrCancellationCannotReserveOrDispatch(string change)
    {
        using var f = new Fixture(); await f.InitializeChirpAsync(); f.AdmitSyntheticWholeArticle(1);
        var articles = f.CreateArticles(); var spend = new GoogleGenerationSpendAuthorizationStore(f, TimeProvider.System, articles: articles);
        var owner = new GoogleGenerationProductionSubmissionStateOwner(spend);
        var preparation = new ChirpArticlePreparationService(articles, f.ChirpCompiler, owner, TimeProvider.System);
        var source = new GoogleGenerationProductionRuntimeRequestSource(spend, owner.ResolveCurrentAsync);
        var exporter = new ArticleAudioExportService(f, new EfDocumentLibrary(f, new DocumentContentStore(f.WholePaths), TimeProvider.System),
            new GoogleGenerationPersistedQueueStateStore(f), f.WholePaths, new WholeNative(), chirpArticles: articles);
        Task<ShellViewModel.GoogleTtsStudioRequestSelection> RefreshAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return change switch
            {
                "voice" => Task.FromResult(f.Selection with { VoiceStableId = "changed" }),
                "project" => Task.FromResult(f.Selection with { ProjectStableId = "changed" }),
                "text" => Task.FromResult(f.Selection with { ExactText = "changed" }),
                _ => throw new OperationCanceledException("synthetic cancellation"),
            };
        }
        var runtime = new ChirpWholeArticleRuntime(preparation, articles, source,
            (_, _) => throw new InvalidOperationException("No execution context may be resolved after mismatched refresh."), exporter,
            f.Selection, Path.Combine(f.WholeRoot, "not-generated.mp3"), Path.Combine(f.WholeRoot, "not-run-ffmpeg.exe"), () => true, RefreshAsync);
        var review = await articles.PrepareWholeArticleAsync(f.Selection.DocumentId, TestContext.Current.CancellationToken);
        var job = await articles.StartWholeArticleAsync(new(review.PreviewId, review.ReviewSha256, review.MaximumAdditionalMinorUnits, true, true), TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<Exception>(() => articles.RunWholeArticleAsync(job.JobId, runtime, token: TestContext.Current.CancellationToken));
        job = await articles.ReadWholeArticleJobAsync(job.JobId, TestContext.Current.CancellationToken);
        Assert.Equal(ChirpWholeArticleState.Stopped, job.State); Assert.Equal(1, job.CatalogRefreshes);
        using var db = f.CreateDbContext();
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM chirp_article_attempts").SingleAsync(TestContext.Current.CancellationToken));
    }

    private static async Task AssertWholeRuntimeSuccessAsync(Fixture f, ChirpArticleService articles, ChirpWholeArticleJob job,
        ChirpWholeArticleRuntime runtime, ChirpWholeArticleReview review, WholeNative native, List<string> sent, string destination)
    {
        job = await articles.RunWholeArticleAsync(job.JobId, runtime, token: TestContext.Current.CancellationToken).ConfigureAwait(false);
        Assert.Equal(ChirpWholeArticleState.Complete, job.State); Assert.NotNull(job.Output);
        Assert.Equal(review.MaximumCatalogRefreshes, job.CatalogRefreshes);
        Assert.Equal(f.Selection.ExactText, string.Concat(sent));
        Assert.Equal(review.RemainingRequests, sent.Count); Assert.True(native.FullDecode);
        Assert.True(File.Exists(destination));
        _ = await f.CreateArticles().RunWholeArticleAsync(job.JobId, runtime, token: TestContext.Current.CancellationToken).ConfigureAwait(false);
        Assert.Equal(review.RemainingRequests, sent.Count);
    }

    private sealed partial class Fixture
    {
        public string WholeRoot => _root;
        public CloudScribe.Infrastructure.Configuration.AppPaths WholePaths => _paths;
        public void AdmitSyntheticWholeArticle(int maximumCatalogRefreshes = 0)
        {
            string path = Path.Combine(_root, "install", "chirp-article-admission-policy.json");
            var p = JsonSerializer.Deserialize<ChirpArticleAdmissionPolicy>(File.ReadAllBytes(path))!;
            p = p with { SchemaVersion = 3, PriorSuccessorAttemptRows = [], WholeArticleGeneration = new(Guid.NewGuid(), [p.Articles[0].Proposal.PlanSha256], maximumCatalogRefreshes) };
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(p); File.WriteAllBytes(path, bytes);
            File.WriteAllText(Path.Combine(_root, "install", "cloudscribe-install.json"), JsonSerializer.Serialize(new
            { Product = "CloudScribe Pro", AppSha256 = p.AppSha256, ChirpArticlePolicySha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) }));
        }
    }

    private sealed class WholeNative : INativeMediaTool
    {
        private int _inputs;
        public bool FullDecode { get; private set; }
        public async Task<NativeMediaToolResult> RunAsync(NativeMediaToolInvocation invocation, CancellationToken cancellationToken = default)
        {
            string output = string.Empty;
            if (invocation.Arguments.Contains("-show_entries", StringComparer.Ordinal))
                output = JsonSerializer.Serialize(new { streams = new[] { new { codec_name = "mp3", sample_rate = "24000", channels = 1 } }, format = new { duration = Math.Max(1, _inputs).ToString(System.Globalization.CultureInfo.InvariantCulture) } });
            else if (invocation.Arguments.Contains("-filter_complex", StringComparer.Ordinal))
            {
                _inputs = invocation.Arguments.Count(x => string.Equals(x, "-i", StringComparison.Ordinal));
                byte[] bytes = new byte[128]; Encoding.ASCII.GetBytes("ID3").CopyTo(bytes, 0);
                await File.WriteAllBytesAsync(invocation.Arguments[^1], bytes, cancellationToken).ConfigureAwait(false);
            }
            else if (invocation.Arguments.Contains("-xerror", StringComparer.Ordinal)) FullDecode = true;
            return new(0, false, output, string.Empty, TimeSpan.Zero);
        }
    }
}
