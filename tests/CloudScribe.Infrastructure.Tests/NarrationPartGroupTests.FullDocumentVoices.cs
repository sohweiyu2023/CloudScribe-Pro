using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudScribe.Application.Documents;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Theory]
    [InlineData("en-US-Neural2-D")]
    [InlineData("en-US-Chirp3-HD-Aoede")]
    public async Task FullDocumentVoicesUseExactRequestsDurableApprovalsAndOrderedAssemblyWithoutReplay(string voice)
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await FullDocumentEvidenceAsync(f, voice);
        var review = await e.Service.PrepareWholeArticleAsync(e.Document.Id, TestContext.Current.CancellationToken);
        Assert.Empty(await FinancialRowsAsync(f, "chirp_article_attempts"));
        var job = await e.Service.StartWholeArticleAsync(WholeConfirmation(review), TestContext.Current.CancellationToken);
        var runtime = new FullDocumentFakeRuntime(f, e);
        job = await e.Service.RunWholeArticleAsync(job.JobId, runtime, token: TestContext.Current.CancellationToken);
        Assert.Equal(ChirpWholeArticleState.Complete, job.State);
        Assert.Equal(e.Document.Text, string.Concat(runtime.Texts));
        Assert.Equal(e.Article.Proposal.Parts.Count, runtime.Texts.Count);
        Assert.Equal(Enumerable.Range(1, e.Article.Proposal.Parts.Count).Select(x => (byte)x), runtime.Native.InputMarkers);
        Assert.Equal(e.Article.AllowedSegments.Sum(x => x.MaximumMinorUnits), review.MaximumAdditionalMinorUnits);
        Assert.Equal(e.Article.Proposal.Parts.Count, (await FinancialRowsAsync(f, "chirp_article_attempts")).Count);
        Assert.Equal(e.Article.Proposal.Parts.Count, (await FinancialRowsAsync(f, "chirp_article_receipts")).Count);
        var restarted = new ChirpArticleService(f, e.Admission, f.Paths, e.Clock);
        int requests = runtime.Texts.Count;
        Assert.Equal(job, await restarted.RunWholeArticleAsync(job.JobId, runtime, token: TestContext.Current.CancellationToken));
        Assert.Equal(requests, runtime.Texts.Count);
        e.Admission.Policy = null;
        Assert.Equal(job.Output, await restarted.InspectCompletedWholeArticleOutputAsync(job.JobId, TestContext.Current.CancellationToken));
        using var db = f.CreateDbContext();
        Assert.Equal(1, await db.Documents.CountAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Neural2FailedOrUncertainAttemptKeepsHoldAndNeverRetriesOrSubdivides(bool uncertain)
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await FullDocumentEvidenceAsync(f, "en-US-Neural2-D");
        var review = await e.Service.PrepareWholeArticleAsync(e.Document.Id, TestContext.Current.CancellationToken);
        var job = await e.Service.StartWholeArticleAsync(WholeConfirmation(review), TestContext.Current.CancellationToken);
        var runtime = new FullDocumentFakeRuntime(f, e) { FailAfterClaim = true, Uncertain = uncertain };
        await Assert.ThrowsAsync<IOException>(() => e.Service.RunWholeArticleAsync(job.JobId, runtime, token: TestContext.Current.CancellationToken));
        var stopped = await e.Service.ReadWholeArticleJobAsync(job.JobId, TestContext.Current.CancellationToken);
        Assert.Equal(ChirpWholeArticleState.Stopped, stopped.State);
        Assert.Single(runtime.Texts); Assert.Empty(runtime.Native.InputMarkers);
        var attempt = Assert.Single(await FinancialRowsAsync(f, "chirp_article_attempts"));
        Assert.Equal(e.Article.AllowedSegments[0].MaximumMinorUnits, attempt[13]);
        Assert.Equal(1L, attempt[16]);
        Assert.Empty(await FinancialRowsAsync(f, "chirp_article_split_attempts"));
        Assert.Empty(await FinancialRowsAsync(f, "chirp_article_receipts"));
        var restarted = new ChirpArticleService(f, e.Admission, f.Paths, e.Clock);
        Assert.Equal(stopped, await restarted.RunWholeArticleAsync(job.JobId, runtime, token: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.PrepareWholeArticleAsync(e.Document.Id, TestContext.Current.CancellationToken));
        Assert.Single(runtime.Texts);
    }

    [Fact]
    public async Task Neural2UnconsumedReservationResumesSameExactApprovalAfterRestart()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await FullDocumentEvidenceAsync(f, "en-US-Neural2-D", whole: false);
        var intent = FullDocumentIntent(e, 1);
        var review = await e.Service.PrepareAsync(intent, TestContext.Current.CancellationToken);
        var approved = await e.Service.ApproveAsync(ChirpConfirmation(review), TestContext.Current.CancellationToken);
        var restarted = new ChirpArticleService(f, e.Admission, f.Paths, e.Clock);
        var resumed = await restarted.PrepareAsync(intent, TestContext.Current.CancellationToken);
        Assert.Equal(approved.AuthorizationId, resumed.ExistingAuthorizationId);
        Assert.Equal(approved, await restarted.ApproveAsync(ChirpConfirmation(resumed), TestContext.Current.CancellationToken));
        var accepted = await AcceptChirpAsync(f, restarted, intent, 1, approved);
        await restarted.PersistAcceptedAsync(accepted.Request, accepted.Outcome, TestContext.Current.CancellationToken);
        Assert.Single(await FinancialRowsAsync(f, "chirp_article_attempts"));
        Assert.Equal(1, (await restarted.ProgressAsync(e.Document.Id, TestContext.Current.CancellationToken)).SavedCoverageParts);
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.PrepareAsync(intent, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Neural2InterruptedAcceptedOutputRecoversLocallyWithoutPaidAuthority()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await FullDocumentEvidenceAsync(f, "en-US-Neural2-D", whole: false);
        var accepted = await AcceptChirpAsync(f, e.Service, FullDocumentIntent(e, 1), 1);
        using (var db = f.CreateDbContext())
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER synthetic_neural_receipt BEFORE INSERT ON chirp_article_receipts BEGIN SELECT RAISE(ABORT,'synthetic-interruption'); END;", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<SqliteException>(() => e.Service.PersistAcceptedAsync(accepted.Request, accepted.Outcome, TestContext.Current.CancellationToken));
        Assert.Empty(await FinancialRowsAsync(f, "chirp_article_receipts"));
        using (var db = f.CreateDbContext())
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER synthetic_neural_receipt;", TestContext.Current.CancellationToken);
        e.Admission.Policy = null; e.Clock.Now += TimeSpan.FromHours(2);
        var restarted = new ChirpArticleService(f, e.Admission, f.Paths, e.Clock);
        Assert.Equal(1, await restarted.RecoverLocalOutputsAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await restarted.RecoverLocalOutputsAsync(TestContext.Current.CancellationToken));
        var file = Assert.Single(Directory.GetFiles(Path.Combine(f.Root, "accepted-chirp-article-audio"), "*.mp3"));
        Assert.Equal(accepted.Outcome.Response!.MediaBytes.ToArray(), await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken));
        Assert.Single(await FinancialRowsAsync(f, "chirp_article_attempts")); Assert.Single(await FinancialRowsAsync(f, "chirp_article_receipts"));
    }

    [Fact]
    public async Task Neural2CurrentVoicePayloadAndEstimateCannotChangeAfterReview()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await FullDocumentEvidenceAsync(f, "en-US-Neural2-D", whole: false);
        var intent = FullDocumentIntent(e, 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => e.Service.PrepareAsync(intent with
        { Envelope = intent.Envelope with { VoiceName = "en-US-Chirp3-HD-Aoede" } }, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => e.Service.PrepareAsync(intent with
        { EstimateMinorUnits = intent.EstimateMinorUnits + 1 }, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => e.Service.PrepareAsync(intent with
        { CompiledPayload = Encoding.UTF8.GetBytes("changed") }, TestContext.Current.CancellationToken));
        var review = await e.Service.PrepareAsync(intent, TestContext.Current.CancellationToken);
        var approved = await e.Service.ApproveAsync(ChirpConfirmation(review), TestContext.Current.CancellationToken);
        Assert.Null(await e.Service.LoadSpendAsync(intent.Envelope with
        { ChirpAuthorizationId = approved.AuthorizationId, PricingProvenanceId = "different-family-price" }, TestContext.Current.CancellationToken));
        Assert.NotNull(await e.Service.LoadSpendAsync(intent.Envelope with { ChirpAuthorizationId = approved.AuthorizationId }, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("legacy-version")]
    [InlineData("chirp-voice")]
    [InlineData("sentence-format")]
    [InlineData("refinement")]
    [InlineData("sentence-successor")]
    [InlineData("diagnostic-grant")]
    public async Task Neural2PolicyRejectsBorrowedChirpVersionsFormatsAndDiagnosticAuthority(string mutation)
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await FullDocumentEvidenceAsync(f, "en-US-Neural2-D");
        var plan = e.Article.Proposal;
        plan = mutation switch
        {
            "legacy-version" => plan with { PlannerVersion = ChirpNarrationPlanner.Version },
            "chirp-voice" => plan with { VoiceName = "en-US-Chirp3-HD-Aoede" },
            "sentence-format" => plan with { InputFormat = new(1) },
            "refinement" => plan with { Refinement = new(plan, 1, Guid.NewGuid(), new string('a', 64), new string('b', 64), Guid.NewGuid().ToString("N")) },
            "sentence-successor" => plan with { SentenceSuccessor = new(plan) },
            _ => plan,
        };
        var policy = e.Admission.Policy! with { Articles = [e.Article with { Proposal = plan }] };
        if (string.Equals(mutation, "diagnostic-grant", StringComparison.Ordinal))
            policy = policy with
            {
                SuccessorGrant = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), plan.PlanSha256, 1,
                "chirp-article:" + plan.PlanSha256 + ":1", new string('a', 64), new string('b', 64), Guid.NewGuid().ToString("N"),
                "synthetic-reference", new string('c', 64), 1, 1)
            };
        var error = Assert.Throws<InvalidDataException>(() => ChirpPolicyValidation.RequireValid(policy, e.Clock.GetUtcNow()));
        Assert.Contains("Neural2 full-document admission", error.Message, StringComparison.Ordinal);
    }

    private static async Task<FullDocumentEvidence> FullDocumentEvidenceAsync(Fixture f, string voice, bool whole = true)
    {
        var document = await f.Library.CreateAsync("Synthetic full document", GoogleFullDocumentPlanCompilerTests.ComparisonText(), TestContext.Current.CancellationToken).ConfigureAwait(false);
        var proposal = ChirpArticlePlanCompiler.Compile(document, "en-US", voice);
        var history = new[] { new ChirpHistoricalRange(document.Id, document.CurrentRevisionId!.Value, 0, proposal.SourceScalars, proposal.SourceSha256, []) };
        var lineage = ChirpPlanLineageMapper.Map(document.Text, proposal.Parts.Select(x => x.Range).ToArray(), history);
        // Synthetic prices only. Production compilation must use its current authenticated family rate.
        decimal syntheticPrice = voice.Contains("-Neural2-", StringComparison.Ordinal) ? 16m : 30m;
        var allowances = proposal.Parts.Select(part => new ChirpSegmentAllowance(part.CoverageOrdinal,
            checked((long)decimal.Ceiling(syntheticPrice * (part.Range.EndScalarExclusive - part.Range.StartScalar) / 10_000m)))).ToArray();
        var article = new AdmittedChirpArticle(proposal, lineage, [], allowances);
        var clock = new ChirpClock();
        var baseline = new RecoveryBudgetBaseline(0, 200, 0, 200, new string('a', 64), [], [], [], []);
        var policy = new ChirpArticleAdmissionPolicy(whole ? 3 : 1, new string('a', 64), new string('b', 64), InstalledRecoveryAdmissionStore.ProfileHash(f.Root),
            "synthetic-account", "synthetic-project", "USD", 2, clock.GetUtcNow().AddHours(1), new string('c', 64), baseline,
            [], [], [article], allowances.Length, allowances.Sum(x => x.MaximumMinorUnits),
            PriorSuccessorAttemptRows: whole ? [] : null, WholeArticleGeneration: whole ? new(Guid.NewGuid(), [proposal.PlanSha256]) : null);
        var admission = new SyntheticChirpAdmission { Policy = policy };
        return new(document, article, admission, clock, new(f, admission, f.Paths, clock));
    }

    private static ChirpRequestIntent FullDocumentIntent(FullDocumentEvidence e, int ordinal)
    {
        var part = e.Article.Proposal.Parts.Single(x => x.CoverageOrdinal == ordinal);
        var wire = ChirpArticlePlanCompiler.CompilePart(e.Document.Text, e.Article.Proposal, part);
        var envelope = ChirpEnvelope(wire) with { VoiceName = e.Article.Proposal.VoiceName, PricingProvenanceId = "synthetic-current-family-price" };
        return new(e.Document.Id, ordinal, "synthetic-project", envelope, wire.Payload, "USD", 2,
            e.Article.AllowedSegments.Single(x => x.CoverageOrdinal == ordinal).MaximumMinorUnits);
    }

    private sealed record FullDocumentEvidence(DocumentSnapshot Document, AdmittedChirpArticle Article,
        SyntheticChirpAdmission Admission, ChirpClock Clock, ChirpArticleService Service);

    private sealed class FullDocumentFakeRuntime(Fixture fixture, FullDocumentEvidence evidence) : IChirpWholeArticleRuntime
    {
        public List<string> Texts { get; } = [];
        public ChirpArticleNative Native { get; } = new();
        public bool FailAfterClaim { get; init; }
        public bool Uncertain { get; init; }
        public bool RequiresCatalogRefresh => false;
        public Task RefreshCatalogAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("No catalog refresh was requested by this synthetic fixture.");

        public async Task GenerateAsync(ChirpWholeArticleJob job, int ordinal, CancellationToken cancellationToken)
        {
            var intent = FullDocumentIntent(evidence, ordinal);
            using var payload = JsonDocument.Parse(intent.CompiledPayload);
            Texts.Add(payload.RootElement.GetProperty("input").GetProperty("text").GetString()!);
            Assert.Equal(evidence.Article.Proposal.VoiceName, payload.RootElement.GetProperty("voice").GetProperty("name").GetString());
            Assert.Equal("en-US", payload.RootElement.GetProperty("voice").GetProperty("languageCode").GetString());
            Assert.InRange(intent.CompiledPayload.Length, 1, 5_000);
            var review = await evidence.Service.PrepareAsync(intent, cancellationToken).ConfigureAwait(false);
            var approved = await evidence.Service.ApproveWholeArticleRequestAsync(job.JobId, review.PreviewId, cancellationToken).ConfigureAwait(false);
            if (FailAfterClaim)
            {
                var claim = ChirpClaim(intent, approved);
                Assert.True(await evidence.Service.TryClaimAuthorizedAsync(intent.Envelope with { ChirpAuthorizationId = approved.AuthorizationId },
                    intent.ProjectId, intent.Currency, intent.Scale, intent.EstimateMinorUnits, claim, cancellationToken).ConfigureAwait(false));
                if (!Uncertain)
                    await new GoogleGenerationPersistedQueueStateStore(fixture, evidence.Admission).SaveOutcomeAsync(claim with
                    { UnresolvedSubmission = false, DiagnosticCode = "google-http-400", ErrorStatus = GoogleErrorStatus.InvalidArgument }, cancellationToken).ConfigureAwait(false);
                throw new IOException("Synthetic terminal or uncertain failure; no retry.");
            }
            var accepted = await AcceptChirpAsync(fixture, evidence.Service, intent, ordinal, approved,
                submitted => Assert.True(submitted.CompiledPayload.Span.SequenceEqual(intent.CompiledPayload.Span))).ConfigureAwait(false);
            await evidence.Service.PersistAcceptedAsync(accepted.Request, accepted.Outcome, cancellationToken).ConfigureAwait(false);
        }

        public async Task<ChirpWholeArticleOutput> ExportAsync(ChirpWholeArticleJob job, CancellationToken cancellationToken)
        {
            var exporter = new ArticleAudioExportService(fixture, fixture.Library, new GoogleGenerationPersistedQueueStateStore(fixture),
                fixture.Paths, Native, chirpArticles: evidence.Service);
            string path = Path.Combine(fixture.Root, "synthetic-full-document.mp3");
            await exporter.ExportAsync(job.DocumentId, path, CreateArticleTools(fixture.Root), cancellationToken).ConfigureAwait(false);
            byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            return new(path, bytes.LongLength, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
    }
}
