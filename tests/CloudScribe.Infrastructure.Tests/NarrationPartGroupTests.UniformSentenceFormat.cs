using System.Text.Json;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Fact]
    public async Task UniformSentenceFormatFreshRootGeneratesEveryMissingPartThroughDurableFakeQueue()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await ChirpEvidenceAsync(f, 8, true);
        var proposal = ChirpArticlePlanCompiler.CompileSentences(e.Document, e.Article.Proposal.LanguageCode,
            e.Article.Proposal.VoiceName, inputFormat: new(1));
        var article = new AdmittedChirpArticle(proposal, ChirpPlanLineageMapper.Map(e.Document.Text,
            proposal.Parts.Select(x => x.Range).ToArray(), e.Article.Lineage.History), [],
            proposal.Parts.Select(x => new ChirpSegmentAllowance(x.CoverageOrdinal, 1)).ToArray());
        e.Admission.Policy = e.Admission.Policy! with
        {
            SchemaVersion = 3,
            PriorSuccessorAttemptRows = [],
            Articles = [article],
            MaximumNewAttempts = article.AllowedSegments.Count,
            MaximumAdditionalReservationMinorUnits = article.AllowedSegments.Count,
            WholeArticleGeneration = new(Guid.NewGuid(), [proposal.PlanSha256])
        };
        e = e with { Article = article };
        var review = await e.Service.PrepareWholeArticleAsync(e.Document.Id, TestContext.Current.CancellationToken);
        var job = await e.Service.StartWholeArticleAsync(WholeConfirmation(review), TestContext.Current.CancellationToken);
        var runtime = new WholeFakeRuntime(f, e);
        var done = await e.Service.RunWholeArticleAsync(job.JobId, runtime, token: TestContext.Current.CancellationToken);
        Assert.Equal(ChirpWholeArticleState.Complete, done.State);
        Assert.Equal(article.AllowedSegments.Select(x => x.CoverageOrdinal), runtime.Ordinals);
        Assert.Single(await FinancialRowsAsync(f, "chirp_article_plans"));
    }

    [Fact]
    public async Task UniformSentenceFormatRetainsFiveNodeHistoryAndBothAcceptedLineages()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await UniformSentenceEvidenceAsync(f);
        string oldRows = JsonHash(await FinancialRowsAsync(f, "chirp_article_successor_attempts"));
        TestContext.Current.TestOutputHelper!.WriteLine("SYNTHETIC_UNIFORM_SENTENCE_POLICY_BASE64=" + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(e.Admission.Policy)));
        var review = await e.Service.PrepareWholeArticleAsync(e.Document.Id, TestContext.Current.CancellationToken);
        Assert.Equal(3, review.AcceptedParts);
        var job = await e.Service.StartWholeArticleAsync(WholeConfirmation(review), TestContext.Current.CancellationToken);
        var runtime = new WholeFakeRuntime(f, e);
        var done = await e.Service.RunWholeArticleAsync(job.JobId, runtime, token: TestContext.Current.CancellationToken);
        Assert.Equal(ChirpWholeArticleState.Complete, done.State);
        Assert.Equal(5, (await FinancialRowsAsync(f, "chirp_article_plans")).Count);
        Assert.Equal(oldRows, JsonHash(await FinancialRowsAsync(f, "chirp_article_successor_attempts")));
        var restarted = new ChirpArticleService(f, e.Admission, f.Paths, e.Clock);
        int count = runtime.Ordinals.Count;
        Assert.Equal(done, await restarted.RunWholeArticleAsync(job.JobId, runtime, token: TestContext.Current.CancellationToken));
        Assert.Equal(count, runtime.Ordinals.Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.PrepareAsync(ChirpIntent(e) with
        { CompiledPayload = CompileChirp("different plaintext").Payload }, TestContext.Current.CancellationToken));
        using (var db = f.CreateDbContext())
            await db.Database.ExecuteSqlRawAsync("INSERT INTO chirp_article_plans SELECT {0},DocumentId,RevisionId,AccountId,ProjectId,ProposalJson,LineageJson,ReusedAudioJson FROM chirp_article_plans LIMIT 1;",
                [new string('f', 64)], TestContext.Current.CancellationToken);
        e.Admission.Policy = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.ProgressAsync(e.Document.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UniformSentenceFormatRejectsUnknownAndRepeatedFormatsWithoutFinancialWrites()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await ChirpEvidenceAsync(f, 8, true);
        Assert.Throws<InvalidOperationException>(() => ChirpArticlePlanCompiler.CompileSentences(e.Document,
            e.Article.Proposal.LanguageCode, e.Article.Proposal.VoiceName, inputFormat: new(2)));
        var proposal = ChirpArticlePlanCompiler.CompileSentences(e.Document, e.Article.Proposal.LanguageCode,
            e.Article.Proposal.VoiceName, inputFormat: new(1));
        Assert.Throws<InvalidOperationException>(() => ChirpArticlePlanCompiler.CompileSentenceSuccessor(e.Document, proposal, [], new(1)));
        var ordinary = ChirpArticlePlanCompiler.CompileSentences(e.Document, e.Article.Proposal.LanguageCode, e.Article.Proposal.VoiceName);
        Assert.NotEqual(ordinary.PlanSha256, proposal.PlanSha256, StringComparer.Ordinal);
        foreach (var part in proposal.Parts)
            Assert.DoesNotContain(ordinary.Parts, old => string.Equals(old.PayloadSha256, part.PayloadSha256, StringComparison.Ordinal));
        Assert.Empty(await FinancialRowsAsync(f, "chirp_article_attempts"));
        Assert.Empty(await FinancialRowsAsync(f, "chirp_article_successor_attempts"));
    }

    private static async Task<ChirpEvidence> UniformSentenceEvidenceAsync(Fixture f)
    {
        var state = await SentenceFormatAsync(f, true).ConfigureAwait(false); var e = state.Evidence;
        var accepted = await AcceptChirpAsync(f, e.Service, state.Intent, 7).ConfigureAwait(false);
        await e.Service.PersistAcceptedAsync(accepted.Request, accepted.Outcome, TestContext.Current.CancellationToken).ConfigureAwait(false);
        var current = e.Admission.Policy!;
        var remaining = e.Article.Proposal.Parts.Where(x => x.Range.ReusedReceiptSha256 is null && x.CoverageOrdinal != state.Intent.CoverageOrdinal)
            .Select(x => new ChirpSegmentAllowance(x.CoverageOrdinal, 1)).ToArray();
        e.Admission.Policy = current with
        {
            SchemaVersion = 3,
            SuccessorGrant = null,
            SourceApprovalSha256 = new string('b', 64),
            WholeArticleGeneration = new(Guid.NewGuid(), [e.Article.Proposal.PlanSha256]),
            Articles = [e.Article with { AllowedSegments = remaining }],
            MaximumNewAttempts = remaining.Length,
            MaximumAdditionalReservationMinorUnits = remaining.Length,
            PriorSuccessorAttemptRows = await RowPinsAsync(f, "chirp_article_successor_attempts", 1).ConfigureAwait(false),
            Budget = current.Budget with
            {
                ProjectCommittedMinorUnits = 154,
                AllocationCommittedMinorUnits = 24,
                ProjectCeilingMinorUnits = 154 + remaining.Length,
                AllocationCeilingMinorUnits = 24 + remaining.Length,
                QueueRows = await RowPinsAsync(f, "google_generation_queue_states", 3).ConfigureAwait(false)
            }
        };
        e = e with { Article = e.Admission.Policy.Articles[0] };
        var oldReview = await e.Service.PrepareWholeArticleAsync(e.Document.Id, TestContext.Current.CancellationToken).ConfigureAwait(false);
        var oldJob = await e.Service.StartWholeArticleAsync(WholeConfirmation(oldReview), TestContext.Current.CancellationToken).ConfigureAwait(false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => e.Service.RunWholeArticleAsync(oldJob.JobId,
            new WholeFakeRuntime(f, e) { RejectAfterReservation = true }, token: TestContext.Current.CancellationToken)).ConfigureAwait(false);
        var receipts = await FinancialRowsAsync(f, "chirp_article_receipts").ConfigureAwait(false);
        var row = receipts.Single(x => Equals(x[0], e.Article.Proposal.PlanSha256));
        var receipt = JsonSerializer.Deserialize<ChirpSegmentAcceptedAudio>((string)row[3]!)!;
        var allReuse = e.Article.ReusedAudio.Append(new(e.Document.Id, e.Document.CurrentRevisionId!.Value, (string)row[4]!,
            receipt.Range with { ReusedReceiptSha256 = (string)row[4]! }, receipt.Audio, receipt.PlanSha256, Guid.ParseExact((string)row[2]!, "N"))).ToArray();
        var proposal = ChirpArticlePlanCompiler.CompileSentenceSuccessor(e.Document, e.Article.Proposal,
            allReuse.Select(x => new ChirpNarrationReuse(x.Range.StartScalar, x.Range.EndScalarExclusive, x.Range.TextSha256, x.ReceiptSha256)).ToArray(), new(1));
        var article = new AdmittedChirpArticle(proposal, ChirpPlanLineageMapper.Map(e.Document.Text, proposal.Parts.Select(x => x.Range).ToArray(), e.Article.Lineage.History),
            allReuse, proposal.Parts.Where(x => x.Range.ReusedReceiptSha256 is null).Select(x => new ChirpSegmentAllowance(x.CoverageOrdinal, 1)).ToArray());
        var p = e.Admission.Policy;
        e.Admission.Policy = p with
        {
            Articles = [article],
            SourceApprovalSha256 = new string('c', 64),
            WholeArticleGeneration = new(Guid.NewGuid(), [proposal.PlanSha256]),
            MaximumNewAttempts = article.AllowedSegments.Count,
            MaximumAdditionalReservationMinorUnits = article.AllowedSegments.Count,
            PriorArticleAttemptRows = await RowPinsAsync(f, "chirp_article_attempts", 1).ConfigureAwait(false),
            LegacyAssociations = p.LegacyAssociations?.Select(x => x with { PlanSha256 = proposal.PlanSha256 }).ToArray(),
            Budget = p.Budget with
            {
                ProjectCommittedMinorUnits = 155,
                AllocationCommittedMinorUnits = 25,
                ProjectCeilingMinorUnits = 155 + article.AllowedSegments.Count,
                AllocationCeilingMinorUnits = 25 + article.AllowedSegments.Count,
                QueueRows = await RowPinsAsync(f, "google_generation_queue_states", 3).ConfigureAwait(false)
            }
        };
        return e with { Article = article };
    }
}
