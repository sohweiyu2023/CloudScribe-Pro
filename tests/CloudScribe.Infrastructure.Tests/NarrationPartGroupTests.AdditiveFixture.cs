using System.Text.Json;
using CloudScribe.Application.Documents;
using CloudScribe.Application.Generation;
using CloudScribe.Infrastructure.Generation;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    private static async Task<AdditiveEvidence> AdditiveEvidenceAsync(Fixture f, long? fixedReservation = null)
    {
        var legacy = await ChirpEvidenceAsync(f).ConfigureAwait(false);
        var first = await AdditionalDocumentAsync(f, "en-US-Neural2-D", fixedReservation).ConfigureAwait(false);
        var second = await AdditionalDocumentAsync(f, "en-US-Chirp3-HD-Aoede", fixedReservation).ConfigureAwait(false);
        var articles = new[] { first.Article, second.Article };
        var baseline = new RecoveryBudgetBaseline(1, 100, 1, 100, new string('d', 64), [], [], [], []);
        var policy = first.Admission.Policy! with
        {
            SourceApprovalSha256 = new string('e', 64),
            Budget = baseline,
            Articles = articles,
            MaximumNewAttempts = articles.Sum(x => x.AllowedSegments.Count),
            MaximumAdditionalReservationMinorUnits = 99,
            WholeArticleGeneration = new(Guid.NewGuid(), articles.Select(x => x.Proposal.PlanSha256).ToArray())
        };
        var pins = new[] { first, second }.SelectMany(x => x.Article.Proposal.Parts.Select(p =>
        {
            var intent = FullDocumentIntent(x, p.CoverageOrdinal);
            return new ChirpAdditionalRequestPin(x.Article.Proposal.PlanSha256, p.CoverageOrdinal, intent.Envelope, intent.EstimateMinorUnits);
        })).ToArray();
        var family = new ChirpAdditionalAdmissionFamily(Guid.NewGuid(), policy, 100, 1, baseline.ReviewedEvidenceSha256, pins);
        var store = new SyntheticAdditionalCatalog { Catalog = new(legacy.Admission.Policy, [family]) };
        var service = new ChirpArticleService(f, store, f.Paths, legacy.Clock);
        return new(legacy with { Service = service }, first with { Service = service }, second with { Service = service }, store, service);
    }

    private static async Task<FullDocumentEvidence> AdditionalDocumentAsync(Fixture f, string voice, long? fixedReservation)
    {
        if (fixedReservation is null) return await FullDocumentEvidenceAsync(f, voice).ConfigureAwait(false);
        var document = await f.Library.CreateAsync("Synthetic exact comparison", "The identical short comparison paragraph.", TestContext.Current.CancellationToken).ConfigureAwait(false);
        var proposal = ChirpArticlePlanCompiler.Compile(document, "en-US", voice);
        var history = new[] { new ChirpHistoricalRange(document.Id, document.CurrentRevisionId!.Value, 0, proposal.SourceScalars, proposal.SourceSha256, []) };
        var article = new AdmittedChirpArticle(proposal, ChirpPlanLineageMapper.Map(document.Text, proposal.Parts.Select(x => x.Range).ToArray(), history), [],
            proposal.Parts.Select(x => new ChirpSegmentAllowance(x.CoverageOrdinal, fixedReservation.Value)).ToArray());
        var clock = new ChirpClock();
        var policy = new ChirpArticleAdmissionPolicy(3, new string('a', 64), new string('b', 64), InstalledRecoveryAdmissionStore.ProfileHash(f.Root),
            "synthetic-account", "synthetic-project", "USD", 2, clock.GetUtcNow().AddHours(1), new string('c', 64),
            new(0, 100, 0, 100, new string('d', 64), [], [], [], []), [], [], [article], 1, fixedReservation.Value,
            PriorSuccessorAttemptRows: [], WholeArticleGeneration: new(Guid.NewGuid(), [proposal.PlanSha256]));
        var admission = new SyntheticChirpAdmission { Policy = policy };
        return new(document, article, admission, clock, new(f, admission, f.Paths, clock));
    }

    private static async Task<ChirpWholeArticleJob> AdditionalExecutingJobAsync(Fixture f, ChirpArticleService service, Guid document)
    {
        var review = await service.PrepareWholeArticleAsync(document, TestContext.Current.CancellationToken).ConfigureAwait(false);
        var job = await service.StartWholeArticleAsync(WholeConfirmation(review), TestContext.Current.CancellationToken).ConfigureAwait(false);
        job = job with { State = ChirpWholeArticleState.Executing, ActiveOrdinal = 1 };
        using var db = f.CreateDbContext();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE chirp_whole_article_jobs SET JobJson={JsonSerializer.Serialize(job)},JobSha256={ChirpPolicyValidation.Hash(job)} WHERE JobId={job.JobId.ToString("N")};", TestContext.Current.CancellationToken).ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO chirp_whole_article_events VALUES({job.JobId.ToString("N")},2,{JsonSerializer.Serialize(job)},{ChirpPolicyValidation.Hash(job)});", TestContext.Current.CancellationToken).ConfigureAwait(false);
        return job;
    }

    private sealed class SyntheticAdditionalCatalog : IChirpArticleAdmissionCatalog
    {
        public required ChirpArticleAdmissionCatalog Catalog { get; set; }
        public Task<ChirpArticleAdmissionPolicy?> LoadVerifiedAsync(CancellationToken cancellationToken = default) => Task.FromResult(Catalog.LegacyPolicy);
        public Task<ChirpArticleAdmissionCatalog> LoadCatalogVerifiedAsync(CancellationToken cancellationToken = default) => Task.FromResult(Catalog);
    }

    private sealed record AdditiveEvidence(ChirpEvidence Legacy, FullDocumentEvidence First, FullDocumentEvidence Second,
        SyntheticAdditionalCatalog Store, ChirpArticleService Service);
}
