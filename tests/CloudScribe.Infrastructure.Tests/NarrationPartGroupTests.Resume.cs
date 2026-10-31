using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Providers.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Fact]
    public async Task ReservedResumeKeepsOneHeldIdentityAcrossRestartThenClaimsAndPersistsMappingWithAcceptedOutput()
    {
        using var f = new Fixture(); var r = await ReservedResumeAsync(f); var policy = r.Admission.Policy!;
        string before = await CatalogRowsAsync(f);
        var review = await ResumeReviewAsync(r);
        Assert.True(review.IsCatalogRevalidation); Assert.Equal(r.Reserved.AuthorizationId, review.ExistingAuthorizationId);
        Assert.Equal(r.Reserved, await r.Service.ApproveReservedResumeAsync(ChirpConfirmation(review), TestContext.Current.CancellationToken));
        Assert.Equal(before, await CatalogRowsAsync(f));
        var restarted = new ChirpArticleService(f, r.Old.Admission, f.Paths, r.Old.Clock, r.Admission);
        review = await ResumeReviewAsync(r, restarted);
        Assert.Equal(r.Reserved, await restarted.ApproveReservedResumeAsync(ChirpConfirmation(review), TestContext.Current.CancellationToken));
        var spend = await restarted.LoadSpendAsync(policy.ExecutionEnvelope, TestContext.Current.CancellationToken);
        Assert.Equal(policy.ExecutionEnvelope, spend!.Envelope);
        Assert.Equal(before, await CatalogRowsAsync(f));
        var request = new GenerationProviderRequest(GoogleGenerationProvider.StableProviderId, GoogleGenerationProvider.SynthesizeOperationStableId,
            policy.ExecutionEnvelope.AccountId, r.Reserved.IdempotencyKey, r.Intent.CompiledPayload, "MP3");
        var persisted = new GoogleGenerationPersistedQueueStateStore(f); int fakeCalls = 0;
        var bound = new GoogleGenerationBoundQueueCoordinator(new((_, _, _) =>
        { fakeCalls++; return Task.FromResult(new GenerationProviderResponse(SubmissionDisposition.Accepted, null, new byte[128], "audio/mpeg", null, "google-accepted")); }),
            persisted.LoadAsync, persisted.SaveAsync, (claim, token) => restarted.TryClaimAuthorizedAsync(policy.ExecutionEnvelope,
                r.Intent.ProjectId, "USD", 2, 1, claim, token), persisted.SaveOutcomeAsync);
        var trust = ChirpTrust(r.Intent);
        var outcome = await bound.ProcessDurableAsync(request, trust, true, true, true, true, TestContext.Current.CancellationToken);
        Assert.True(File.Exists(await restarted.PersistAcceptedAsync(request, outcome, TestContext.Current.CancellationToken)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.LoadSpendAsync(policy.ExecutionEnvelope, TestContext.Current.CancellationToken));
        Assert.Equal(1, fakeCalls);
        using var db = f.CreateDbContext();
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM chirp_reserved_catalog_mappings").SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM chirp_reserved_dispatch_mappings").SingleAsync(TestContext.Current.CancellationToken));
        var receiptJson = await db.Database.SqlQueryRaw<string>("SELECT ReceiptJson AS Value FROM chirp_article_receipts").SingleAsync(TestContext.Current.CancellationToken);
        Assert.Contains(ChirpPolicyValidation.Hash(policy), receiptJson, StringComparison.Ordinal);
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM chirp_article_attempts").SingleAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("mapping")]
    [InlineData("claim")]
    public async Task RevocationAfterWriteRollsBackMappingOrClaimAndRetainsReservation(string stage)
    {
        using var f = new Fixture(); var r = await ReservedResumeAsync(f); string before = await CatalogRowsAsync(f);
        var review = await ResumeReviewAsync(r);
        if (string.Equals(stage, "mapping", StringComparison.Ordinal))
        {
            r.Admission.RevokeAt = r.Admission.Reads + 2;
            await Assert.ThrowsAsync<InvalidOperationException>(() => r.Service.ApproveReservedResumeAsync(ChirpConfirmation(review), TestContext.Current.CancellationToken));
        }
        else
        {
            await r.Service.ApproveReservedResumeAsync(ChirpConfirmation(review), TestContext.Current.CancellationToken);
            r.Admission.RevokeAt = r.Admission.Reads + 3;
            await Assert.ThrowsAsync<InvalidOperationException>(() => r.Service.TryClaimAuthorizedAsync(r.Admission.Policy!.ExecutionEnvelope,
                r.Intent.ProjectId, "USD", 2, 1, ChirpClaim(r.Intent, r.Reserved), TestContext.Current.CancellationToken));
        }
        Assert.Equal(before, await CatalogRowsAsync(f));
        using var db = f.CreateDbContext();
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM chirp_reserved_dispatch_mappings").SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(string.Equals(stage, "mapping", StringComparison.Ordinal) ? 0 : 1,
            await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM chirp_reserved_catalog_mappings").SingleAsync(TestContext.Current.CancellationToken));
    }
}
