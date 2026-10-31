using CloudScribe.Infrastructure.Generation;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Fact]
    public async Task CatalogPreviewAfterPolicyExpiryPreservesSameReservationAndGrantsNoAuthorityAcrossRestart()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await ChirpEvidenceAsync(f);
        var intent = ChirpIntent(e); var review = await e.Service.PrepareAsync(intent, TestContext.Current.CancellationToken);
        var approved = await e.Service.ApproveAsync(ChirpConfirmation(review), TestContext.Current.CancellationToken);
        var before = await CatalogRowsAsync(f);
        e.Clock.Now = e.Admission.Policy!.ExpiresAtUtc.AddSeconds(1);
        var fresh = intent with { Envelope = intent.Envelope with { RequestRevision = intent.Envelope.RequestRevision + 1 } };
        Guid catalog = Guid.NewGuid(); var expires = e.Clock.Now.AddMinutes(30);
        var candidate = await e.Service.InspectReservedCatalogCandidateAsync(approved.AuthorizationId, fresh, catalog, expires, TestContext.Current.CancellationToken);
        Assert.False(candidate.GrantsDispatchAuthority); Assert.Equal(0, candidate.AdditionalReservationMinorUnits);
        Assert.False(candidate.CandidateCatalogEvidenceAuthenticated); Assert.False(candidate.IndependentFinancialBaselineReconciled);
        Assert.Equal(approved.IdempotencyKey, candidate.IdempotencyKey);
        Assert.Equal(intent.Envelope with { ChirpAuthorizationId = approved.AuthorizationId }, candidate.OriginalEnvelope);
        Assert.Equal(fresh.Envelope with { ChirpAuthorizationId = approved.AuthorizationId }, candidate.CandidateEnvelope);
        var restarted = new ChirpArticleService(f, e.Admission, f.Paths, e.Clock);
        Assert.Equal(candidate, await restarted.InspectReservedCatalogCandidateAsync(approved.AuthorizationId, fresh, catalog, expires, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => restarted.LoadSpendAsync(candidate.CandidateEnvelope, TestContext.Current.CancellationToken));
        Assert.Equal(before, await CatalogRowsAsync(f));
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("consumed")]
    [InlineData("submitted")]
    [InlineData("unknown")]
    [InlineData("provider")]
    [InlineData("outcome")]
    [InlineData("account")]
    [InlineData("project")]
    [InlineData("credential")]
    [InlineData("payload")]
    [InlineData("estimate")]
    public async Task CatalogPreviewRejectsUnsafeStateOrChangedIdentityWithoutMutation(string change)
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await ChirpEvidenceAsync(f);
        var intent = ChirpIntent(e); var review = await e.Service.PrepareAsync(intent, TestContext.Current.CancellationToken);
        var approved = await e.Service.ApproveAsync(ChirpConfirmation(review), TestContext.Current.CancellationToken);
        if (string.Equals(change, "consumed", StringComparison.Ordinal))
            Assert.True(await e.Service.TryClaimAuthorizedAsync(intent.Envelope with { ChirpAuthorizationId = approved.AuthorizationId },
                intent.ProjectId, intent.Currency, intent.Scale, intent.EstimateMinorUnits, ChirpClaim(intent, approved), TestContext.Current.CancellationToken));
        using (var db = f.CreateDbContext())
        {
            string? sql = change switch
            {
                "submitted" => "UPDATE google_generation_queue_states SET SubmissionAttempted=1 WHERE IdempotencyKey={0}",
                "unknown" => "UPDATE google_generation_queue_states SET UnresolvedSubmission=1 WHERE IdempotencyKey={0}",
                "provider" => "UPDATE google_generation_queue_states SET ProviderRequestId='synthetic-provider' WHERE IdempotencyKey={0}",
                "outcome" => "UPDATE google_generation_queue_states SET OutcomeObservedAtUnixMilliseconds=1 WHERE IdempotencyKey={0}",
                _ => null,
            };
            if (sql is not null) await db.Database.ExecuteSqlRawAsync(sql, [approved.IdempotencyKey], TestContext.Current.CancellationToken);
        }
        var fresh = change switch
        {
            "account" => intent with { Envelope = intent.Envelope with { AccountId = "changed" } },
            "credential" => intent with { Envelope = intent.Envelope with { CredentialReferenceId = "changed" } },
            "project" => intent with { ProjectId = "changed" },
            "payload" => intent with { CompiledPayload = "private altered synthetic payload"u8.ToArray() },
            "estimate" => intent with { EstimateMinorUnits = 2 },
            _ => intent,
        };
        string before = await CatalogRowsAsync(f);
        var expiry = string.Equals(change, "stale", StringComparison.Ordinal) ? e.Clock.Now : e.Clock.Now.AddMinutes(30);
        await Assert.ThrowsAsync<InvalidOperationException>(() => e.Service.InspectReservedCatalogCandidateAsync(approved.AuthorizationId, fresh, Guid.NewGuid(), expiry, TestContext.Current.CancellationToken));
        Assert.Equal(before, await CatalogRowsAsync(f));
    }

    private static async Task<string> CatalogRowsAsync(Fixture f)
    {
        var rows = await FinancialRowsAsync(f, "chirp_article_attempts").ConfigureAwait(false);
        var queue = await FinancialRowsAsync(f, "google_generation_queue_states").ConfigureAwait(false);
        var spend = await FinancialRowsAsync(f, "google_generation_spend_authorizations").ConfigureAwait(false);
        var successors = await FinancialRowsAsync(f, "chirp_article_successor_attempts").ConfigureAwait(false);
        return JsonHash(new { rows, queue, spend, successors });
    }
}
