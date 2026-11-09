using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudScribe.App.Composition;
using CloudScribe.App.ViewModels;
using CloudScribe.Application.Documents;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Documents;
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
    [InlineData("accepted-children")]
    [InlineData("uncertain-child")]
    [InlineData("generic-root")]
    [InlineData("output-changed-before-child")]
    public async Task ActualAppRuntimeRoutesBoundedChildrenThroughNativeApprovalClaimSaveAndExport(string mode)
    {
        using var f = new Fixture();
        await f.InitializeChirpAsync(string.Join(' ', Enumerable.Repeat("Synthetic natural sentence, with another clause。", 12)));
        AdmitRuntimeRecoveryFixture(f);
        var articles = f.CreateArticles(); var spend = new GoogleGenerationSpendAuthorizationStore(f, TimeProvider.System, articles: articles);
        var owner = new GoogleGenerationProductionSubmissionStateOwner(spend);
        var preparation = new ChirpArticlePreparationService(articles, f.ChirpCompiler, owner, TimeProvider.System);
        var requests = new GoogleGenerationProductionRuntimeRequestSource(spend, owner.ResolveCurrentAsync);
        var persisted = new GoogleGenerationPersistedQueueStateStore(f); var observations = new RuntimeRecoveryObservations(mode);
        var native = new WholeNative();
        var exporter = new ArticleAudioExportService(f, new EfDocumentLibrary(f, new DocumentContentStore(f.WholePaths), TimeProvider.System),
            persisted, f.WholePaths, native, chirpArticles: articles);
        string tool = Path.Combine(f.WholeRoot, "ffmpeg.exe"), destination = Path.Combine(f.WholeRoot, "recovered.mp3");
        await File.WriteAllBytesAsync(tool, [0], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(f.WholeRoot, "ffprobe.exe"), [0], TestContext.Current.CancellationToken);
        var runtime = new ChirpWholeArticleRuntime(preparation, articles, requests,
            (request, _) => RecoveryExecutionContext(articles, persisted, request, observations), exporter, f.Selection, destination, tool,
            observeStage: (stage, _) =>
            {
                if (string.Equals(mode, "output-changed-before-child", StringComparison.Ordinal)
                    && stage == CloudScribe.Application.Logging.ArticleExecutionStage.Preparing && observations.RootFailures > 0)
                    Directory.CreateDirectory(destination);
            });
        var review = await articles.PrepareWholeArticleAsync(f.Selection.DocumentId, TestContext.Current.CancellationToken);
        var job = await articles.StartWholeArticleAsync(new(review.PreviewId, review.ReviewSha256, review.MaximumAdditionalMinorUnits, true, true), TestContext.Current.CancellationToken);
        if (string.Equals(mode, "accepted-children", StringComparison.Ordinal))
        {
            var done = await articles.RunWholeArticleAsync(job.JobId, runtime, token: TestContext.Current.CancellationToken);
            Assert.Equal(ChirpWholeArticleState.Complete, done.State); Assert.True(native.FullDecode);
            Assert.Equal(f.Selection.ExactText, string.Concat(observations.AcceptedText));
            Assert.Equal(2, observations.ChildCalls); Assert.Equal(1, observations.RootFailures);
            Assert.True(File.Exists(destination));
        }
        else
        {
            await Assert.ThrowsAnyAsync<Exception>(() => articles.RunWholeArticleAsync(job.JobId, runtime, token: TestContext.Current.CancellationToken));
            Assert.False(File.Exists(destination));
            Assert.Equal(mode is "generic-root" or "output-changed-before-child" ? 0 : 1, observations.ChildCalls);
            if (string.Equals(mode, "output-changed-before-child", StringComparison.Ordinal))
            {
                using var db = f.CreateDbContext();
                Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM chirp_article_split_attempts").SingleAsync(TestContext.Current.CancellationToken));
                Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM chirp_article_attempts").SingleAsync(TestContext.Current.CancellationToken));
            }
            Assert.Equal(ChirpWholeArticleState.Stopped, (await articles.ReadWholeArticleJobAsync(job.JobId, TestContext.Current.CancellationToken)).State);
        }
        Assert.Equal(observations.Keys.Count, observations.Keys.Distinct(StringComparer.Ordinal).Count());
    }

    private static void AdmitRuntimeRecoveryFixture(Fixture f)
    {
        string path = Path.Combine(f.WholeRoot, "install", "chirp-article-admission-policy.json");
        var policy = JsonSerializer.Deserialize<ChirpArticleAdmissionPolicy>(File.ReadAllBytes(path))!;
        var s = f.Selection;
        var document = new DocumentSnapshot(s.DocumentId, "Synthetic recovery route", s.ExactText, 0, 0, DocumentStatus.Active, false, s.RevisionId!.Value, null, null, 0);
        var proposal = ChirpArticlePlanCompiler.CompileSentences(document, s.LanguageCode, s.VoiceStableId, inputFormat: new(1));
        int maximum = proposal.Parts.Count * 2 + 6;
        var original = policy.Articles[0];
        var article = original with
        {
            Proposal = proposal,
            Lineage = ChirpPlanLineageMapper.Map(s.ExactText, proposal.Parts.Select(x => x.Range).ToArray(), original.Lineage.History),
            AllowedSegments = proposal.Parts.Select(x => new ChirpSegmentAllowance(x.CoverageOrdinal, 2)).ToArray()
        };
        policy = policy with
        {
            SchemaVersion = 3,
            PriorSuccessorAttemptRows = [],
            Articles = [article],
            MaximumNewAttempts = proposal.Parts.Count + 6,
            MaximumAdditionalReservationMinorUnits = maximum,
            WholeArticleGeneration = new(Guid.NewGuid(), [proposal.PlanSha256]),
            SentenceRecovery = new(1, Guid.NewGuid(), ChirpBoundedSentenceSplit.Version, 2, 6, 6, 6, 6, []),
            Budget = policy.Budget with
            {
                ProjectCeilingMinorUnits = policy.Budget.ProjectCommittedMinorUnits + maximum,
                AllocationCeilingMinorUnits = policy.Budget.AllocationCommittedMinorUnits + maximum
            }
        };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(policy); File.WriteAllBytes(path, bytes);
        File.WriteAllText(Path.Combine(f.WholeRoot, "install", "cloudscribe-install.json"), JsonSerializer.Serialize(new
        { Product = "CloudScribe Pro", AppSha256 = policy.AppSha256, ChirpArticlePolicySha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) }));
    }

    private static Task<GoogleGenerationUiExecutionContext> RecoveryExecutionContext(ChirpArticleService articles,
        GoogleGenerationPersistedQueueStateStore persisted, GoogleGenerationProductionRuntimeRequest request, RuntimeRecoveryObservations observations)
    {
        var queue = new GoogleGenerationQueueCoordinator((compiled, _, _) => Task.FromResult(observations.Respond(compiled)));
        var bound = new GoogleGenerationBoundQueueCoordinator(queue, persisted.LoadAsync, persisted.SaveAsync,
            (claim, token) => articles.TryClaimAuthorizedAsync(request.SubmissionEnvelope, "synthetic-project", "USD", 2, 1, claim, token), persisted.SaveOutcomeAsync);
        return Task.FromResult(new GoogleGenerationUiExecutionContext(new(bound), request.Snapshot));
    }

    private sealed class RuntimeRecoveryObservations(string mode)
    {
        public List<string> Keys { get; } = [];
        public List<string> AcceptedText { get; } = [];
        public int ChildCalls { get; private set; }
        public int RootFailures { get; private set; }
        public GenerationProviderResponse Respond(GenerationProviderRequest request)
        {
            Keys.Add(request.IdempotencyKey);
            bool child = request.IdempotencyKey.Split(':').Length > 3;
            if (child) ChildCalls++;
            if (!child && RootFailures == 0)
            {
                RootFailures++;
                return new(SubmissionDisposition.RejectedSafeToRetry, null, ReadOnlyMemory<byte>.Empty, null, null, "google-http-400",
                    GoogleErrorStatus.InvalidArgument, string.Equals(mode, "generic-root", StringComparison.Ordinal)
                        ? GoogleValidationMessageHint.NotProvided : GoogleValidationMessageHint.SentenceLengthReported);
            }
            if (child && string.Equals(mode, "uncertain-child", StringComparison.Ordinal))
                return new(SubmissionDisposition.UnknownRequiresReconciliation, null, ReadOnlyMemory<byte>.Empty, null, null, "google-transport-unknown");
            using var payload = JsonDocument.Parse(request.CompiledPayload);
            AcceptedText.Add(GoogleSpeechPlanCompiler.ReadSentenceMarkedText(payload.RootElement.GetProperty("input").GetProperty("ssml").GetString()!));
            byte[] audio = new byte[128]; Encoding.ASCII.GetBytes("ID3").CopyTo(audio, 0);
            return new(SubmissionDisposition.Accepted, null, audio, "audio/mpeg", null, "google-accepted");
        }
    }
}
