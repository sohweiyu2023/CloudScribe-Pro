using CloudScribe.Application.Logging;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefinedPlanCannotRegenerateTerminalSuccessBeforeOrDuringLocalSave(bool failFinalReceipt)
    {
        using var f = new Fixture(); await f.InitializeAsync(); var fixture = await RefinedEvidenceAsync(f);
        var refinedPolicy = fixture.Evidence.Admission.Policy!;
        fixture.Evidence.Admission.Policy = refinedPolicy with
        {
            SourceApprovalSha256 = new string('f', 64),
            Articles = [fixture.Evidence.Article with { AllowedSegments = [new(3, 1)] }]
        };
        var parentService = new ChirpArticleService(f, fixture.Evidence.Admission, f.Paths, fixture.Evidence.Clock);
        var accepted = await AcceptChirpAsync(f, parentService, ChirpIntent(fixture.Evidence, 3), 9);
        if (failFinalReceipt)
        {
            using var db = f.CreateDbContext();
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER synthetic_refined_save BEFORE INSERT ON chirp_article_receipts BEGIN SELECT RAISE(ABORT,'synthetic-local-save-interruption'); END;", TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<SqliteException>(() => parentService.PersistAcceptedAsync(accepted.Request, accepted.Outcome, TestContext.Current.CancellationToken));
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER synthetic_refined_save;", TestContext.Current.CancellationToken);
        }
        fixture.Evidence.Admission.Policy = refinedPolicy with
        {
            Budget = refinedPolicy.Budget with
            {
                ProjectCommittedMinorUnits = 151,
                AllocationCommittedMinorUnits = 21,
                QueueRows = await RowPinsAsync(f, "google_generation_queue_states", 3)
            },
            PriorArticleAttemptRows = await RowPinsAsync(f, "chirp_article_attempts", 1)
        };
        var restarted = new ChirpArticleService(f, fixture.Evidence.Admission, f.Paths, fixture.Evidence.Clock);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.ProgressAsync(fixture.Evidence.Document.Id, TestContext.Current.CancellationToken));
        Assert.Contains("Recover the existing accepted output", error.Message, StringComparison.Ordinal);
        Assert.Equal(LocalRequestReason.RefinementOutputRecoveryRequired, LocalRequestDiagnostics.Describe(error, DateTimeOffset.UtcNow).Reason);
        Assert.Equal(3, (await FinancialRowsAsync(f, "chirp_article_attempts")).Count);
        Assert.Single(await FinancialRowsAsync(f, "chirp_article_receipts"));
        Assert.Equal(failFinalReceipt ? 2 : 1, (await FinancialRowsAsync(f, "chirp_article_output_journals")).Count);
    }

    [Fact]
    public async Task RefinedLegacyAssociationIsAppendOnlyWithoutDuplicatingRetainedCost()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var fixture = await RefinedEvidenceAsync(f, legacy: true);
        var old = Assert.Single(await FinancialRowsAsync(f, "chirp_article_legacy_associations"));
        string oldHash = JsonHash(old); string spendHash = JsonHash(await FinancialRowsAsync(f, "google_generation_spend_authorizations"));
        var service = new ChirpArticleService(f, fixture.Evidence.Admission, f.Paths, fixture.Evidence.Clock);
        int pilot = fixture.Article.AllowedSegments[0].CoverageOrdinal;
        var resolved = await service.ResolveSegmentAsync(fixture.Evidence.Document.Id, pilot, "synthetic-account", "synthetic-project", "cmn-CN-Chirp3-HD-Alnilam", "cmn-CN", TestContext.Current.CancellationToken);
        var payload = CompileChirp(resolved.ExactText);
        var intent = new ChirpRequestIntent(resolved.Document.Id, pilot, "synthetic-project", ChirpEnvelope(payload), payload.Payload, "USD", 2, 1);
        var review = await service.PrepareAsync(intent, TestContext.Current.CancellationToken);
        Assert.Equal(150, review.ProjectCommittedMinorUnits);
        var approved = await service.ApproveAsync(ChirpConfirmation(review), TestContext.Current.CancellationToken);
        var audits = await FinancialRowsAsync(f, "chirp_article_legacy_associations");
        Assert.Equal(2, audits.Count); Assert.Contains(audits, x => string.Equals(JsonHash(x), oldHash, StringComparison.Ordinal));
        Assert.Equal(spendHash, JsonHash(await FinancialRowsAsync(f, "google_generation_spend_authorizations")), StringComparer.Ordinal);
        Assert.Equal(1, approved.ReservedMinorUnits);
        Assert.Equal(fixture.AcceptedBytes, await File.ReadAllBytesAsync(fixture.Reuse.Audio.AudioPath, TestContext.Current.CancellationToken));
        var restarted = new ChirpArticleService(f, fixture.Evidence.Admission, f.Paths, fixture.Evidence.Clock);
        var same = await restarted.PrepareAsync(intent, TestContext.Current.CancellationToken);
        Assert.Equal(approved.AuthorizationId, same.ExistingAuthorizationId);
        Assert.Equal(approved, await restarted.ApproveAsync(ChirpConfirmation(same), TestContext.Current.CancellationToken));
        Assert.Equal(2, (await FinancialRowsAsync(f, "chirp_article_legacy_associations")).Count);
    }
}
