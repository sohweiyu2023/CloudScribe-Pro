using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Providers.Abstractions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Fact]
    public async Task ResumeSupersessionAndDispatchClaimHaveOnlyOneTransactionalWinner()
    {
        using var f = new Fixture(); var r = await ReservedResumeAsync(f);
        var first = await ResumeReviewAsync(r); await r.Service.ApproveReservedResumeAsync(ChirpConfirmation(first), TestContext.Current.CancellationToken);
        var old = r.Admission.Policy!;
        var nextStore = new SyntheticResumeAdmission { Policy = old with { MappingId = Guid.NewGuid(), SourceApprovalSha256 = new string('2', 64), PriorMappingSha256 = ChirpPolicyValidation.Hash(old) } };
        var next = new ChirpArticleService(f, r.Old.Admission, f.Paths, r.Old.Clock, nextStore);
        var nextReview = await ResumeReviewAsync(r, next);
        async Task<bool> MapAsync()
        {
            try { await next.ApproveReservedResumeAsync(ChirpConfirmation(nextReview), TestContext.Current.CancellationToken).ConfigureAwait(false); return true; }
            catch (InvalidOperationException) { return false; }
        }
        async Task<bool> ClaimAsync()
        {
            try
            {
                return await r.Service.TryClaimAuthorizedAsync(old.ExecutionEnvelope, r.Intent.ProjectId, "USD", 2, 1,
                ChirpClaim(r.Intent, r.Reserved), TestContext.Current.CancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException) { return false; }
        }
        Assert.Single(await Task.WhenAll(Task.Run(MapAsync, TestContext.Current.CancellationToken), Task.Run(ClaimAsync, TestContext.Current.CancellationToken)), x => x);
        using var db = f.CreateDbContext();
        int consumed = await db.Database.SqlQueryRaw<int>("SELECT Consumed AS Value FROM chirp_article_attempts").SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(consumed, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM chirp_reserved_dispatch_mappings").SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2 - consumed, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM chirp_reserved_catalog_mappings").SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT SUM(ReservedMinorUnits) AS Value FROM chirp_article_attempts").SingleAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AcceptedOutputRecoveryUsesDispatchMappingAfterSupersessionAndExpiryWithoutRegeneration()
    {
        using var f = new Fixture(); var r = await ReservedResumeAsync(f);
        var first = await ResumeReviewAsync(r); await r.Service.ApproveReservedResumeAsync(ChirpConfirmation(first), TestContext.Current.CancellationToken);
        var prior = r.Admission.Policy!;
        var used = prior with { MappingId = Guid.NewGuid(), SourceApprovalSha256 = new string('2', 64), PriorMappingSha256 = ChirpPolicyValidation.Hash(prior) };
        r.Admission.Policy = used;
        var second = await ResumeReviewAsync(r); await r.Service.ApproveReservedResumeAsync(ChirpConfirmation(second), TestContext.Current.CancellationToken);
        var request = new GenerationProviderRequest(GoogleGenerationProvider.StableProviderId, GoogleGenerationProvider.SynthesizeOperationStableId,
            used.ExecutionEnvelope.AccountId, r.Reserved.IdempotencyKey, r.Intent.CompiledPayload, "MP3");
        var states = new GoogleGenerationPersistedQueueStateStore(f); int calls = 0;
        var bound = new GoogleGenerationBoundQueueCoordinator(new((_, _, _) =>
        { calls++; return Task.FromResult(new GenerationProviderResponse(SubmissionDisposition.Accepted, null, new byte[128], "audio/mpeg", null, "google-accepted")); }),
            states.LoadAsync, states.SaveAsync, (claim, token) => r.Service.TryClaimAuthorizedAsync(used.ExecutionEnvelope, r.Intent.ProjectId, "USD", 2, 1, claim, token), states.SaveOutcomeAsync);
        var outcome = await bound.ProcessDurableAsync(request, ChirpTrust(r.Intent), true, true, true, true, TestContext.Current.CancellationToken);
        using (var db = f.CreateDbContext()) await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER synthetic_resume_output BEFORE INSERT ON chirp_article_receipts BEGIN SELECT RAISE(ABORT,'synthetic-output-interruption'); END;", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<SqliteException>(() => r.Service.PersistAcceptedAsync(request, outcome, TestContext.Current.CancellationToken));
        using (var db = f.CreateDbContext()) await db.Database.ExecuteSqlRawAsync("DROP TRIGGER synthetic_resume_output;", TestContext.Current.CancellationToken);
        r.Old.Clock.Now = used.ExpiresAtUtc.AddHours(1); r.Admission.Policy = null;
        var restarted = new ChirpArticleService(f, r.Old.Admission, f.Paths, r.Old.Clock);
        Assert.Equal(1, await restarted.RecoverLocalOutputsAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, calls);
        using var final = f.CreateDbContext();
        string receipt = await final.Database.SqlQueryRaw<string>("SELECT ReceiptJson AS Value FROM chirp_article_receipts").SingleAsync(TestContext.Current.CancellationToken);
        Assert.Contains(ChirpPolicyValidation.Hash(used), receipt, StringComparison.Ordinal);
        Assert.DoesNotContain(ChirpPolicyValidation.Hash(prior), receipt, StringComparison.Ordinal);
        Assert.Equal(1, (await restarted.ProgressAsync(r.Old.Document.Id, TestContext.Current.CancellationToken)).SavedCoverageParts);
    }
}
