using System.Text.Json;
using CloudScribe.Application.Generation;
using CloudScribe.Infrastructure.Generation;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Fact]
    public async Task SentenceSuccessorReusesTwoGenerationsAndCompletesExistingWholeArticleAfterRestart()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var fixture = await SentenceEvidenceAsync(f);
        var e = fixture.Evidence;
        Assert.Equal(2, (await e.Service.ProgressAsync(e.Document.Id, TestContext.Current.CancellationToken)).SavedCoverageParts);
        var review = await e.Service.PrepareWholeArticleAsync(e.Document.Id, TestContext.Current.CancellationToken);
        var job = await e.Service.StartWholeArticleAsync(WholeConfirmation(review), TestContext.Current.CancellationToken);
        var runtime = new WholeFakeRuntime(f, e);
        var completed = await e.Service.RunWholeArticleAsync(job.JobId, runtime, token: TestContext.Current.CancellationToken);
        Assert.Equal(ChirpWholeArticleState.Complete, completed.State); Assert.True(runtime.Exported);
        Assert.Equal(3, (await FinancialRowsAsync(f, "chirp_article_plans")).Count);
        Assert.Equal(fixture.FailedState, await new GoogleGenerationPersistedQueueStateStore(f).LoadAsync(fixture.FailedState.AccountId,
            fixture.FailedState.OperationStableId, fixture.FailedState.IdempotencyKey, TestContext.Current.CancellationToken));
        var restarted = new ChirpArticleService(f, e.Admission, f.Paths, e.Clock);
        int requests = runtime.Ordinals.Count;
        Assert.Equal(completed, await restarted.RunWholeArticleAsync(job.JobId, runtime, token: TestContext.Current.CancellationToken));
        Assert.Equal(requests, runtime.Ordinals.Count);
        e.Admission.Policy = null;
        Assert.True((await restarted.ProgressAsync(e.Document.Id, TestContext.Current.CancellationToken)).Complete);
    }

    [Theory]
    [InlineData("missing-inherited")]
    [InlineData("changed-inherited-audio")]
    [InlineData("missing-direct")]
    [InlineData("uncertain-parent")]
    [InlineData("unconsumed-parent")]
    [InlineData("failed-with-output-journal")]
    [InlineData("expired-policy")]
    [InlineData("insufficient-budget")]
    public async Task SentenceAdmissionFailsClosedWithoutNewFinancialWrites(string mutation)
    {
        using var f = new Fixture(); await f.InitializeAsync(); var fixture = await SentenceEvidenceAsync(f,
            reservePending: string.Equals(mutation, "unconsumed-parent", StringComparison.Ordinal)); var e = fixture.Evidence;
        var policy = e.Admission.Policy!; var article = e.Article;
        string rowsBefore = JsonHash(await FinancialRowsAsync(f, "chirp_article_attempts"));
        if (string.Equals(mutation, "missing-inherited", StringComparison.Ordinal)) e.Admission.Policy = policy with { Articles = [article with { ReusedAudio = article.ReusedAudio.Skip(1).ToArray() }] };
        if (string.Equals(mutation, "missing-direct", StringComparison.Ordinal)) e.Admission.Policy = policy with { Articles = [article with { ReusedAudio = article.ReusedAudio.Take(1).ToArray() }] };
        if (string.Equals(mutation, "changed-inherited-audio", StringComparison.Ordinal)) await File.WriteAllBytesAsync(article.ReusedAudio[0].Audio.AudioPath, [0], TestContext.Current.CancellationToken);
        if (string.Equals(mutation, "expired-policy", StringComparison.Ordinal)) e.Clock.Now = policy.ExpiresAtUtc;
        if (string.Equals(mutation, "insufficient-budget", StringComparison.Ordinal)) e.Admission.Policy = policy with { Budget = policy.Budget with { ProjectCeilingMinorUnits = policy.Budget.ProjectCommittedMinorUnits } };
        if (string.Equals(mutation, "uncertain-parent", StringComparison.Ordinal))
        {
            using var db = f.CreateDbContext();
            await db.Database.ExecuteSqlRawAsync("UPDATE google_generation_queue_states SET UnresolvedSubmission=1 WHERE IdempotencyKey={0}",
                [fixture.DirectKey], TestContext.Current.CancellationToken);
            rowsBefore = JsonHash(await FinancialRowsAsync(f, "chirp_article_attempts"));
        }
        if (string.Equals(mutation, "failed-with-output-journal", StringComparison.Ordinal))
        {
            using var db = f.CreateDbContext();
            await db.Database.ExecuteSqlRawAsync("UPDATE google_generation_queue_states SET DiagnosticCode='google-http-400',ErrorStatus=1 WHERE IdempotencyKey={0}",
                [fixture.DirectKey], TestContext.Current.CancellationToken);
        }
        if (mutation is "uncertain-parent" or "unconsumed-parent" or "failed-with-output-journal")
            e.Admission.Policy = policy with
            {
                PriorArticleAttemptRows = await RowPinsAsync(f, "chirp_article_attempts", 1),
                Budget = policy.Budget with { QueueRows = await RowPinsAsync(f, "google_generation_queue_states", 3) }
            };
        var error = await Record.ExceptionAsync(() => e.Service.PrepareWholeArticleAsync(e.Document.Id, TestContext.Current.CancellationToken));
        Assert.True(error is InvalidDataException or InvalidOperationException);
        if (string.Equals(mutation, "failed-with-output-journal", StringComparison.Ordinal)) Assert.Equal("Failed predecessor has accepted or recoverable output; never regenerate it.", error.Message);
        Assert.Equal(rowsBefore, JsonHash(await FinancialRowsAsync(f, "chirp_article_attempts")));
        Assert.Equal(2, (await FinancialRowsAsync(f, "chirp_article_plans")).Count);
    }

    private static async Task<SentenceFixture> SentenceEvidenceAsync(Fixture f, bool reservePending = false)
    {
        var previous = await RefinedEvidenceAsync(f, sentenceSource: true).ConfigureAwait(false); var e = previous.Evidence;
        var service = new ChirpArticleService(f, e.Admission, f.Paths, e.Clock);
        int ordinal = previous.Article.AllowedSegments[0].CoverageOrdinal;
        var segment = await service.ResolveSegmentAsync(e.Document.Id, ordinal, "synthetic-account", "synthetic-project",
            "cmn-CN-Chirp3-HD-Alnilam", "cmn-CN", TestContext.Current.CancellationToken).ConfigureAwait(false);
        var wire = CompileChirp(segment.ExactText);
        var intent = new ChirpRequestIntent(e.Document.Id, ordinal, "synthetic-project", ChirpEnvelope(wire), wire.Payload, "USD", 2, 1);
        var accepted = await AcceptChirpAsync(f, service, intent, 4).ConfigureAwait(false);
        await service.PersistAcceptedAsync(accepted.Request, accepted.Outcome, TestContext.Current.CancellationToken).ConfigureAwait(false);
        if (reservePending)
        {
            int nextOrdinal = previous.Article.Proposal.Parts.First(x => x.Range.ReusedReceiptSha256 is null && x.CoverageOrdinal != ordinal).CoverageOrdinal;
            var current = e.Admission.Policy!;
            e.Admission.Policy = current with
            {
                Articles = [previous.Article with { AllowedSegments = [new(ordinal, 1), new(nextOrdinal, 1)] }],
                MaximumNewAttempts = 2,
                MaximumAdditionalReservationMinorUnits = 2
            };
            // Existing consumed row pins name the original immutable policy; carry them into this separate synthetic admission.
            e.Admission.Policy = e.Admission.Policy with
            {
                PriorArticleAttemptRows = await RowPinsAsync(f, "chirp_article_attempts", 1).ConfigureAwait(false),
                Budget = current.Budget with { ProjectCommittedMinorUnits = 151, AllocationCommittedMinorUnits = 21, QueueRows = await RowPinsAsync(f, "google_generation_queue_states", 3).ConfigureAwait(false) }
            };
            var nextIntent = ChirpIntent(e with { Article = previous.Article }, nextOrdinal);
            var pendingReview = await service.PrepareAsync(nextIntent, TestContext.Current.CancellationToken).ConfigureAwait(false);
            _ = await service.ApproveAsync(ChirpConfirmation(pendingReview), TestContext.Current.CancellationToken).ConfigureAwait(false);
        }
        var receipts = await FinancialRowsAsync(f, "chirp_article_receipts").ConfigureAwait(false);
        var row = receipts.Single(x => Equals(x[0], previous.Article.Proposal.PlanSha256));
        var receipt = JsonSerializer.Deserialize<ChirpSegmentAcceptedAudio>((string)row[3]!)!;
        var direct = new ChirpReusedArticleAudio(e.Document.Id, e.Document.CurrentRevisionId!.Value, (string)row[4]!,
            receipt.Range with { ReusedReceiptSha256 = (string)row[4]! }, receipt.Audio, receipt.PlanSha256, Guid.ParseExact((string)row[2]!, "N"));
        var retained = new[] { previous.Reuse, direct };
        var proposal = ChirpArticlePlanCompiler.CompileSentenceSuccessor(e.Document, previous.Article.Proposal,
            retained.Select(x => new ChirpNarrationReuse(x.Range.StartScalar, x.Range.EndScalarExclusive, x.Range.TextSha256, x.ReceiptSha256)).ToArray());
        var article = new AdmittedChirpArticle(proposal, ChirpPlanLineageMapper.Map(e.Document.Text, proposal.Parts.Select(x => x.Range).ToArray(), previous.Article.Lineage.History),
            retained, proposal.Parts.Where(x => x.Range.ReusedReceiptSha256 is null).Select(x => new ChirpSegmentAllowance(x.CoverageOrdinal, 1)).ToArray());
        var old = e.Admission.Policy!;
        e.Admission.Policy = old with
        {
            SchemaVersion = 3,
            Articles = [article],
            WholeArticleGeneration = new(Guid.NewGuid(), [proposal.PlanSha256]),
            Budget = old.Budget with { ProjectCommittedMinorUnits = reservePending ? 152 : 151, AllocationCommittedMinorUnits = reservePending ? 22 : 21, QueueRows = await RowPinsAsync(f, "google_generation_queue_states", 3).ConfigureAwait(false) },
            PriorArticleAttemptRows = await RowPinsAsync(f, "chirp_article_attempts", 1).ConfigureAwait(false),
            MaximumNewAttempts = article.AllowedSegments.Count,
            MaximumAdditionalReservationMinorUnits = article.AllowedSegments.Count
        };
        var next = e with { Article = article, Service = new(f, e.Admission, f.Paths, e.Clock) };
        return new(next, previous.FailedState, receipt.Audio.IdempotencyKey);
    }

    private sealed record SentenceFixture(ChirpEvidence Evidence, GoogleGenerationPersistedQueueState FailedState, string DirectKey);
}
