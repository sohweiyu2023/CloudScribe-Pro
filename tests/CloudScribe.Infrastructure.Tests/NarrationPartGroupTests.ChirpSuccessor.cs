using CloudScribe.Infrastructure.Generation;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Fact]
    public async Task SuccessorIsAppendOnlyResumesOneReservationAndExportsAcceptedCoverage()
    {
        using var fixture = new Fixture(); await fixture.InitializeAsync();
        var state = await CreateSuccessorFixtureAsync(fixture); var evidence = state.Evidence;
        var review = await evidence.Service.PrepareAsync(state.Intent, TestContext.Current.CancellationToken);
        Assert.Equal(state.Parent.AuthorizationId, review.FailedParentAuthorizationId);
        Assert.Equal(149, review.ProjectCommittedMinorUnits);
        var child = await evidence.Service.ApproveAsync(ChirpConfirmation(review), TestContext.Current.CancellationToken);
        Assert.NotEqual(state.Parent.AuthorizationId, child.AuthorizationId);
        Assert.Equal(state.Parent.IdempotencyKey + ":" + child.AuthorizationId.ToString("N"), child.IdempotencyKey);
        var restarted = new ChirpArticleService(fixture, evidence.Admission, fixture.Paths, evidence.Clock);
        var resumed = await restarted.PrepareAsync(state.Intent, TestContext.Current.CancellationToken);
        Assert.Equal(child.AuthorizationId, resumed.ExistingAuthorizationId);
        Assert.Equal(150, resumed.ProjectCommittedMinorUnits);
        Assert.Equal(child, await restarted.ApproveAsync(ChirpConfirmation(resumed), TestContext.Current.CancellationToken));
        var accepted = await AcceptChirpAsync(fixture, restarted, state.Intent, 9);
        string file = await restarted.PersistAcceptedAsync(accepted.Request, accepted.Outcome, TestContext.Current.CancellationToken);
        Assert.True(File.Exists(file));
        Assert.True((await restarted.ProgressAsync(evidence.Document.Id, TestContext.Current.CancellationToken)).Complete);
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.PrepareAsync(state.Intent, TestContext.Current.CancellationToken));
        Assert.Equal(state.ParentRowsSha256, JsonHash(await FinancialRowsAsync(fixture, "chirp_article_attempts")));
        Assert.Single(await FinancialRowsAsync(fixture, "chirp_article_successor_attempts"));
        Assert.Single(await FinancialRowsAsync(fixture, "chirp_article_receipts"));
        var queue = new GoogleGenerationPersistedQueueStateStore(fixture, evidence.Admission);
        Assert.Equal(state.FailedState, await queue.LoadAsync(state.FailedState.AccountId, state.FailedState.OperationStableId,
            state.FailedState.IdempotencyKey, TestContext.Current.CancellationToken));
        var native = new ChirpArticleNative();
        var export = new ArticleAudioExportService(fixture, fixture.Library, queue, fixture.Paths, native, chirpArticles: restarted);
        await export.ExportAsync(evidence.Document.Id, Path.Combine(fixture.Root, "complete.mp3"), CreateArticleTools(fixture.Root), TestContext.Current.CancellationToken);
        Assert.Single(native.InputMarkers);
    }

    [Theory]
    [InlineData("accepted")]
    [InlineData("uncertain")]
    public async Task AcceptedOrUncertainParentCannotReceiveDiagnosticSuccessor(string outcome)
    {
        using var fixture = new Fixture(); await fixture.InitializeAsync();
        var state = await CreateSuccessorFixtureAsync(fixture, outcome);
        await Assert.ThrowsAsync<InvalidOperationException>(() => state.Evidence.Service.PrepareAsync(state.Intent, TestContext.Current.CancellationToken));
        Assert.Empty(await FinancialRowsAsync(fixture, "chirp_article_successor_attempts"));
    }

    [Fact]
    public async Task InterruptedSuccessorApprovalRollsBackChildRegistryAndQueue()
    {
        using var fixture = new Fixture(); await fixture.InitializeAsync(); var state = await CreateSuccessorFixtureAsync(fixture);
        var review = await state.Evidence.Service.PrepareAsync(state.Intent, TestContext.Current.CancellationToken);
        string before = JsonHash(await FinancialRowsAsync(fixture, "google_generation_queue_states"));
        using (var db = fixture.CreateDbContext())
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER synthetic_successor BEFORE INSERT ON chirp_article_successor_attempts BEGIN SELECT RAISE(ABORT,'synthetic-interruption'); END;", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => state.Evidence.Service.ApproveAsync(ChirpConfirmation(review), TestContext.Current.CancellationToken));
        Assert.Empty(await FinancialRowsAsync(fixture, "chirp_article_successor_attempts"));
        Assert.Equal(before, JsonHash(await FinancialRowsAsync(fixture, "google_generation_queue_states")));
    }
}
