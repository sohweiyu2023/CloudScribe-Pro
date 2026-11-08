using CloudScribe.Infrastructure.Generation;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Theory]
    [InlineData("foreign-policy")]
    [InlineData("reservation")]
    [InlineData("envelope")]
    [InlineData("missing-queue")]
    [InlineData("missing-attempt")]
    [InlineData("missing-attempt-and-queue")]
    [InlineData("duplicate-key")]
    public async Task AdditiveSiblingFinancialCorruptionBlocksLegacyClaimBeforeProjection(string corruption)
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await AdditiveEvidenceAsync(f, 2);
        var oldIntent = ChirpIntent(e.Legacy);
        var oldReview = await e.Service.PrepareAsync(oldIntent, TestContext.Current.CancellationToken);
        var old = await e.Service.ApproveAsync(ChirpConfirmation(oldReview), TestContext.Current.CancellationToken);
        var job = await AdditionalExecutingJobAsync(f, e.Service, e.First.Document.Id);
        var review = await e.Service.PrepareAsync(FullDocumentIntent(e.First, 1), TestContext.Current.CancellationToken);
        var sibling = await e.Service.ApproveWholeArticleRequestAsync(job.JobId, review.PreviewId, TestContext.Current.CancellationToken);
        using (var db = f.CreateDbContext())
        {
            // Deliberate corruption of this disposable synthetic database, never production repair.
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER chirp_root_binding_immutable; DROP TRIGGER chirp_root_retained;", TestContext.Current.CancellationToken);
            string id = sibling.AuthorizationId.ToString("N");
            if (string.Equals(corruption, "foreign-policy", StringComparison.Ordinal))
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE chirp_article_attempts SET PolicySha256={new string('f', 64)} WHERE AuthorizationId={id};", TestContext.Current.CancellationToken);
            else if (string.Equals(corruption, "reservation", StringComparison.Ordinal))
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE chirp_article_attempts SET ReservedMinorUnits=3 WHERE AuthorizationId={id};", TestContext.Current.CancellationToken);
            else if (string.Equals(corruption, "envelope", StringComparison.Ordinal))
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE chirp_article_attempts SET EnvelopeJson=replace(EnvelopeJson,'synthetic-current-family-price','changed-price') WHERE AuthorizationId={id};", TestContext.Current.CancellationToken);
            else if (string.Equals(corruption, "missing-queue", StringComparison.Ordinal))
                await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM google_generation_queue_states WHERE IdempotencyKey={sibling.IdempotencyKey};", TestContext.Current.CancellationToken);
            else if (corruption is "missing-attempt" or "missing-attempt-and-queue")
            {
                if (string.Equals(corruption, "missing-attempt-and-queue", StringComparison.Ordinal))
                    await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM google_generation_queue_states WHERE IdempotencyKey={sibling.IdempotencyKey};", TestContext.Current.CancellationToken);
                await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM chirp_article_attempts WHERE AuthorizationId={id};", TestContext.Current.CancellationToken);
            }
            else await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO chirp_article_successor_attempts
                SELECT {Guid.NewGuid().ToString("N")},PlanSha256,CoverageOrdinal,PolicySha256,SourceApprovalSha256,AccountId,ProjectId,
                    ChildKey,ReviewSha256,EnvelopeJson,Currency,Scale,EstimateMinorUnits,ReservedMinorUnits,ApprovedFinancialSha256,
                    ApprovedAtUnixMilliseconds,Consumed,ClaimedLocalAttemptId,{old.AuthorizationId.ToString("N")},{old.AuthorizationId.ToString("N")},
                    {new string('a', 64)},{new string('b', 64)},{Guid.NewGuid().ToString("N")}
                FROM chirp_article_attempts WHERE AuthorizationId={id};
                """, TestContext.Current.CancellationToken);
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => e.Service.TryClaimAuthorizedAsync(oldIntent.Envelope with { ChirpAuthorizationId = old.AuthorizationId },
            oldIntent.ProjectId, "USD", 2, oldIntent.EstimateMinorUnits, ChirpClaim(oldIntent, old), TestContext.Current.CancellationToken));
        Assert.Equal(0L, (await FinancialRowsAsync(f, "chirp_article_attempts")).Single(x => Equals(x[0], old.AuthorizationId.ToString("N")))[16]);
    }

    [Fact]
    public async Task AdditiveOrphanQueueCannotBeExcludedByPolicyFiltering()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await AdditiveEvidenceAsync(f, 2);
        await new GoogleGenerationPersistedQueueStateStore(f).SaveAsync(new("synthetic-account", GoogleGenerationProvider.SynthesizeOperationStableId,
            "unknown-synthetic-queue", false, null), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => e.Service.PrepareAsync(FullDocumentIntent(e.First, 1), TestContext.Current.CancellationToken));
        Assert.Empty(await FinancialRowsAsync(f, "chirp_article_attempts"));
    }

    [Fact]
    public async Task AdditiveSameSourceApprovalCannotCreateAnotherIndependentAllowance()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await AdditiveEvidenceAsync(f, 2);
        var separate = await AdditiveEvidenceAsync(f, 2);
        var one = e.Store.Catalog.Families[0]; var two = separate.Store.Catalog.Families[0];
        Assert.NotEqual(one.FamilyId, two.FamilyId);
        Assert.Empty(one.Policy.Articles.Select(x => x.Proposal.DocumentId).Intersect(two.Policy.Articles.Select(x => x.Proposal.DocumentId)));
        Assert.Throws<InvalidDataException>(() => ChirpAdditionalAdmissionValidation.RequireValid(e.Store.Catalog with { Families = [one, two] }, e.Legacy.Clock.Now));
        var reusedLegacy = one with { Policy = one.Policy with { SourceApprovalSha256 = e.Store.Catalog.LegacyPolicy!.SourceApprovalSha256 } };
        Assert.Throws<InvalidDataException>(() => ChirpAdditionalAdmissionValidation.RequireValid(e.Store.Catalog with { Families = [reusedLegacy] }, e.Legacy.Clock.Now));
        Assert.Empty(await FinancialRowsAsync(f, "chirp_article_attempts"));
    }
}
