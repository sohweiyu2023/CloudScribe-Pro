using CloudScribe.Infrastructure.Generation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Theory]
    [InlineData("account")]
    [InlineData("project")]
    [InlineData("credential")]
    [InlineData("payload")]
    [InlineData("source")]
    [InlineData("selection")]
    [InlineData("runtime")]
    [InlineData("full-identity")]
    [InlineData("catalog")]
    [InlineData("expired")]
    [InlineData("started")]
    [InlineData("uncertain")]
    public async Task ReservedResumeRejectsChangedOrStartedEvidenceWithoutNewFunds(string change)
    {
        using var f = new Fixture(); var r = await ReservedResumeAsync(f); var p = r.Admission.Policy!;
        var intent = change switch
        {
            "account" => r.Intent with { Envelope = r.Intent.Envelope with { AccountId = "other" } },
            "project" => r.Intent with { ProjectId = "other" },
            "credential" => r.Intent with { Envelope = r.Intent.Envelope with { CredentialReferenceId = "other" } },
            "payload" => r.Intent with { CompiledPayload = "private synthetic altered text"u8.ToArray() },
            "source" => r.Intent with { DocumentId = Guid.NewGuid() },
            _ => r.Intent,
        };
        if (string.Equals(change, "expired", StringComparison.Ordinal)) r.Old.Clock.Now = p.ExpiresAtUtc;
        if (change is "started" or "uncertain")
        {
            using var db = f.CreateDbContext();
            await db.Database.ExecuteSqlRawAsync("UPDATE google_generation_queue_states SET SubmissionAttempted=1,UnresolvedSubmission={0} WHERE IdempotencyKey={1}",
                [string.Equals(change, "uncertain", StringComparison.Ordinal) ? 1 : 0, r.Reserved.IdempotencyKey], TestContext.Current.CancellationToken);
        }
        string before = await CatalogRowsAsync(f);
        Func<Task> action = () => r.Service.PrepareReservedResumeAsync(intent,
            string.Equals(change, "selection", StringComparison.Ordinal) ? new string('0', 64) : p.SelectionSha256,
            string.Equals(change, "runtime", StringComparison.Ordinal) ? new string('0', 64) : p.RuntimeEvidenceSha256,
            string.Equals(change, "catalog", StringComparison.Ordinal) ? Guid.NewGuid() : p.CatalogSnapshotId,
            string.Equals(change, "full-identity", StringComparison.Ordinal) ? new string('0', 64) : p.RequestIdentitySha256, TestContext.Current.CancellationToken);
        if (string.Equals(change, "expired", StringComparison.Ordinal)) await Assert.ThrowsAsync<InvalidDataException>(action);
        else await Assert.ThrowsAsync<InvalidOperationException>(action);
        Assert.Equal(before, await CatalogRowsAsync(f));
        using var final = f.CreateDbContext();
        Assert.Equal(0, await final.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM chirp_reserved_catalog_mappings").SingleAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentResumeMappingIsIdempotentOrRejectsDifferentMappingWithoutReservation(bool conflicting)
    {
        using var f = new Fixture(); var r = await ReservedResumeAsync(f); string before = await CatalogRowsAsync(f);
        var otherAdmission = new SyntheticResumeAdmission { Policy = conflicting ? r.Admission.Policy! with { MappingId = Guid.NewGuid() } : r.Admission.Policy };
        var other = new ChirpArticleService(f, r.Old.Admission, f.Paths, r.Old.Clock, otherAdmission);
        var first = await ResumeReviewAsync(r); var second = await ResumeReviewAsync(r, other);
        async Task<bool> MapAsync(ChirpArticleService service, ChirpRequestReview review)
        {
            try { await service.ApproveReservedResumeAsync(ChirpConfirmation(review), TestContext.Current.CancellationToken).ConfigureAwait(false); return true; }
            catch (InvalidOperationException) { return false; }
        }
        var outcomes = await Task.WhenAll(Task.Run(() => MapAsync(r.Service, first), TestContext.Current.CancellationToken),
            Task.Run(() => MapAsync(other, second), TestContext.Current.CancellationToken));
        Assert.Equal(conflicting ? 1 : 2, outcomes.Count(x => x));
        Assert.Equal(before, await CatalogRowsAsync(f));
        using var db = f.CreateDbContext();
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM chirp_reserved_catalog_mappings").SingleAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConcurrentResumeClaimsHaveOneWinnerAndMappingCannotBeDeletedOrRewritten()
    {
        using var f = new Fixture(); var r = await ReservedResumeAsync(f);
        var review = await ResumeReviewAsync(r); await r.Service.ApproveReservedResumeAsync(ChirpConfirmation(review), TestContext.Current.CancellationToken);
        async Task<bool> ClaimAsync()
        {
            try
            {
                return await r.Service.TryClaimAuthorizedAsync(r.Admission.Policy!.ExecutionEnvelope, r.Intent.ProjectId, "USD", 2, 1,
                ChirpClaim(r.Intent, r.Reserved), TestContext.Current.CancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException) { return false; }
        }
        Assert.Single(await Task.WhenAll(Task.Run(ClaimAsync, TestContext.Current.CancellationToken), Task.Run(ClaimAsync, TestContext.Current.CancellationToken)), x => x);
        using var db = f.CreateDbContext();
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("UPDATE chirp_reserved_catalog_mappings SET PolicySha256='changed'", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM chirp_reserved_dispatch_mappings", TestContext.Current.CancellationToken));
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM chirp_article_attempts").SingleAsync(TestContext.Current.CancellationToken));
    }
}
