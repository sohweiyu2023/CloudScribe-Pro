using System.Text;
using System.Text.Json;
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
    public async Task ChirpAcceptedOutputIsDurableIdempotentAndNeedsNoNewRequestAfterRestart()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await ChirpEvidenceAsync(f); var intent = ChirpIntent(e);
        var accepted = await AcceptChirpAsync(f, e.Service, intent, 1);
        string file = await e.Service.PersistAcceptedAsync(accepted.Request, accepted.Outcome, TestContext.Current.CancellationToken);
        Assert.Equal(file, await e.Service.PersistAcceptedAsync(accepted.Request, accepted.Outcome, TestContext.Current.CancellationToken));
        Assert.Single(await FinancialRowsAsync(f, "chirp_article_attempts"));
        Assert.Single(await FinancialRowsAsync(f, "chirp_article_receipts"));
        var restarted = new ChirpArticleService(f, e.Admission, f.Paths, e.Clock);
        Assert.Equal(0, await restarted.RecoverLocalOutputsAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, (await restarted.ProgressAsync(e.Document.Id, TestContext.Current.CancellationToken)).SavedCoverageParts);
        var next = await restarted.PrepareAsync(ChirpIntent(e, 2), TestContext.Current.CancellationToken);
        Assert.Equal(149, next.ProjectCommittedMinorUnits);
        using var db = f.CreateDbContext();
        Assert.Equal(61, await db.Documents.CountAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChirpJournalRepairsReceiptOrCompletedPendingFileWithoutRegenerating(bool pendingFile)
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await ChirpEvidenceAsync(f); var intent = ChirpIntent(e);
        var accepted = await AcceptChirpAsync(f, e.Service, intent, 1);
        using (var db = f.CreateDbContext())
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER synthetic_chirp_receipt BEFORE INSERT ON chirp_article_receipts BEGIN SELECT RAISE(ABORT,'synthetic-output-interruption'); END;", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<SqliteException>(() => e.Service.PersistAcceptedAsync(accepted.Request, accepted.Outcome, TestContext.Current.CancellationToken));
        Assert.Empty(await FinancialRowsAsync(f, "chirp_article_receipts"));
        string file = Assert.Single(Directory.GetFiles(Path.Combine(f.Root, "accepted-chirp-article-audio"), "*.mp3"));
        if (pendingFile) File.Move(file, file + ".pending");
        using (var db = f.CreateDbContext()) await db.Database.ExecuteSqlRawAsync("DROP TRIGGER synthetic_chirp_receipt;", TestContext.Current.CancellationToken);
        e.Admission.Policy = null; e.Clock.Now = e.Clock.Now.AddHours(2); // Local recovery is not paid authority or renewal.
        var restarted = new ChirpArticleService(f, e.Admission, f.Paths, e.Clock);
        Assert.Equal(1, await restarted.RecoverLocalOutputsAsync(TestContext.Current.CancellationToken));
        Assert.Single(await FinancialRowsAsync(f, "chirp_article_receipts"));
        Assert.Single(await FinancialRowsAsync(f, "chirp_article_attempts"));
        Assert.Equal(accepted.Outcome.Response!.MediaBytes.ToArray(), await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ChirpMissingPartialOrFailedOutputRetainsHoldAndBlocksFurtherRequests()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await ChirpEvidenceAsync(f); var intent = ChirpIntent(e);
        var accepted = await AcceptChirpAsync(f, e.Service, intent, 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => e.Service.PrepareAsync(ChirpIntent(e, 2), TestContext.Current.CancellationToken));
        string file = await e.Service.PersistAcceptedAsync(accepted.Request, accepted.Outcome, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(file, [0], TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => e.Service.PrepareAsync(ChirpIntent(e, 2), TestContext.Current.CancellationToken));
        Assert.Single(await FinancialRowsAsync(f, "chirp_article_attempts"));
        Assert.Equal(1L, (await FinancialRowsAsync(f, "chirp_article_attempts"))[0][13]);
    }

    private static async Task<(GenerationProviderRequest Request, GoogleGenerationQueueOutcome Outcome)> AcceptChirpAsync(Fixture f,
        ChirpArticleService service, ChirpRequestIntent intent, int marker)
    {
        var review = await service.PrepareAsync(intent, TestContext.Current.CancellationToken).ConfigureAwait(false);
        var approved = await service.ApproveAsync(ChirpConfirmation(review), TestContext.Current.CancellationToken).ConfigureAwait(false);
        byte[] audio = new byte[128]; Encoding.ASCII.GetBytes("ID3").CopyTo(audio, 0); audio[10] = checked((byte)marker);
        var request = new GenerationProviderRequest(GoogleGenerationProvider.StableProviderId, GoogleGenerationProvider.SynthesizeOperationStableId,
            intent.Envelope.AccountId, approved.IdempotencyKey, intent.CompiledPayload, "mp3");
        var persisted = new GoogleGenerationPersistedQueueStateStore(f); int fakeCalls = 0;
        var queue = new GoogleGenerationQueueCoordinator((_, _, _) =>
        { fakeCalls++; return Task.FromResult(new GenerationProviderResponse(SubmissionDisposition.Accepted, null, audio, "audio/mpeg", null, "google-accepted")); });
        var bound = new GoogleGenerationBoundQueueCoordinator(queue, persisted.LoadAsync, persisted.SaveAsync,
            (claim, token) => service.TryClaimAuthorizedAsync(intent.Envelope with { ChirpAuthorizationId = approved.AuthorizationId }, intent.ProjectId,
                intent.Currency, intent.Scale, intent.EstimateMinorUnits, claim, token), persisted.SaveOutcomeAsync);
        var outcome = await bound.ProcessDurableAsync(request, ChirpTrust(intent), true, true, true, true, TestContext.Current.CancellationToken).ConfigureAwait(false);
        Assert.Equal(1, fakeCalls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => bound.ProcessDurableAsync(request, ChirpTrust(intent), true, true, true, true, TestContext.Current.CancellationToken)).ConfigureAwait(false);
        Assert.Equal(1, fakeCalls); return (request, outcome);
    }

    private static GenerationCacheTrustContext ChirpTrust(ChirpRequestIntent intent) => new(GoogleGenerationProvider.StableProviderId,
        intent.Envelope.AccountId, intent.ProjectId, "synthetic-endpoint", "global", GoogleGenerationProvider.SynthesizeOperationStableId,
        "synthetic-model", intent.Envelope.VoiceName, "synthetic-fingerprint", "synthetic-plan", "cmn-CN", "synthetic-controls", "MP3",
        "synthetic-sample", "synthetic-adapter", "synthetic-compiler", "synthetic-ast", "synthetic-normalization",
        intent.Envelope.PricingProvenanceId, intent.Envelope.CapabilityProvenanceId, "synthetic-governance", "synthetic-feature", "synthetic-account-capability");
}
