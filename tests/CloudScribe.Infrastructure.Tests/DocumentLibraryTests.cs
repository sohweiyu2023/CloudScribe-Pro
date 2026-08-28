using CloudScribe.Application.Documents;
using CloudScribe.Domain.Documents;
using CloudScribe.Infrastructure.Configuration;
using CloudScribe.Infrastructure.Files;
using CloudScribe.Infrastructure.Persistence;
using CloudScribe.Infrastructure.Persistence.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CloudScribe.Infrastructure.Tests;

public sealed class DocumentLibraryTests
{
    [Fact]
    public async Task CreateSaveReopenAndSearchUseDurableVerifiedRevisionFiles()
    {
        await using TestLibraryFixture fixture = await TestLibraryFixture.CreateAsync();

        DocumentSnapshot created = await fixture.Library.CreateAsync(
            "Local draft",
            "alpha βeta 你好 👩🏽‍🚀",
            TestContext.Current.CancellationToken);
        Assert.Equal(1, created.ConcurrencyVersion);
        Assert.NotNull(created.CurrentRevisionId);

        DocumentSnapshot saved = await fixture.Library.SaveAsync(
            new DocumentSaveRequest(
                created.Id,
                "Local draft renamed",
                "alpha βeta 你好 👩🏽‍🚀\nsecond line",
                created.ConcurrencyVersion,
                DocumentRevisionKind.Checkpoint,
                "Manual checkpoint"),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, saved.ConcurrencyVersion);
        Assert.Equal("Local draft renamed", saved.Title);
        Assert.Equal("alpha βeta 你好 👩🏽‍🚀\nsecond line", saved.Text);

        DocumentSnapshot reopened = Assert.IsType<DocumentSnapshot>(
            await fixture.Library.OpenAsync(saved.Id, TestContext.Current.CancellationToken));
        Assert.Equal(saved.Text, reopened.Text);
        Assert.Equal(saved.CurrentRevisionId, reopened.CurrentRevisionId);

        IReadOnlyList<DocumentSummary> titleMatches = await fixture.Library.SearchAsync(
            "renamed",
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(titleMatches);
        Assert.Equal(saved.Id, titleMatches[0].Id);

        IReadOnlyList<DocumentSummary> bodyMatches = await fixture.Library.SearchAsync(
            "second line",
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(bodyMatches);

        await using CloudScribeDbContext context = fixture.Factory.CreateDbContext();
        DocumentRevisionEntity[] revisions = await context.DocumentRevisions
            .Where(item => item.DocumentId == saved.Id)
            .OrderBy(item => item.CreatedAtUnixMilliseconds)
            .ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, revisions.Length);
        Assert.All(revisions, revision =>
        {
            Assert.False(string.IsNullOrWhiteSpace(revision.ContentRelativePath));
            Assert.True(revision.ContentByteLength > 0);
            Assert.Equal(64, revision.ContentSha256.Length);
        });
    }

    [Fact]
    public async Task StaleWriterFailsWithoutCreatingAnotherRevision()
    {
        await using TestLibraryFixture fixture = await TestLibraryFixture.CreateAsync();
        DocumentSnapshot created = await fixture.Library.CreateAsync(
            "Concurrency",
            "v1",
            TestContext.Current.CancellationToken);
        DocumentSnapshot current = await fixture.Library.SaveAsync(
            new DocumentSaveRequest(
                created.Id,
                created.Title,
                "v2",
                created.ConcurrencyVersion,
                DocumentRevisionKind.Autosave),
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<DocumentConcurrencyException>(() => fixture.Library.SaveAsync(
            new DocumentSaveRequest(
                created.Id,
                created.Title,
                "stale",
                created.ConcurrencyVersion,
                DocumentRevisionKind.Autosave),
            TestContext.Current.CancellationToken));

        await using CloudScribeDbContext context = fixture.Factory.CreateDbContext();
        Assert.Equal(2, await context.DocumentRevisions.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(current.ConcurrencyVersion, (await context.Documents.SingleAsync(TestContext.Current.CancellationToken)).ConcurrencyVersion);
    }

    [Fact]
    public async Task ArchiveDeleteAndUndoAreVersionedAndExcludedFromActiveLibrary()
    {
        await using TestLibraryFixture fixture = await TestLibraryFixture.CreateAsync();
        DocumentSnapshot created = await fixture.Library.CreateAsync(
            "Lifecycle",
            "body",
            TestContext.Current.CancellationToken);

        DocumentSummary archived = await fixture.Library.ChangeStatusAsync(
            created.Id,
            DocumentStatus.Archived,
            created.ConcurrencyVersion,
            TestContext.Current.CancellationToken);
        Assert.Equal(DocumentStatus.Archived, archived.Status);
        Assert.Empty(await fixture.Library.ListAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Single(await fixture.Library.ListAsync(DocumentStatus.Archived, cancellationToken: TestContext.Current.CancellationToken));

        DocumentSummary deleted = await fixture.Library.ChangeStatusAsync(
            created.Id,
            DocumentStatus.Deleted,
            archived.ConcurrencyVersion,
            TestContext.Current.CancellationToken);
        Assert.Equal(DocumentStatus.Deleted, deleted.Status);

        DocumentSummary restored = await fixture.Library.ChangeStatusAsync(
            created.Id,
            DocumentStatus.Active,
            deleted.ConcurrencyVersion,
            TestContext.Current.CancellationToken);
        Assert.Equal(DocumentStatus.Active, restored.Status);
        Assert.Single(await fixture.Library.ListAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CorruptedRevisionFileFailsClosedOnReopen()
    {
        await using TestLibraryFixture fixture = await TestLibraryFixture.CreateAsync();
        DocumentSnapshot created = await fixture.Library.CreateAsync(
            "Integrity",
            "trusted bytes",
            TestContext.Current.CancellationToken);

        await using (CloudScribeDbContext context = fixture.Factory.CreateDbContext())
        {
            DocumentRevisionEntity revision = await context.DocumentRevisions
                .SingleAsync(TestContext.Current.CancellationToken);
            string path = Path.Combine(
                fixture.Paths.RootDirectory,
                revision.ContentRelativePath!.Replace('/', Path.DirectorySeparatorChar));
            await File.WriteAllTextAsync(path, "tampered bytes", TestContext.Current.CancellationToken);
        }

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Library.OpenAsync(created.Id, TestContext.Current.CancellationToken));
    }

    private sealed class TestLibraryFixture : IAsyncDisposable
    {
        private TestLibraryFixture(
            string root,
            AppPaths paths,
            TestDbContextFactory factory,
            EfDocumentLibrary library)
        {
            Root = root;
            Paths = paths;
            Factory = factory;
            Library = library;
        }

        public string Root { get; }

        public AppPaths Paths { get; }

        public TestDbContextFactory Factory { get; }

        public EfDocumentLibrary Library { get; }

        public static async Task<TestLibraryFixture> CreateAsync()
        {
            string root = Path.Combine(Path.GetTempPath(), "cloudscribe-document-library-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            AppPaths paths = new(Options.Create(new CloudScribeOptions
            {
                AppDataDirectoryOverride = root,
            }));
            paths.EnsureDirectories();

            DbContextOptions<CloudScribeDbContext> options = new DbContextOptionsBuilder<CloudScribeDbContext>()
                .UseSqlite(new SqliteConnectionStringBuilder
                {
                    DataSource = paths.DatabasePath,
                    ForeignKeys = true,
                    DefaultTimeout = 5,
                }.ConnectionString)
                .Options;
            TestDbContextFactory factory = new(options);
            CloudScribeDbContext context = factory.CreateDbContext();
            await using (context.ConfigureAwait(false))
            {
                await context.Database.MigrateAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
            }

            DocumentContentStore contentStore = new(paths);
            EfDocumentLibrary library = new(factory, contentStore, TimeProvider.System);
            return new TestLibraryFixture(root, paths, factory, library);
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            return ValueTask.CompletedTask;
        }
    }

    public sealed class TestDbContextFactory(DbContextOptions<CloudScribeDbContext> options)
        : IDbContextFactory<CloudScribeDbContext>
    {
        public CloudScribeDbContext CreateDbContext() => new(options);

        public Task<CloudScribeDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateDbContext());
        }
    }
}
