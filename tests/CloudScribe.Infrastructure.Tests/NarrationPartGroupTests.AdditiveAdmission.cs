using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Fact]
    public async Task AdditiveEnglishFamilyPreservesLegacyReservationDigestAcrossAcceptedAndUncertainTraffic()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await AdditiveEvidenceAsync(f, 2);
        var oldIntent = ChirpIntent(e.Legacy);
        var oldReview = await e.Service.PrepareAsync(oldIntent, TestContext.Current.CancellationToken);
        var oldApproval = await e.Service.ApproveAsync(ChirpConfirmation(oldReview), TestContext.Current.CancellationToken);
        string retained = JsonHash(Assert.Single(await FinancialRowsAsync(f, "chirp_article_attempts")));
        var firstJob = await AdditionalExecutingJobAsync(f, e.Service, e.First.Document.Id);
        var firstIntent = FullDocumentIntent(e.First, 1);
        var firstReview = await e.Service.PrepareAsync(firstIntent, TestContext.Current.CancellationToken);
        Assert.Equal(150, firstReview.ProjectCommittedMinorUnits); // 148 historical + 1 old hold + 1 independent provision.
        var first = await e.Service.ApproveWholeArticleRequestAsync(firstJob.JobId, firstReview.PreviewId, TestContext.Current.CancellationToken);
        var accepted = await AcceptChirpAsync(f, e.Service, firstIntent, 1, first);
        await e.Service.PersistAcceptedAsync(accepted.Request, accepted.Outcome, TestContext.Current.CancellationToken);
        var secondJob = await AdditionalExecutingJobAsync(f, e.Service, e.Second.Document.Id);
        var secondIntent = FullDocumentIntent(e.Second, 1);
        var secondReview = await e.Service.PrepareAsync(secondIntent, TestContext.Current.CancellationToken);
        var second = await e.Service.ApproveWholeArticleRequestAsync(secondJob.JobId, secondReview.PreviewId, TestContext.Current.CancellationToken);
        Assert.True(await e.Service.TryClaimAuthorizedAsync(secondIntent.Envelope with { ChirpAuthorizationId = second.AuthorizationId },
            secondIntent.ProjectId, "USD", 2, secondIntent.EstimateMinorUnits, ChirpClaim(secondIntent, second), TestContext.Current.CancellationToken));
        var rows = await FinancialRowsAsync(f, "chirp_article_attempts");
        Assert.Equal(retained, JsonHash(rows.Single(x => Equals(x[0], oldApproval.AuthorizationId.ToString("N")))));
        var restarted = new ChirpArticleService(f, e.Store, f.Paths, e.Legacy.Clock);
        Assert.NotNull(await restarted.LoadSpendAsync(oldIntent.Envelope with { ChirpAuthorizationId = oldApproval.AuthorizationId }, TestContext.Current.CancellationToken));
        Assert.True((await restarted.ProgressAsync(e.First.Document.Id, TestContext.Current.CancellationToken)).Complete);
        Assert.Null(await restarted.LoadSpendAsync(secondIntent.Envelope with { ChirpAuthorizationId = second.AuthorizationId }, TestContext.Current.CancellationToken));
        Assert.Equal(e.Legacy.Historical, await new GoogleGenerationPersistedQueueStateStore(f).LoadAsync(e.Legacy.Historical.AccountId,
            e.Legacy.Historical.OperationStableId, e.Legacy.Historical.IdempotencyKey, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("credential")]
    [InlineData("capability")]
    [InlineData("pricing")]
    [InlineData("revision")]
    [InlineData("voice")]
    [InlineData("account")]
    [InlineData("estimate")]
    public async Task AdditiveChangedExactRequestEvidenceFailsBeforeReservation(string change)
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await AdditiveEvidenceAsync(f, 2);
        var intent = FullDocumentIntent(e.First, 1);
        intent = change switch
        {
            "credential" => intent with { Envelope = intent.Envelope with { CredentialReferenceId = "different" } },
            "capability" => intent with { Envelope = intent.Envelope with { CapabilityProvenanceId = "different" } },
            "pricing" => intent with { Envelope = intent.Envelope with { PricingProvenanceId = "different" } },
            "revision" => intent with { Envelope = intent.Envelope with { RequestRevision = 42 } },
            "voice" => intent with { Envelope = intent.Envelope with { VoiceName = "en-US-Chirp3-HD-Aoede" } },
            "account" => intent with { Envelope = intent.Envelope with { AccountId = "different" } },
            _ => intent with { EstimateMinorUnits = 1 },
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => e.Service.PrepareAsync(intent, TestContext.Current.CancellationToken));
        Assert.Empty(await FinancialRowsAsync(f, "chirp_article_attempts"));
    }

    [Fact]
    public async Task AdditiveMissingSiblingCannotBeOmittedFromLegacyAccounting()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await AdditiveEvidenceAsync(f, 2);
        var job = await AdditionalExecutingJobAsync(f, e.Service, e.First.Document.Id);
        var review = await e.Service.PrepareAsync(FullDocumentIntent(e.First, 1), TestContext.Current.CancellationToken);
        _ = await e.Service.ApproveWholeArticleRequestAsync(job.JobId, review.PreviewId, TestContext.Current.CancellationToken);
        e.Store.Catalog = e.Store.Catalog with { Families = [] };
        await Assert.ThrowsAsync<InvalidOperationException>(() => e.Service.PrepareAsync(ChirpIntent(e.Legacy), TestContext.Current.CancellationToken));
        Assert.Single(await FinancialRowsAsync(f, "chirp_article_attempts"));
    }
}
