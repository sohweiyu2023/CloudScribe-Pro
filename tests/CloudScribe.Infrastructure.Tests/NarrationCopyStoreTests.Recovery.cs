#pragma warning disable MA0004 // Synthetic tests have no synchronization context to preserve.
using CloudScribe.Infrastructure.Persistence.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationCopyStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DatabaseFailureRollsBackWholePublicationAndRetryReusesFlushedBlob(bool importing)
    {
        await using var fixture = await Fixture.CreateAsync();
        Guid batchId = Guid.NewGuid();
        Guid itemId = Guid.NewGuid();
        var source = await fixture.SourceAsync();
        using var db = fixture.Factory.CreateDbContext();
        string failureTrigger = importing ? "CREATE TRIGGER reject_test_publish BEFORE INSERT ON batch_source_imports BEGIN SELECT RAISE(ABORT,'injected'); END;" : "CREATE TRIGGER reject_test_publish BEFORE INSERT ON narration_copies BEGIN SELECT RAISE(ABORT,'injected'); END;";
        await db.Database.ExecuteSqlRawAsync(
            failureTrigger, Token);
        async Task AttemptAsync()
        {
            if (importing)
                await fixture.Store().ImportBatchSourceAsync(batchId, itemId, Hash("raw"), "Input", SourceText, Token);
            else
                await PrepareAsync(fixture.Store(), source);
        }
        await Assert.ThrowsAsync<DbUpdateException>(AttemptAsync);
        Assert.Single(await db.Documents.ToArrayAsync(Token));
        Assert.Single(await db.DocumentRevisions.ToArrayAsync(Token));
        Assert.Empty(await db.Set<NarrationCopyEntity>().ToArrayAsync(Token));
        Assert.Empty(await db.Set<BatchSourceImportEntity>().ToArrayAsync(Token));
        string[] afterFailure = fixture.Blobs().Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(2, afterFailure.Length);
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_test_publish;", Token);
        await AttemptAsync();
        Assert.Equal(afterFailure, fixture.Blobs().Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(2, await db.Documents.CountAsync(Token));
        Assert.Equal(2, await db.DocumentRevisions.CountAsync(Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostCommitAcknowledgementRetainsCommittedBlobAndExactIdentity(bool importing)
    {
        await using var fixture = await Fixture.CreateAsync();
        var source = await fixture.SourceAsync();
        Guid batchId = Guid.NewGuid();
        Guid itemId = Guid.NewGuid();
        fixture.Fault.Armed = true;
        if (importing)
            await Assert.ThrowsAsync<IOException>(() => fixture.Store().ImportBatchSourceAsync(batchId, itemId, Hash("raw"), "Input", SourceText, Token));
        else
            await Assert.ThrowsAsync<IOException>(() => PrepareAsync(fixture.Store(), source));
        using var db = fixture.Factory.CreateDbContext();
        var durable = await db.Documents.AsNoTracking().SingleAsync(row => row.Id != source.Id, Token);
        string[] files = fixture.Blobs().Order(StringComparer.Ordinal).ToArray();
        var recovered = importing
            ? await fixture.Store().ImportBatchSourceAsync(batchId, itemId, Hash("raw"), "Input", SourceText, Token)
            : (await PrepareAsync(fixture.Store(), source)).Narration;
        Assert.Equal(durable.Id, recovered.Id);
        Assert.Equal(durable.CurrentRevisionId, recovered.CurrentRevisionId);
        Assert.Equal(files, fixture.Blobs().Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(recovered, await fixture.Library.OpenAsync(recovered.Id, Token));
        Assert.Equal(2, await db.Documents.CountAsync(Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompetingStoreInstancesPublishOnlyOneIdentity(bool importing)
    {
        await using var fixture = await Fixture.CreateAsync();
        var source = await fixture.SourceAsync();
        Guid batchId = Guid.NewGuid();
        Guid itemId = Guid.NewGuid();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
        {
            await start.Task.WaitAsync(Token);
            return importing
                ? await fixture.Store().ImportBatchSourceAsync(batchId, itemId, Hash("raw"), "Input", SourceText, Token)
                : (await PrepareAsync(fixture.Store(), source)).Narration;
        }, Token)).ToArray();
        start.SetResult();
        var snapshots = await Task.WhenAll(tasks);
        Assert.All(snapshots, snapshot => Assert.Equal(snapshots[0], snapshot));
        using var db = fixture.Factory.CreateDbContext();
        Assert.Equal(2, await db.Documents.CountAsync(Token));
        Assert.Equal(2, await db.DocumentRevisions.CountAsync(Token));
        Assert.Equal(2, fixture.Blobs().Length);
    }

    [Fact]
    public async Task ProvenanceIsAppendOnlyAndCannotBeDeletedOrOverwritten()
    {
        await using var fixture = await Fixture.CreateAsync();
        var source = await fixture.Store().ImportBatchSourceAsync(Guid.NewGuid(), Guid.NewGuid(), Hash("raw"), "Input", SourceText, Token);
        await PrepareAsync(fixture.Store(), source);
        using var db = fixture.Factory.CreateDbContext();
        foreach (string sql in new[]
        {
            "UPDATE batch_source_imports SET RawSha256=RawSha256",
            "DELETE FROM batch_source_imports",
            "UPDATE narration_copies SET OmittedUrlCount=OmittedUrlCount",
            "DELETE FROM narration_copies",
        })
            await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync(sql, Token));
    }
}
