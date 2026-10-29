using System.Text.Json;
using CloudScribe.Application.Generation;
using CloudScribe.Infrastructure.Generation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    private static async Task<ChirpEvidence> LegacyAssociationEvidenceAsync(Fixture f)
    {
        var e = await ChirpEvidenceAsync(f).ConfigureAwait(false);
        // Synthetic reproduction of historical missing wire metadata. Never run against a user profile.
        using (var db = f.CreateDbContext())
            await db.Database.ExecuteSqlRawAsync("UPDATE google_generation_queue_states SET PayloadSha256=NULL,PayloadBytes=NULL,DiagnosticCode=NULL WHERE IdempotencyKey={0};",
                [e.Historical.IdempotencyKey], TestContext.Current.CancellationToken).ConfigureAwait(false);
        var history = e.Article.Lineage.History.Select((x, i) => i == 0 ? x with { Attempts = [] } : x).ToArray();
        var article = e.Article with { Lineage = ChirpPlanLineageMapper.Map(e.Document.Text, e.Article.Proposal.Parts.Select(x => x.Range).ToArray(), history) };
        e = e with { Article = article };
        e.Admission.Policy = e.Admission.Policy! with
        {
            Articles = [article with { AllowedSegments = [new(2, 1)] }],
            Budget = e.Admission.Policy.Budget with { QueueRows = await RowPinsAsync(f, "google_generation_queue_states", 3).ConfigureAwait(false) }
        };
        // Existing accepted coverage was saved under the prior, immutable lineage.
        var accepted = await AcceptChirpAsync(f, e.Service, ChirpIntent(e, 2), 2).ConfigureAwait(false);
        await e.Service.PersistAcceptedAsync(accepted.Request, accepted.Outcome, TestContext.Current.CancellationToken).ConfigureAwait(false);
        var q = (await FinancialRowsAsync(f, "google_generation_queue_states").ConfigureAwait(false)).Single(x => Equals(x[2], e.Historical.IdempotencyKey));
        var s = Assert.Single(await FinancialRowsAsync(f, "google_generation_spend_authorizations").ConfigureAwait(false));
        var h = history[0];
        var supplement = new ChirpLegacyAttemptAssociation(article.Proposal.PlanSha256, h.DocumentId, h.RevisionId,
            h.StartScalar, h.EndScalarExclusive, h.TextSha256, "synthetic-account", "synthetic-project", GoogleGenerationProvider.SynthesizeOperationStableId,
            e.Historical.IdempotencyKey, JsonHash(q), Guid.Parse((string)s[0]!), JsonHash(s), (string)s[8]!, checked((int)(long)s[9]!), 3, new string('d', 64));
        e.Admission.Policy = e.Admission.Policy with
        {
            Articles = [article],
            Budget = e.Admission.Policy.Budget with
            {
                ProjectCommittedMinorUnits = 149,
                AllocationCommittedMinorUnits = 19,
                QueueRows = await RowPinsAsync(f, "google_generation_queue_states", 3).ConfigureAwait(false)
            },
            PriorArticleAttemptRows = await RowPinsAsync(f, "chirp_article_attempts", 1).ConfigureAwait(false),
            LegacyAssociations = [supplement],
            LegacyAssociationRows = [],
            MaximumNewAttempts = 1,
            MaximumAdditionalReservationMinorUnits = 1
        };
        return e;
    }

    [Fact]
    public async Task ChirpSupplementPreservesImmutablePlanOldHoldAndAcceptedAudioAcrossRestart()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await LegacyAssociationEvidenceAsync(f);
        var plans = JsonSerializer.Serialize(await FinancialRowsAsync(f, "chirp_article_plans"));
        var receipts = JsonSerializer.Serialize(await FinancialRowsAsync(f, "chirp_article_receipts"));
        var spend = JsonSerializer.Serialize(await FinancialRowsAsync(f, "google_generation_spend_authorizations"));
        var oldQueue = (await FinancialRowsAsync(f, "google_generation_queue_states")).Single(x => Equals(x[2], e.Historical.IdempotencyKey));
        string canaryFile = Assert.Single(Directory.GetFiles(Path.Combine(f.Root, "accepted-chirp-article-audio"), "*.mp3"));
        byte[] canaryBytes = await File.ReadAllBytesAsync(canaryFile, TestContext.Current.CancellationToken);
        var review = await e.Service.PrepareAsync(ChirpIntent(e), TestContext.Current.CancellationToken);
        var approved = await e.Service.ApproveAsync(ChirpConfirmation(review), TestContext.Current.CancellationToken);
        var restart = new ChirpArticleService(f, e.Admission, f.Paths, e.Clock);
        var same = await restart.PrepareAsync(ChirpIntent(e), TestContext.Current.CancellationToken);
        Assert.Equal(approved.AuthorizationId, same.ExistingAuthorizationId);
        Assert.Equal(approved, await restart.ApproveAsync(ChirpConfirmation(same), TestContext.Current.CancellationToken));
        Assert.Single(await FinancialRowsAsync(f, "chirp_article_legacy_associations"));
        Assert.Equal(plans, JsonSerializer.Serialize(await FinancialRowsAsync(f, "chirp_article_plans")));
        Assert.Equal(receipts, JsonSerializer.Serialize(await FinancialRowsAsync(f, "chirp_article_receipts")));
        Assert.Equal(spend, JsonSerializer.Serialize(await FinancialRowsAsync(f, "google_generation_spend_authorizations")));
        Assert.Equal(JsonHash(oldQueue), JsonHash((await FinancialRowsAsync(f, "google_generation_queue_states")).Single(x => Equals(x[2], e.Historical.IdempotencyKey))));
        var accepted = await AcceptChirpAsync(f, restart, ChirpIntent(e), 1);
        await restart.PersistAcceptedAsync(accepted.Request, accepted.Outcome, TestContext.Current.CancellationToken);
        Assert.Equal(canaryBytes, await File.ReadAllBytesAsync(canaryFile, TestContext.Current.CancellationToken));
        e.Admission.Policy = null; e.Clock.Now = e.Clock.Now.AddHours(2);
        Assert.Equal(2, (await restart.ProgressAsync(e.Document.Id, TestContext.Current.CancellationToken)).SavedCoverageParts);
        await Assert.ThrowsAsync<InvalidOperationException>(() => restart.PrepareAsync(ChirpIntent(e), TestContext.Current.CancellationToken));
        using var db = f.CreateDbContext();
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM chirp_article_legacy_associations;", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("UPDATE chirp_article_legacy_associations SET FirstPolicySha256='changed';", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("queue")]
    [InlineData("spend")]
    [InlineData("payload")]
    [InlineData("bytes")]
    [InlineData("account")]
    [InlineData("project")]
    [InlineData("revision")]
    [InlineData("range")]
    [InlineData("amount")]
    [InlineData("duplicate")]
    [InlineData("unpinned")]
    [InlineData("evidence")]
    [InlineData("omitted")]
    public async Task ChirpLegacyAssociationMismatchCreatesNeitherAuditNorReservation(string mismatch)
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await LegacyAssociationEvidenceAsync(f);
        var p = e.Admission.Policy!; var a = p.LegacyAssociations![0];
        a = mismatch switch
        {
            "queue" => a with { QueueRowSha256 = new string('e', 64) },
            "spend" => a with { SpendAuthorizationId = Guid.NewGuid() },
            "payload" => a with { OriginalPayloadSha256 = new string('e', 64) },
            "bytes" => a with { OriginalPayloadBytes = a.OriginalPayloadBytes + 1 },
            "account" => a with { AccountId = "other-account" },
            "project" => a with { ProjectId = "other-project" },
            "revision" => a with { RevisionId = Guid.NewGuid() },
            "range" => a with { EndScalarExclusive = a.EndScalarExclusive + 1 },
            "amount" => a with { RetainedReservationMinorUnits = 2 },
            "evidence" => a with { ReviewedEvidenceSha256 = "unreviewed" },
            _ => a
        };
        e.Admission.Policy = p with
        {
            LegacyAssociations = string.Equals(mismatch, "omitted", StringComparison.Ordinal) ? null : string.Equals(mismatch, "duplicate", StringComparison.Ordinal) ? [a, a] : [a],
            LegacyAssociationRows = string.Equals(mismatch, "omitted", StringComparison.Ordinal) ? null : [],
            Budget = string.Equals(mismatch, "unpinned", StringComparison.Ordinal) ? p.Budget with { SpendRows = [] } : p.Budget
        };
        await Assert.ThrowsAnyAsync<Exception>(() => e.Service.PrepareAsync(ChirpIntent(e), TestContext.Current.CancellationToken));
        Assert.Empty(await FinancialRowsAsync(f, "chirp_article_legacy_associations"));
        Assert.Single(await FinancialRowsAsync(f, "chirp_article_attempts")); // Existing accepted canary only.
    }

    [Fact]
    public async Task ChirpLegacyAuditAndReservationRollBackTogetherOnRevocation()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await LegacyAssociationEvidenceAsync(f);
        var review = await e.Service.PrepareAsync(ChirpIntent(e), TestContext.Current.CancellationToken);
        e.Admission.RevokeAt = e.Admission.Reads + 3;
        await Assert.ThrowsAsync<InvalidOperationException>(() => e.Service.ApproveAsync(ChirpConfirmation(review), TestContext.Current.CancellationToken));
        Assert.Empty(await FinancialRowsAsync(f, "chirp_article_legacy_associations"));
        Assert.Single(await FinancialRowsAsync(f, "chirp_article_attempts"));
    }

    [Fact]
    public async Task ChirpReviewedRenewalPinsOriginalAuditWithoutRewritingItOrReusingOldAuthority()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await LegacyAssociationEvidenceAsync(f);
        var accepted = await AcceptChirpAsync(f, e.Service, ChirpIntent(e), 1);
        await e.Service.PersistAcceptedAsync(accepted.Request, accepted.Outcome, TestContext.Current.CancellationToken);
        string audit = JsonSerializer.Serialize(await FinancialRowsAsync(f, "chirp_article_legacy_associations"));
        var prior = e.Admission.Policy!;
        var renewed = prior with
        {
            SourceApprovalSha256 = new string('e', 64),
            ExpiresAtUtc = e.Clock.Now.AddHours(2),
            Budget = prior.Budget with
            {
                ProjectCommittedMinorUnits = 150,
                AllocationCommittedMinorUnits = 20,
                QueueRows = await RowPinsAsync(f, "google_generation_queue_states", 3)
            },
            PriorArticleAttemptRows = await RowPinsAsync(f, "chirp_article_attempts", 1),
            LegacyAssociationRows = await RowPinsAsync(f, "chirp_article_legacy_associations", 2),
            Articles = [e.Article with { AllowedSegments = [new(3, 1)] }]
        };
        var restarted = new ChirpArticleService(f, e.Admission, f.Paths, e.Clock);
        e.Admission.Policy = renewed with { LegacyAssociations = null, LegacyAssociationRows = null };
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.PrepareAsync(ChirpIntent(e, 3), TestContext.Current.CancellationToken));
        e.Admission.Policy = renewed with { LegacyAssociations = [renewed.LegacyAssociations![0] with { ReviewedEvidenceSha256 = new string('f', 64) }] };
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.PrepareAsync(ChirpIntent(e, 3), TestContext.Current.CancellationToken));
        e.Admission.Policy = renewed;
        var review = await restarted.PrepareAsync(ChirpIntent(e, 3), TestContext.Current.CancellationToken);
        await restarted.ApproveAsync(ChirpConfirmation(review), TestContext.Current.CancellationToken);
        Assert.Equal(audit, JsonSerializer.Serialize(await FinancialRowsAsync(f, "chirp_article_legacy_associations")));
        Assert.Equal(3, (await FinancialRowsAsync(f, "chirp_article_attempts")).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChirpChangedUnknownQueueOrUncertainLegacyCannotBeReadmittedByRepinning(bool uncertain)
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await LegacyAssociationEvidenceAsync(f);
        using (var db = f.CreateDbContext())
            await db.Database.ExecuteSqlRawAsync(uncertain
                ? "UPDATE google_generation_queue_states SET UnresolvedSubmission=1 WHERE IdempotencyKey={0};"
                : "UPDATE google_generation_queue_states SET PayloadBytes=1 WHERE IdempotencyKey={0};",
                [e.Historical.IdempotencyKey], TestContext.Current.CancellationToken);
        var p = e.Admission.Policy!;
        var row = (await FinancialRowsAsync(f, "google_generation_queue_states")).Single(x => Equals(x[2], e.Historical.IdempotencyKey));
        e.Admission.Policy = p with
        {
            Budget = p.Budget with { QueueRows = await RowPinsAsync(f, "google_generation_queue_states", 3) },
            LegacyAssociations = [p.LegacyAssociations![0] with { QueueRowSha256 = JsonHash(row) }]
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => e.Service.PrepareAsync(ChirpIntent(e), TestContext.Current.CancellationToken));
        Assert.Empty(await FinancialRowsAsync(f, "chirp_article_legacy_associations"));
        Assert.Single(await FinancialRowsAsync(f, "chirp_article_attempts"));
    }

    [Fact]
    public async Task ChirpConcurrentSupplementApprovalsCommitOnlyOneAuditAndOneNewReservation()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await LegacyAssociationEvidenceAsync(f);
        var other = new ChirpArticleService(f, e.Admission, f.Paths, e.Clock);
        var first = await e.Service.PrepareAsync(ChirpIntent(e), TestContext.Current.CancellationToken);
        var second = await other.PrepareAsync(ChirpIntent(e), TestContext.Current.CancellationToken);
        var results = await Task.WhenAll(Task.Run(() => TryApproveChirpAsync(e.Service, first), TestContext.Current.CancellationToken),
            Task.Run(() => TryApproveChirpAsync(other, second), TestContext.Current.CancellationToken));
        Assert.Single(results, x => x is not null);
        Assert.Single(await FinancialRowsAsync(f, "chirp_article_legacy_associations"));
        Assert.Equal(2, (await FinancialRowsAsync(f, "chirp_article_attempts")).Count);
    }
}
