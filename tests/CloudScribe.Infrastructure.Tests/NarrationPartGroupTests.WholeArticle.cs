using System.Text.Json;
using CloudScribe.Infrastructure.Generation;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Fact]
    public async Task WholeArticleCannotInferAuthorityFromExistingRequestPolicy()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await ChirpEvidenceAsync(f);
        await Assert.ThrowsAsync<InvalidOperationException>(() => e.Service.PrepareWholeArticleAsync(e.Document.Id, TestContext.Current.CancellationToken));
        Assert.Empty(await FinancialRowsAsync(f, "chirp_whole_article_jobs"));
        Assert.Empty(await FinancialRowsAsync(f, "chirp_article_attempts"));
        var policy = e.Admission.Policy! with { WholeArticleGeneration = new(Guid.NewGuid(), [e.Article.Proposal.PlanSha256]) };
        Assert.Throws<InvalidDataException>(() => ChirpPolicyValidation.RequireValid(policy, e.Clock.GetUtcNow()));
    }

    [Fact]
    public async Task WholeArticleReviewIsReadOnlyAndOneConsentCreatesOneDurableJob()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await WholeEvidenceAsync(f);
        var a = await e.Service.PrepareWholeArticleAsync(e.Document.Id, TestContext.Current.CancellationToken);
        var b = await e.Service.PrepareWholeArticleAsync(e.Document.Id, TestContext.Current.CancellationToken);
        Assert.Equal(e.Article.Proposal.Parts.Count, a.RemainingRequests);
        Assert.Equal(a.RemainingRequests, a.MaximumAdditionalMinorUnits);
        Assert.Equal(148, a.RetainedCommitmentsMinorUnits);
        Assert.Empty(await FinancialRowsAsync(f, "chirp_article_attempts"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => e.Service.StartWholeArticleAsync(WholeConfirmation(a) with { ConfirmedByUser = false }, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => e.Service.StartWholeArticleAsync(WholeConfirmation(a), TestContext.Current.CancellationToken));
        var job = await e.Service.StartWholeArticleAsync(WholeConfirmation(b), TestContext.Current.CancellationToken);
        var c = await e.Service.PrepareWholeArticleAsync(e.Document.Id, TestContext.Current.CancellationToken);
        Assert.Equal(job, await e.Service.StartWholeArticleAsync(WholeConfirmation(c), TestContext.Current.CancellationToken));
        Assert.Equal(job, await new ChirpArticleService(f, e.Admission, f.Paths, e.Clock).ReadWholeArticleJobAsync(job.JobId, TestContext.Current.CancellationToken));
        Assert.Empty(await FinancialRowsAsync(f, "chirp_article_attempts"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => e.Service.StartWholeArticleAsync(WholeConfirmation(a), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WholeArticleUsesExistingLedgerClaimReceiptsAndExportsOnlyCompleteOrderedCoverage()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await WholeEvidenceAsync(f);
        var review = await e.Service.PrepareWholeArticleAsync(e.Document.Id, TestContext.Current.CancellationToken);
        var job = await e.Service.StartWholeArticleAsync(WholeConfirmation(review), TestContext.Current.CancellationToken);
        var runtime = new WholeFakeRuntime(f, e);
        var result = await e.Service.RunWholeArticleAsync(job.JobId, runtime, token: TestContext.Current.CancellationToken);
        Assert.Equal(ChirpWholeArticleState.Complete, result.State);
        Assert.Equal(Enumerable.Range(1, review.RemainingRequests), runtime.Ordinals);
        Assert.True(runtime.Exported);
        Assert.Equal(review.RemainingRequests, (await FinancialRowsAsync(f, "chirp_article_attempts")).Count);
        Assert.Equal(review.RemainingRequests, (await FinancialRowsAsync(f, "chirp_article_receipts")).Count);
        var restarted = new ChirpArticleService(f, e.Admission, f.Paths, e.Clock);
        Assert.Equal(result, await restarted.RunWholeArticleAsync(job.JobId, runtime, token: TestContext.Current.CancellationToken));
        Assert.Equal(review.RemainingRequests, runtime.Ordinals.Count); // No accepted request was submitted twice.
        Assert.Equal(e.Historical, await new GoogleGenerationPersistedQueueStateStore(f).LoadAsync(e.Historical.AccountId,
            e.Historical.OperationStableId, e.Historical.IdempotencyKey, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WholeArticleFailureRetainsReservationAndStopsWithoutNextRequestOrAutomaticReplay()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await WholeEvidenceAsync(f);
        var review = await e.Service.PrepareWholeArticleAsync(e.Document.Id, TestContext.Current.CancellationToken);
        var job = await e.Service.StartWholeArticleAsync(WholeConfirmation(review), TestContext.Current.CancellationToken);
        var runtime = new WholeFakeRuntime(f, e) { FailAfterReservation = true };
        await Assert.ThrowsAsync<IOException>(() => e.Service.RunWholeArticleAsync(job.JobId, runtime, token: TestContext.Current.CancellationToken));
        var stopped = await e.Service.ReadWholeArticleJobAsync(job.JobId, TestContext.Current.CancellationToken);
        Assert.Equal(ChirpWholeArticleState.Stopped, stopped.State);
        Assert.False(runtime.Exported);
        Assert.Single(await FinancialRowsAsync(f, "chirp_article_attempts"));
        Assert.Empty(await FinancialRowsAsync(f, "chirp_article_receipts"));
        Assert.Equal(stopped, await e.Service.RunWholeArticleAsync(job.JobId, runtime, token: TestContext.Current.CancellationToken));
        Assert.Single(runtime.Ordinals);
        Assert.DoesNotContain("SYNTHETIC_PRIVATE_ERROR_NOT_PERSISTED", JsonSerializer.Serialize(await FinancialRowsAsync(f, "chirp_whole_article_events")), StringComparison.Ordinal);
        var row = Assert.Single(await FinancialRowsAsync(f, "chirp_article_attempts"));
        var approved = new ChirpApprovedAttempt(Guid.ParseExact((string)row[0]!, "N"), (string)row[7]!, (long)row[13]!);
        var intent = ChirpIntent(e);
        await Assert.ThrowsAsync<InvalidOperationException>(() => e.Service.TryClaimAuthorizedAsync(intent.Envelope with { ChirpAuthorizationId = approved.AuthorizationId },
            intent.ProjectId, intent.Currency, intent.Scale, intent.EstimateMinorUnits, ChirpClaim(intent, approved), TestContext.Current.CancellationToken));
        Assert.Equal(0L, (await FinancialRowsAsync(f, "chirp_article_attempts"))[0][16]);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("rollback")]
    [InlineData("budget")]
    [InlineData("account")]
    public async Task WholeArticleReviewFailsClosedBeforeAnyReservation(string failure)
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await WholeEvidenceAsync(f);
        var review = await e.Service.PrepareWholeArticleAsync(e.Document.Id, TestContext.Current.CancellationToken);
        if (string.Equals(failure, "expired", StringComparison.Ordinal)) e.Clock.Now = review.ExpiresAtUtc.AddTicks(1);
        if (string.Equals(failure, "rollback", StringComparison.Ordinal)) e.Clock.Now -= TimeSpan.FromSeconds(1);
        if (string.Equals(failure, "budget", StringComparison.Ordinal)) e.Admission.Policy = e.Admission.Policy! with { MaximumAdditionalReservationMinorUnits = 1 };
        if (string.Equals(failure, "account", StringComparison.Ordinal)) e.Admission.Policy = e.Admission.Policy! with { AccountId = "changed" };
        await Assert.ThrowsAnyAsync<Exception>(() => e.Service.StartWholeArticleAsync(WholeConfirmation(review), TestContext.Current.CancellationToken));
        Assert.Empty(await FinancialRowsAsync(f, "chirp_whole_article_jobs"));
        Assert.Empty(await FinancialRowsAsync(f, "chirp_article_attempts"));
    }

    [Fact]
    public async Task WholeArticleRequestRequiresDurableExecutingJobNotCallerSuppliedJobId()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await WholeEvidenceAsync(f);
        var request = await e.Service.PrepareAsync(ChirpIntent(e), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() => e.Service.ApproveWholeArticleRequestAsync(Guid.NewGuid(), request.PreviewId, TestContext.Current.CancellationToken));
        request = await e.Service.PrepareAsync(ChirpIntent(e), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => e.Service.ApproveAsync(ChirpConfirmation(request), TestContext.Current.CancellationToken));
        Assert.Empty(await FinancialRowsAsync(f, "chirp_article_attempts"));
    }

    [Fact]
    public async Task WholeArticleInterruptedActiveRequestIsNotBlindlyReplayedAfterRestart()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await WholeEvidenceAsync(f);
        var review = await e.Service.PrepareWholeArticleAsync(e.Document.Id, TestContext.Current.CancellationToken);
        var job = await e.Service.StartWholeArticleAsync(WholeConfirmation(review), TestContext.Current.CancellationToken);
        job = job with { State = ChirpWholeArticleState.Executing, ActiveOrdinal = 1 };
        using (var db = f.CreateDbContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE chirp_whole_article_jobs SET JobJson={JsonSerializer.Serialize(job)},JobSha256={ChirpPolicyValidation.Hash(job)} WHERE JobId={job.JobId.ToString("N")};", TestContext.Current.CancellationToken);
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO chirp_whole_article_events VALUES({job.JobId.ToString("N")},2,{JsonSerializer.Serialize(job)},{ChirpPolicyValidation.Hash(job)});", TestContext.Current.CancellationToken);
        }
        var runtime = new WholeFakeRuntime(f, e);
        var result = await new ChirpArticleService(f, e.Admission, f.Paths, e.Clock).RunWholeArticleAsync(job.JobId, runtime, token: TestContext.Current.CancellationToken);
        Assert.Equal(ChirpWholeArticleState.Stopped, result.State);
        Assert.Empty(runtime.Ordinals);
        Assert.Empty(await FinancialRowsAsync(f, "chirp_article_attempts"));
    }

    [Fact]
    public async Task WholeArticleRefinedPlanPreservesFailedHoldAndReusesAcceptedAudio()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var refined = await RefinedEvidenceAsync(f);
        var article = refined.Article with
        {
            AllowedSegments = refined.Article.Proposal.Parts
            .Where(x => x.Range.ReusedReceiptSha256 is null).Select(x => new ChirpSegmentAllowance(x.CoverageOrdinal, 1)).ToArray()
        };
        var e = refined.Evidence with { Article = article };
        e.Admission.Policy = e.Admission.Policy! with
        {
            SchemaVersion = 3,
            Articles = [article],
            MaximumNewAttempts = article.AllowedSegments.Count,
            MaximumAdditionalReservationMinorUnits = article.AllowedSegments.Count,
            WholeArticleGeneration = new(Guid.NewGuid(), [article.Proposal.PlanSha256])
        };
        var review = await e.Service.PrepareWholeArticleAsync(e.Document.Id, TestContext.Current.CancellationToken);
        Assert.Equal(1, review.AcceptedParts);
        var job = await e.Service.StartWholeArticleAsync(WholeConfirmation(review), TestContext.Current.CancellationToken);
        var runtime = new WholeFakeRuntime(f, e);
        Assert.Equal(ChirpWholeArticleState.Complete, (await e.Service.RunWholeArticleAsync(job.JobId, runtime, token: TestContext.Current.CancellationToken)).State);
        Assert.Equal(article.AllowedSegments.Select(x => x.CoverageOrdinal), runtime.Ordinals);
        Assert.Equal(refined.AcceptedBytes, await File.ReadAllBytesAsync(refined.Reuse.Audio.AudioPath, TestContext.Current.CancellationToken));
        Assert.Equal(refined.FailedState, await new GoogleGenerationPersistedQueueStateStore(f).LoadAsync(refined.FailedState.AccountId,
            refined.FailedState.OperationStableId, refined.FailedState.IdempotencyKey, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WholeArticleCompletedFileIsCheckedAfterRestartAndNeverRegeneratedWhenMissing()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await WholeEvidenceAsync(f);
        var review = await e.Service.PrepareWholeArticleAsync(e.Document.Id, TestContext.Current.CancellationToken);
        var job = await e.Service.StartWholeArticleAsync(WholeConfirmation(review), TestContext.Current.CancellationToken);
        var runtime = new WholeFakeRuntime(f, e);
        job = await e.Service.RunWholeArticleAsync(job.JobId, runtime, token: TestContext.Current.CancellationToken);
        Assert.NotNull(job.Output);
        await File.WriteAllBytesAsync(job.Output.Path, [0], TestContext.Current.CancellationToken);
        int calls = runtime.Ordinals.Count;
        await Assert.ThrowsAsync<InvalidDataException>(() => e.Service.RunWholeArticleAsync(job.JobId, runtime, token: TestContext.Current.CancellationToken));
        Assert.Equal(calls, runtime.Ordinals.Count);
        Assert.Equal(ChirpWholeArticleState.Stopped, (await e.Service.ReadWholeArticleJobAsync(job.JobId, TestContext.Current.CancellationToken)).State);
    }

    private static async Task<ChirpEvidence> WholeEvidenceAsync(Fixture f)
    {
        var e = await ChirpEvidenceAsync(f, 8).ConfigureAwait(false);
        e.Admission.Policy = e.Admission.Policy! with
        {
            SchemaVersion = 3,
            PriorSuccessorAttemptRows = [],
            MaximumNewAttempts = e.Article.AllowedSegments.Count,
            MaximumAdditionalReservationMinorUnits = e.Article.AllowedSegments.Count,
            WholeArticleGeneration = new(Guid.NewGuid(), [e.Article.Proposal.PlanSha256])
        };
        return e;
    }

    private static ChirpWholeArticleConfirmation WholeConfirmation(ChirpWholeArticleReview r) => new(r.PreviewId, r.ReviewSha256, r.MaximumAdditionalMinorUnits, true, true);

    private sealed class WholeFakeRuntime(Fixture fixture, ChirpEvidence evidence) : IChirpWholeArticleRuntime
    {
        public List<int> Ordinals { get; } = [];
        public Action<ReadOnlyMemory<byte>>? ObservePayload { get; init; }
        public bool Exported { get; private set; }
        public bool FailAfterReservation { get; init; }
        public bool RejectAfterReservation { get; init; }
        public bool RequireRefreshBeforeEveryRequest { get; init; }
        public bool RequiresCatalogRefresh { get; private set; }
        public int Refreshes { get; private set; }
        public void ExpireCatalog() => RequiresCatalogRefresh = true;
        public Task RefreshCatalogAsync(CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Refreshes++; RequiresCatalogRefresh = false; return Task.CompletedTask; }
        public async Task GenerateAsync(ChirpWholeArticleJob job, int ordinal, CancellationToken cancellationToken)
        {
            Ordinals.Add(ordinal);
            var intent = ChirpIntent(evidence, ordinal);
            if (evidence.Article.Proposal.InputFormat is not null)
            {
                using var wire = System.Text.Json.JsonDocument.Parse(intent.CompiledPayload);
                var part = evidence.Article.Proposal.Parts.Single(x => x.CoverageOrdinal == ordinal);
                Assert.False(wire.RootElement.GetProperty("input").TryGetProperty("text", out _));
                Assert.Equal(CloudScribe.Application.Generation.ChirpNarrationPlanner.Slice(evidence.Document.Text, part.Range),
                    System.Xml.Linq.XElement.Parse(wire.RootElement.GetProperty("input").GetProperty("ssml").GetString()!, System.Xml.Linq.LoadOptions.PreserveWhitespace).Value);
            }
            var review = await evidence.Service.PrepareAsync(intent, cancellationToken).ConfigureAwait(false);
            var approved = await evidence.Service.ApproveWholeArticleRequestAsync(job.JobId, review.PreviewId, cancellationToken).ConfigureAwait(false);
            if (RejectAfterReservation)
            {
                var claim = ChirpClaim(intent, approved);
                Assert.True(await evidence.Service.TryClaimAuthorizedAsync(intent.Envelope with { ChirpAuthorizationId = approved.AuthorizationId },
                    intent.ProjectId, "USD", 2, 1, claim, cancellationToken).ConfigureAwait(false));
                await new GoogleGenerationPersistedQueueStateStore(fixture, evidence.Admission).SaveOutcomeAsync(claim with
                { UnresolvedSubmission = false, DiagnosticCode = "google-http-400", ErrorStatus = CloudScribe.Domain.Generation.GoogleErrorStatus.InvalidArgument }, cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException("Synthetic terminal HTTP400; no retry.");
            }
            if (FailAfterReservation) throw new IOException("SYNTHETIC_PRIVATE_ERROR_NOT_PERSISTED");
            ObservePayload?.Invoke(intent.CompiledPayload);
            var accepted = await AcceptChirpAsync(fixture, evidence.Service, intent, ordinal, approved).ConfigureAwait(false);
            await evidence.Service.PersistAcceptedAsync(accepted.Request, accepted.Outcome, cancellationToken).ConfigureAwait(false);
            if (RequireRefreshBeforeEveryRequest) RequiresCatalogRefresh = true;
        }
        public async Task<ChirpWholeArticleOutput> ExportAsync(ChirpWholeArticleJob job, CancellationToken cancellationToken)
        {
            Assert.True((await evidence.Service.ProgressAsync(job.DocumentId, cancellationToken).ConfigureAwait(false)).Complete);
            Exported = true;
            string path = Path.Combine(fixture.Root, "synthetic-whole-article.mp3");
            byte[] bytes = new byte[128];
            await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
            return new(path, bytes.LongLength, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)));
        }
    }
}
