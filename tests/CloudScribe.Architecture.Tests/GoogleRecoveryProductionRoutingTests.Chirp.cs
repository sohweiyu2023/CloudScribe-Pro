using CloudScribe.App.Composition;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Providers.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Architecture.Tests;

public sealed partial class GoogleRecoveryProductionRoutingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChirpActualPreparationResumesOneReservationActivatesExactSliceAndStoresFakeAcceptedAudio(bool lostActivationAcknowledgement)
    {
        using var f = new Fixture(); await f.InitializeChirpAsync();
        var articles = f.CreateArticles(); var spend = new GoogleGenerationSpendAuthorizationStore(f, TimeProvider.System, articles: articles);
        var owner = new GoogleGenerationProductionSubmissionStateOwner(lostActivationAcknowledgement ? new FailActivationOnce(spend) : spend);
        var preparation = new ChirpArticlePreparationService(articles, f.ChirpCompiler, owner, TimeProvider.System);
        var review = await preparation.PrepareAsync(f.Selection, 1, TestContext.Current.CancellationToken);
        Assert.Null(await owner.ResolveCurrentAsync(TestContext.Current.CancellationToken));
        if (lostActivationAcknowledgement)
            await Assert.ThrowsAsync<IOException>(() => preparation.ApproveAsync(f.Selection, ChirpConfirm(review), TestContext.Current.CancellationToken));
        else await preparation.ApproveAsync(f.Selection, ChirpConfirm(review), TestContext.Current.CancellationToken);
        Guid original;
        using (var db = f.CreateDbContext()) original = Guid.ParseExact(await db.Database.SqlQueryRaw<string>("SELECT AuthorizationId AS Value FROM chirp_article_attempts").SingleAsync(TestContext.Current.CancellationToken), "N");
        articles = f.CreateArticles(); spend = new(f, TimeProvider.System, articles: articles); owner = new(spend);
        preparation = new(articles, f.ChirpCompiler, owner, TimeProvider.System);
        var resumed = await preparation.PrepareAsync(f.Selection, 1, TestContext.Current.CancellationToken);
        Assert.Equal(original, resumed.ExistingAuthorizationId);
        await preparation.ApproveAsync(f.Selection, ChirpConfirm(resumed), TestContext.Current.CancellationToken);
        var active = (await owner.ResolveCurrentAsync(TestContext.Current.CancellationToken))!;
        Assert.Equal(original, active.SubmissionEnvelope.ChirpAuthorizationId);
        _ = await new GoogleGenerationProductionRuntimeRequestSource(spend, owner.ResolveCurrentAsync).ResolveAsync(TestContext.Current.CancellationToken);
        var observed = await preparation.CompileReadonlyAsync(f.Selection, TestContext.Current.CancellationToken);
        Assert.Equal(active.Snapshot.ProviderRequest.CompiledPayload.ToArray(), observed.Snapshot.ProviderRequest.CompiledPayload.ToArray());
        Assert.DoesNotContain(f.Selection.ExactText, System.Text.Encoding.UTF8.GetString(observed.Snapshot.ProviderRequest.CompiledPayload.Span), StringComparison.Ordinal);
        int fakeCalls = 0;
        var queue = new GoogleGenerationQueueCoordinator((_, _, _) =>
        { fakeCalls++; return Task.FromResult(new GenerationProviderResponse(SubmissionDisposition.Accepted, null, new byte[128], "audio/mpeg", null, "google-accepted")); });
        var persisted = new GoogleGenerationPersistedQueueStateStore(f);
        var bound = new GoogleGenerationBoundQueueCoordinator(queue, persisted.LoadAsync, persisted.SaveAsync,
            (claim, token) => articles.TryClaimAuthorizedAsync(active.SubmissionEnvelope, f.Selection.ProjectStableId, "USD", 2, 1, claim, token), persisted.SaveOutcomeAsync);
        var outcome = await bound.ProcessDurableAsync(active.Snapshot.ProviderRequest, active.Snapshot.AdmittedTrust, true, true, true, true, TestContext.Current.CancellationToken);
        string file = await articles.PersistAcceptedAsync(active.Snapshot.ProviderRequest, outcome, TestContext.Current.CancellationToken);
        Assert.True(File.Exists(file)); Assert.Equal(1, fakeCalls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => bound.ProcessDurableAsync(active.Snapshot.ProviderRequest, active.Snapshot.AdmittedTrust, true, true, true, true, TestContext.Current.CancellationToken));
        Assert.Equal(1, fakeCalls);
        await AssertChirpRoutingRowsAsync(f);
    }

    private static async Task AssertChirpRoutingRowsAsync(Fixture f)
    {
        using var final = f.CreateDbContext();
        Assert.Equal(1, await final.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM chirp_article_attempts").SingleAsync(TestContext.Current.CancellationToken).ConfigureAwait(false));
        Assert.Equal(1, await final.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM chirp_article_receipts").SingleAsync(TestContext.Current.CancellationToken).ConfigureAwait(false));
        Assert.Equal(0, await final.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM google_generation_spend_authorizations").SingleAsync(TestContext.Current.CancellationToken).ConfigureAwait(false));
    }

    [Theory]
    [InlineData("capability")]
    [InlineData("account")]
    [InlineData("document")]
    [InlineData("text")]
    public async Task ChirpChangedNativeOrCompilerEvidenceCannotActivateOrReserve(string change)
    {
        using var f = new Fixture(); await f.InitializeChirpAsync(); var articles = f.CreateArticles();
        var owner = new GoogleGenerationProductionSubmissionStateOwner(new GoogleGenerationSpendAuthorizationStore(f, TimeProvider.System, articles: articles));
        var prep = new ChirpArticlePreparationService(articles, f.ChirpCompiler, owner, TimeProvider.System);
        var review = await prep.PrepareAsync(f.Selection, 1, TestContext.Current.CancellationToken);
        var selection = change switch
        {
            "account" => f.Selection with { AccountStableId = "changed" },
            "document" => f.Selection with { DocumentId = Guid.NewGuid() },
            "text" => f.Selection with { ExactText = f.Selection.ExactText + " changed" },
            _ => f.Selection
        };
        if (string.Equals(change, "capability", StringComparison.Ordinal)) f.ChirpCompiler.Capability = "changed";
        await Assert.ThrowsAsync<InvalidOperationException>(() => prep.ApproveAsync(selection, ChirpConfirm(review), TestContext.Current.CancellationToken));
        Assert.Null(await owner.ResolveCurrentAsync(TestContext.Current.CancellationToken));
        using var db = f.CreateDbContext();
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM chirp_article_attempts").SingleAsync(TestContext.Current.CancellationToken));
    }

    private static ChirpApprovalConfirmation ChirpConfirm(ChirpRequestReview review) => new(review.PreviewId, review.ReviewSha256, review.MaximumMinorUnits, true, true);
}
