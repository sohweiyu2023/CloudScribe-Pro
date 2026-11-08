using System.Text.Json;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Documents;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Infrastructure.Persistence;
using CloudScribe.Providers.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Theory]
    [InlineData("source-edit")]
    [InlineData("source-archive")]
    [InlineData("child-edit")]
    [InlineData("copy-hash")]
    public async Task NarrationCopyArticleFreshPrepareRejectsStaleOrTamperedProvenance(string mutation)
    {
        using var f = new Fixture(); await f.InitializeAsync(); var n = await NarrationArticleAsync(f); var e = n.Evidence;
        if (string.Equals(mutation, "source-edit", StringComparison.Ordinal)) await EditNarrationSourceAsync(f, n.Source);
        if (string.Equals(mutation, "child-edit", StringComparison.Ordinal)) await EditNarrationSourceAsync(f, n.Copy.Narration);
        using (var db = f.CreateDbContext())
        {
            if (string.Equals(mutation, "source-archive", StringComparison.Ordinal))
                (await db.Documents.SingleAsync(x => x.Id == n.Source.Id, TestContext.Current.CancellationToken)).Status = (int)DocumentStatus.Archived;
            if (string.Equals(mutation, "copy-hash", StringComparison.Ordinal))
                (await db.DocumentRevisions.SingleAsync(x => x.Id == n.Copy.Narration.CurrentRevisionId, TestContext.Current.CancellationToken)).ContentSha256 = new string('f', 64);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        await Assert.ThrowsAnyAsync<Exception>(() => e.Service.PrepareAsync(ChirpIntent(e), TestContext.Current.CancellationToken));
        Assert.Empty(await FinancialRowsAsync(f, "chirp_article_attempts"));
        var log = Assert.Single(n.Logger.Entries);
        Assert.Equal(1651, log.Event.Id);
        Assert.Contains("NarrationInvalidated", log.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("example.test", log.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(n.Source.Text, log.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(f.Root, log.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NarrationCopyArticleApprovalRechecksOriginalAfterReview()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var n = await NarrationArticleAsync(f); var e = n.Evidence;
        var review = await e.Service.PrepareAsync(ChirpIntent(e), TestContext.Current.CancellationToken);
        await EditNarrationSourceAsync(f, n.Source);
        await Assert.ThrowsAsync<InvalidOperationException>(() => e.Service.ApproveAsync(ChirpConfirmation(review), TestContext.Current.CancellationToken));
        Assert.Empty(await FinancialRowsAsync(f, "chirp_article_attempts"));
        Assert.Empty(await FinancialRowsAsync(f, "chirp_article_plans"));
    }

    [Fact]
    public async Task NarrationCopyArticleStaleUnconsumedDispatchNeverCallsProviderOrReplacesReservation()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var n = await NarrationArticleAsync(f); var e = n.Evidence;
        var intent = ChirpIntent(e);
        var review = await e.Service.PrepareAsync(intent, TestContext.Current.CancellationToken);
        var approved = await e.Service.ApproveAsync(ChirpConfirmation(review), TestContext.Current.CancellationToken);
        await EditNarrationSourceAsync(f, n.Source);
        var envelope = intent.Envelope with { ChirpAuthorizationId = approved.AuthorizationId };
        await Assert.ThrowsAsync<InvalidOperationException>(() => e.Service.LoadSpendAsync(envelope, TestContext.Current.CancellationToken));
        var store = new GoogleGenerationPersistedQueueStateStore(f); int calls = 0;
        var queue = new GoogleGenerationQueueCoordinator((_, _, _) =>
        {
            calls++;
            return Task.FromResult(new GenerationProviderResponse(SubmissionDisposition.Accepted, null, new byte[128], "audio/mpeg", null, "google-accepted"));
        });
        var bound = new GoogleGenerationBoundQueueCoordinator(queue, store.LoadAsync, store.SaveAsync,
            (claim, token) => e.Service.TryClaimAuthorizedAsync(envelope, intent.ProjectId, intent.Currency, intent.Scale, intent.EstimateMinorUnits, claim, token), store.SaveOutcomeAsync);
        var request = new GenerationProviderRequest(GoogleGenerationProvider.StableProviderId, GoogleGenerationProvider.SynthesizeOperationStableId,
            intent.Envelope.AccountId, approved.IdempotencyKey, intent.CompiledPayload, "mp3");
        await Assert.ThrowsAsync<InvalidOperationException>(() => bound.ProcessDurableAsync(request, ChirpTrust(intent), true, true, true, true, TestContext.Current.CancellationToken));
        Assert.Equal(0, calls);
        var row = Assert.Single(await FinancialRowsAsync(f, "chirp_article_attempts"));
        Assert.Equal(approved.AuthorizationId.ToString("N"), row[0]); Assert.Equal(0L, row[16]);
        Assert.Equal(approved.IdempotencyKey, row[7]); Assert.Equal(approved.ReservedMinorUnits, row[13]);
    }

    [Fact]
    public async Task NarrationCopyArticleUsesExactChildPayloadAndRetainsAcceptedReadbackAfterSourceEdit()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var n = await NarrationArticleAsync(f); var e = n.Evidence;
        var intent = ChirpIntent(e);
        using (var wire = JsonDocument.Parse(intent.CompiledPayload))
        {
            Assert.Equal(ChirpNarrationPlanner.Slice(n.Copy.Narration.Text, e.Article.Proposal.Parts[0].Range),
                wire.RootElement.GetProperty("input").GetProperty("text").GetString());
            Assert.DoesNotContain("https://", wire.RootElement.GetRawText(), StringComparison.Ordinal);
        }
        bool observed = false;
        var accepted = await AcceptChirpAsync(f, e.Service, intent, 1, observeProvider: submitted =>
        {
            observed = true;
            Assert.Equal(intent.CompiledPayload.ToArray(), submitted.CompiledPayload.ToArray());
            using var wire = JsonDocument.Parse(submitted.CompiledPayload);
            Assert.Equal(ChirpNarrationPlanner.Slice(n.Copy.Narration.Text, e.Article.Proposal.Parts[0].Range),
                wire.RootElement.GetProperty("input").GetProperty("text").GetString());
        });
        Assert.True(observed);
        Assert.Equal(intent.CompiledPayload.ToArray(), accepted.Request.CompiledPayload.ToArray());
        await EditNarrationSourceAsync(f, n.Source);
        string file = await e.Service.PersistAcceptedAsync(accepted.Request, accepted.Outcome, TestContext.Current.CancellationToken);
        var restarted = new ChirpArticleService(f, e.Admission, f.Paths, e.Clock);
        Assert.Equal(file, await restarted.PersistAcceptedAsync(accepted.Request, accepted.Outcome, TestContext.Current.CancellationToken));
        Assert.Equal(0, await restarted.RecoverLocalOutputsAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, (await restarted.ProgressAsync(e.Document.Id, TestContext.Current.CancellationToken)).SavedCoverageParts);
        Assert.Single(await FinancialRowsAsync(f, "chirp_article_attempts"));
        Assert.Equal(n.Copy.Narration, await f.Library.OpenAsync(n.Copy.Narration.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NarrationCopyArticleConsumedUncertainAttemptRemainsReadableWithoutReplay()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var n = await NarrationArticleAsync(f); var e = n.Evidence;
        var intent = ChirpIntent(e); var review = await e.Service.PrepareAsync(intent, TestContext.Current.CancellationToken);
        var approved = await e.Service.ApproveAsync(ChirpConfirmation(review), TestContext.Current.CancellationToken);
        var envelope = intent.Envelope with { ChirpAuthorizationId = approved.AuthorizationId }; var claim = ChirpClaim(intent, approved);
        Assert.True(await e.Service.TryClaimAuthorizedAsync(envelope, intent.ProjectId, "USD", 2, 1, claim, TestContext.Current.CancellationToken));
        await EditNarrationSourceAsync(f, n.Source);
        Assert.Null(await e.Service.LoadSpendAsync(envelope, TestContext.Current.CancellationToken));
        Assert.False(await e.Service.TryClaimAuthorizedAsync(envelope, intent.ProjectId, "USD", 2, 1, claim, TestContext.Current.CancellationToken));
        var progress = await e.Service.ProgressAsync(e.Document.Id, TestContext.Current.CancellationToken);
        Assert.Equal("Stopped; reconcile result, no automatic retry", progress.Parts[0].Status);
        Assert.Equal(claim, await new GoogleGenerationPersistedQueueStateStore(f).LoadAsync(claim.AccountId, claim.OperationStableId, claim.IdempotencyKey, TestContext.Current.CancellationToken));
        Assert.Single(await FinancialRowsAsync(f, "chirp_article_attempts"));
    }

    [Fact]
    public async Task NarrationCopyCannotInheritOriginalArticleAdmission()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var n = await NarrationArticleAsync(f); var e = n.Evidence;
        var sourceProposal = ChirpArticlePlanCompiler.Compile(n.Source, "cmn-CN", "cmn-CN-Chirp3-HD-Alnilam");
        e.Admission.Policy = e.Admission.Policy! with { Articles = [e.Article with { Proposal = sourceProposal }] };
        await Assert.ThrowsAsync<InvalidOperationException>(() => e.Service.PrepareAsync(ChirpIntent(e), TestContext.Current.CancellationToken));
        Assert.Empty(await FinancialRowsAsync(f, "chirp_article_attempts"));
    }
}
