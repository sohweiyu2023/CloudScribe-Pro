using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Providers.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Fact]
    public async Task ExpiredDiagnosticReservationResumesSameCentThroughNativeMappingClaimAndSingleFakeOutcome()
    {
        using var f = new Fixture();
        await f.InitializeAsync();
        byte[] price = "{\"synthetic\":\"diagnostic-resume-price\"}"u8.ToArray();
        var state = await SentenceDiagnosticAsync(f, Hash(System.Text.Encoding.UTF8.GetString(price)));
        var e = state.Evidence;
        var review = await e.Service.PrepareAsync(state.Intent, TestContext.Current.CancellationToken);
        var reserved = await e.Service.ApproveAsync(ChirpConfirmation(review), TestContext.Current.CancellationToken);
        string held = JsonHash(await FinancialRowsAsync(f, "chirp_article_successor_attempts"));
        var historical = e.Admission.Policy!;
        e.Clock.Now = historical.ExpiresAtUtc.AddSeconds(1);
        var catalog = await SeedReservedProviderAsync(f, e.Clock.GetUtcNow(), state.Intent.Envelope, price);
        var inspected = await e.Service.InspectReservedResumeAdmissionAsync(reserved.AuthorizationId, historical,
            catalog.Id, catalog.ExpiresAtUtc, TestContext.Current.CancellationToken);
        var policy = new ChirpReservationResumePolicy(1, Guid.NewGuid(), new string('a', 64), new string('b', 64),
            historical.ProfileRootSha256, new string('d', 64), e.Clock.GetUtcNow().AddMinutes(10), historical,
            reserved.AuthorizationId, inspected.OriginalAttemptRowSha256, inspected.CleanQueueRowSha256, inspected.FinancialSha256,
            inspected.OriginalApprovedAtUtc, inspected.OriginalEnvelope, inspected.OriginalEnvelope with { RequestRevision = 2 },
            catalog.Id, catalog.ExpiresAtUtc, inspected.ProviderEvidenceSha256, new string('e', 64), new string('f', 64), new string('1', 64), 1, 0, 0);
        var admission = new SyntheticResumeAdmission { Policy = policy };
        var service = new ChirpArticleService(f, e.Admission, f.Paths, e.Clock, admission);
        var intent = state.Intent with { Envelope = policy.ExecutionEnvelope with { ChirpAuthorizationId = null } };
        var r = new ReservedResumeFixture(e, service, admission, reserved, intent);
        review = await ResumeReviewAsync(r);
        Assert.True(review.IsCatalogRevalidation);
        Assert.Equal(reserved, await service.ApproveReservedResumeAsync(ChirpConfirmation(review), TestContext.Current.CancellationToken));
        Assert.Equal(held, JsonHash(await FinancialRowsAsync(f, "chirp_article_successor_attempts")));
        var restarted = new ChirpArticleService(f, e.Admission, f.Paths, e.Clock, admission);
        var persisted = new GoogleGenerationPersistedQueueStateStore(f);
        int calls = 0;
        var request = new GenerationProviderRequest(GoogleGenerationProvider.StableProviderId, GoogleGenerationProvider.SynthesizeOperationStableId,
            policy.ExecutionEnvelope.AccountId, reserved.IdempotencyKey, intent.CompiledPayload, "MP3");
        var bound = new GoogleGenerationBoundQueueCoordinator(new((_, _, _) =>
        {
            calls++;
            return Task.FromResult(new GenerationProviderResponse(SubmissionDisposition.Accepted, null, new byte[128], "audio/mpeg", null, "google-accepted"));
        }), persisted.LoadAsync, persisted.SaveAsync,
            (claim, token) => restarted.TryClaimAuthorizedAsync(policy.ExecutionEnvelope, intent.ProjectId, "USD", 2, 1, claim, token), persisted.SaveOutcomeAsync);
        var outcome = await bound.ProcessDurableAsync(request, ChirpTrust(intent), true, true, true, true, TestContext.Current.CancellationToken);
        Assert.True(File.Exists(await restarted.PersistAcceptedAsync(request, outcome, TestContext.Current.CancellationToken)));
        Assert.Equal(1, calls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.LoadSpendAsync(policy.ExecutionEnvelope, TestContext.Current.CancellationToken));
        using var db = f.CreateDbContext();
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM chirp_article_successor_attempts").SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM chirp_reserved_catalog_mappings").SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM chirp_reserved_dispatch_mappings").SingleAsync(TestContext.Current.CancellationToken));
        Assert.NotNull(await db.Database.SqlQueryRaw<long?>("SELECT OutcomeObservedAtUnixMilliseconds AS Value FROM google_generation_queue_states WHERE LocalAttemptId IS NOT NULL ORDER BY rowid DESC LIMIT 1").SingleAsync(TestContext.Current.CancellationToken));
    }
}
