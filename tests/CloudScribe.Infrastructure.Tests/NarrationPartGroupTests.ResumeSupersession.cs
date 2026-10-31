using CloudScribe.Infrastructure.Generation;
using CloudScribe.Infrastructure.Providers;
using CloudScribe.Providers.Abstractions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshExplicitResumeSupersedesUnsubmittedCatalogMappingAndKeepsAllFunds(bool expiredAncestor)
    {
        using var f = new Fixture(); var r = await ReservedResumeAsync(f);
        var first = await ResumeReviewAsync(r); await r.Service.ApproveReservedResumeAsync(ChirpConfirmation(first), TestContext.Current.CancellationToken);
        string before = await CatalogRowsAsync(f); var prior = r.Admission.Policy!;
        r.Old.Clock.Now = expiredAncestor ? prior.CatalogExpiresAtUtc.AddSeconds(1) : r.Old.Clock.Now.AddMinutes(1);
        var account = await new EfProviderAccountStore(f, r.Old.Clock).FindAsync(GoogleGenerationProvider.StableProviderId, prior.OriginalEnvelope.AccountId, TestContext.Current.CancellationToken);
        var catalog = await new EfProviderCapabilitySnapshotStore(f).SaveAsync(new(account!.Reference, r.Old.Clock.Now, prior.OriginalEnvelope.CapabilityProvenanceId,
            [new(GoogleGenerationProvider.SynthesizeOperationStableId, ProviderCapabilityState.Supported, ProviderLifecycleState.Available)]),
            r.Old.Clock.Now.AddMinutes(30), TestContext.Current.CancellationToken);
        var inspected = await r.Service.InspectReservedResumeAdmissionAsync(r.Reserved.AuthorizationId, prior.HistoricalPolicy, catalog.Id,
            catalog.ExpiresAtUtc, TestContext.Current.CancellationToken);
        var next = prior with
        {
            MappingId = Guid.NewGuid(),
            SourceApprovalSha256 = new string('2', 64),
            PriorMappingSha256 = ChirpPolicyValidation.Hash(prior),
            ExecutionEnvelope = prior.ExecutionEnvelope with { RequestRevision = 3 },
            CatalogSnapshotId = catalog.Id,
            CatalogExpiresAtUtc = catalog.ExpiresAtUtc,
            ExpiresAtUtc = catalog.ExpiresAtUtc.AddMinutes(-1),
            ProviderEvidenceSha256 = inspected.ProviderEvidenceSha256
        };
        r.Admission.Policy = next;
        var intent = r.Intent with { Envelope = next.ExecutionEnvelope with { ChirpAuthorizationId = null } };
        var review = await r.Service.PrepareReservedResumeAsync(intent, next.SelectionSha256, next.RuntimeEvidenceSha256, next.CatalogSnapshotId, next.RequestIdentitySha256, TestContext.Current.CancellationToken);
        Assert.Equal(r.Reserved, await r.Service.ApproveReservedResumeAsync(ChirpConfirmation(review), TestContext.Current.CancellationToken));
        Assert.NotNull(await r.Service.LoadSpendAsync(next.ExecutionEnvelope, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => r.Service.LoadSpendAsync(prior.ExecutionEnvelope, TestContext.Current.CancellationToken));
        Assert.Equal(before, await CatalogRowsAsync(f));
        using var db = f.CreateDbContext();
        Assert.Equal(2, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM chirp_reserved_catalog_mappings").SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM chirp_article_attempts").SingleAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("root")]
    [InlineData("approval-reuse")]
    [InlineData("wrong-predecessor")]
    public async Task ResumeSupersessionRejectsRootDuplicationApprovalReuseAndWrongHead(string change)
    {
        using var f = new Fixture(); var r = await ReservedResumeAsync(f);
        var first = await ResumeReviewAsync(r); await r.Service.ApproveReservedResumeAsync(ChirpConfirmation(first), TestContext.Current.CancellationToken);
        string before = await CatalogRowsAsync(f); var prior = r.Admission.Policy!;
        r.Admission.Policy = prior with
        {
            MappingId = Guid.NewGuid(),
            SourceApprovalSha256 = string.Equals(change, "approval-reuse", StringComparison.Ordinal) ? prior.SourceApprovalSha256 : new string('2', 64),
            PriorMappingSha256 = change switch { "root" => null, "wrong-predecessor" => new string('0', 64), _ => ChirpPolicyValidation.Hash(prior) }
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => ResumeReviewAsync(r));
        Assert.Equal(before, await CatalogRowsAsync(f));
        using var db = f.CreateDbContext();
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync(
            "INSERT INTO chirp_reserved_catalog_mappings SELECT AuthorizationId,{0},PolicyJson,{1},CreatedAtUnixMilliseconds,NULL FROM chirp_reserved_catalog_mappings LIMIT 1;",
            [Guid.NewGuid().ToString("N"), new string('0', 64)], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RestoringAnOlderStillFreshPinCannotRollBackTheExplicitMappingHead()
    {
        using var f = new Fixture(); var r = await ReservedResumeAsync(f);
        var first = await ResumeReviewAsync(r); await r.Service.ApproveReservedResumeAsync(ChirpConfirmation(first), TestContext.Current.CancellationToken);
        var prior = r.Admission.Policy!;
        r.Admission.Policy = prior with { MappingId = Guid.NewGuid(), SourceApprovalSha256 = new string('2', 64), PriorMappingSha256 = ChirpPolicyValidation.Hash(prior) };
        var second = await ResumeReviewAsync(r); await r.Service.ApproveReservedResumeAsync(ChirpConfirmation(second), TestContext.Current.CancellationToken);
        r.Admission.Policy = prior;
        await Assert.ThrowsAsync<InvalidOperationException>(() => r.Service.LoadSpendAsync(prior.ExecutionEnvelope, TestContext.Current.CancellationToken));
        using var db = f.CreateDbContext();
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT Consumed AS Value FROM chirp_article_attempts").SingleAsync(TestContext.Current.CancellationToken));
    }
}
