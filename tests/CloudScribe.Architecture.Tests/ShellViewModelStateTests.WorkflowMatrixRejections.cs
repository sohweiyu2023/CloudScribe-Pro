using System.Security.Cryptography;
using CloudScribe.App.Composition;
using CloudScribe.Domain.Documents;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Architecture.Tests;

public sealed partial class ShellViewModelStateTests
{
    [Fact]
    public async Task WorkflowMatrixCanaryIsReachableOnlyAfterExplicitApprovalAndNeverRetries()
    {
        var token = TestContext.Current.CancellationToken;
        await using var f = await CatalogReopenFixture.CreateAsync();
        await f.InitializeNativeCompilerAsync();
        var source = await f.Disk.NewLibrary().CreateAsync("Synthetic canary control", MatrixSource("plain"), token);
        using var model = f.NewModel();
        await f.OpenAsync(model);
        await f.SelectionAsync(model, source, MatrixVoice("neural2"));
        var pipeline = new MatrixPipeline(f); pipeline.Bind(model);
        await model.PrepareGoogleGenerationSpendReviewAsync(token);
        await model.GenerateWithGoogleCommand.ExecuteAsync(null);
        Assert.Equal(0, pipeline.CanarySubmissions);
        var review = await model.PrepareGoogleGenerationSpendReviewAsync(token);
        await model.ApproveGoogleGenerationSpendAsync(review.CurrentEstimateMinorUnits, true, token);
        Assert.True(model.CanGenerateWithGoogle);
        await model.GenerateWithGoogleCommand.ExecuteAsync(null);
        Assert.Equal(1, pipeline.CanarySubmissions);
        Assert.False(model.CanGenerateWithGoogle);
        await model.GenerateWithGoogleCommand.ExecuteAsync(null);
        Assert.Equal(1, pipeline.CanarySubmissions);
        Assert.Equal(0, f.Handler.PaidRequests);
        Assert.Equal(source, await f.Disk.NewLibrary().OpenAsync(source.Id, token));
    }

    [Theory]
    [InlineData("v1")]
    [InlineData("v2")]
    public async Task WorkflowMatrixExcludedUrlOnlyPoliciesRejectBeforePublishingReview(string omission)
    {
        var token = TestContext.Current.CancellationToken;
        await using var f = await CatalogReopenFixture.CreateAsync();
        var source = await f.Disk.NewLibrary().CreateAsync("Synthetic excluded case", MatrixSource("url-only"), token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => PrepareMatrixNarrationAsync(f, source, omission));
        using var db = f.Factory.CreateDbContext();
        Assert.Single(await db.Documents.ToArrayAsync(token));
        Assert.Single(await db.DocumentRevisions.ToArrayAsync(token));
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM narration_copies").SingleAsync(token));
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM google_generation_spend_authorizations").SingleAsync(token));
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM google_generation_queue_states").SingleAsync(token));
        Assert.Equal(source, await f.Disk.NewLibrary().OpenAsync(source.Id, token));
        Assert.Equal(0, f.Handler.PaidRequests);
    }

    [Theory]
    [InlineData("neural2", "v1", false)]
    [InlineData("neural2", "v2", true)]
    [InlineData("aoede", "v1", true)]
    [InlineData("aoede", "v2", false)]
    public async Task WorkflowMatrixSourceEditRejectsApprovalAndPreservesCommittedIdentity(string voice, string omission, bool committed)
    {
        var token = TestContext.Current.CancellationToken;
        await using var f = await CatalogReopenFixture.CreateAsync();
        await f.InitializeNativeCompilerAsync();
        int vaultWrites = f.Vault.Writes;
        var library = f.Disk.NewLibrary();
        var source = await library.CreateAsync("Synthetic edited source", MatrixSource("unicode"), token);
        string originalFile = Assert.Single(Directory.GetFiles(f.Disk.Paths.DocumentsDirectory, "*.utf8", SearchOption.AllDirectories));
        string originalHash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(originalFile, token)));
        var narration = await PrepareMatrixNarrationAsync(f, source, omission);
        using var model = f.NewModel();
        await f.OpenAsync(model);
        await f.SelectionAsync(model, narration, MatrixVoice(voice));
        var pipeline = new MatrixPipeline(f); pipeline.Bind(model);
        var review = await model.PrepareGoogleGenerationSpendReviewAsync(token);
        var pending = (await pipeline.Pending.ResolveCurrentAsync(token))!;
        var persisted = pending.Snapshot.CurrentState;
        if (committed)
        {
            await model.ApproveGoogleGenerationSpendAsync(review.CurrentEstimateMinorUnits, true, token);
            persisted = await MatrixPersistOutcomeAsync(pipeline, pending, "uncertain", f.Clock.GetUtcNow());
        }
        var edited = await library.SaveAsync(new(source.Id, source.Title, source.Text + " Changed original after review.",
            source.ConcurrencyVersion, DocumentRevisionKind.Checkpoint, "Synthetic source edit"), token);
        Assert.NotEqual(source.CurrentRevisionId, edited.CurrentRevisionId);
        if (!committed) await Assert.ThrowsAsync<InvalidOperationException>(() => model.ApproveGoogleGenerationSpendAsync(review.CurrentEstimateMinorUnits, true, token));
        else await Assert.ThrowsAsync<InvalidOperationException>(() => model.PrepareGoogleGenerationSpendReviewAsync(token));
        Assert.Null(model.PreparedGoogleGenerationSpendReview);
        Assert.False(model.PreparedGoogleGenerationSpendApproved);
        Assert.Null(await pipeline.Pending.ResolveCurrentAsync(token));
        Assert.Null(await pipeline.Submission.ResolveCurrentAsync(token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.Runtime.ResolveAsync(token));
        Assert.Equal(edited, await library.OpenAsync(source.Id, token));
        Assert.Equal(narration, await library.OpenAsync(narration.Id, token));
        Assert.Equal(originalHash, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(originalFile, token))));
        var queue = new GoogleGenerationPersistedQueueStateStore(f.Factory);
        Assert.Equal(persisted, await queue.LoadAsync(persisted.AccountId, persisted.OperationStableId, persisted.IdempotencyKey, token));
        if (committed)
        {
            Assert.NotNull(await new GoogleGenerationSpendAuthorizationStore(f.Factory, f.Clock).LoadApprovedAsync(pending.Envelope, token));
            Assert.False(await queue.TryClaimSubmissionAsync(MatrixClaim(pending, f.Clock.GetUtcNow()), token));
        }
        else Assert.Null(await pipeline.Spend.LoadApprovedAsync(pending.Envelope, token));
        AssertMatrixNoExternalEffects(f, vaultWrites);
    }

    private static void AssertMatrixNoExternalEffects(CatalogReopenFixture f, int vaultWrites)
    {
        Assert.Equal(vaultWrites, f.Vault.Writes);
        Assert.Equal(0, f.Handler.PaidRequests);
    }
}
