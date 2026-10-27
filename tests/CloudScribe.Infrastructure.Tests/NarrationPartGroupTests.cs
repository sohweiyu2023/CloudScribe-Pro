using System.Security.Cryptography;
using System.Text;
using CloudScribe.Application.Documents;
using CloudScribe.Domain.Documents;
using CloudScribe.Infrastructure.Configuration;
using CloudScribe.Infrastructure.Files;
using CloudScribe.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CloudScribe.Infrastructure.Tests;

public sealed class NarrationPartGroupTests
{
    [Fact]
    public async Task TwoOriginalsAndFortySixPartsShowSixArticlesWithoutChangingDocuments()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        List<DocumentSnapshot> originals = [];
        for (int index = 0; index < 4; index++)
            originals.Add(await fixture.Library.CreateAsync(index == 0 ? "Seven - Part 001" : $"Unrelated {index}", "Unrelated synthetic text", TestContext.Current.CancellationToken));
        var seven = await fixture.CreateBatchAsync("Seven", 20);
        var anatta = await fixture.CreateBatchAsync("Anatta", 26);
        var all = originals.Concat(seven.Children).Concat(anatta.Children).Append(seven.Parent).Append(anatta.Parent).ToArray();
        await fixture.Groups.RegisterAsync(seven.Preparation.GroupId, seven.Members, TestContext.Current.CancellationToken);
        await fixture.Groups.RegisterAsync(anatta.Preparation.GroupId, anatta.Members, TestContext.Current.CancellationToken);
        var articles = await fixture.Groups.QueryLibraryAsync(string.Empty, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(6, articles.Count);
        Assert.Equal(46, articles.Sum(article => article.Parts.Count));
        Assert.Contains(articles, article => article.Document.Id == originals[0].Id && article.Parts.Count == 0);
        Assert.Equal(20, articles.Single(article => article.Document.Id == seven.Parent.Id).Parts.Count);
        Assert.Equal(26, articles.Single(article => article.Document.Id == anatta.Parent.Id).Parts.Count);
        foreach (var before in all)
            Assert.Equal(before, await fixture.Library.OpenAsync(before.Id, TestContext.Current.CancellationToken));
        var repeated = await fixture.Groups.BeginAsync(seven.Parent.Id, seven.Parent.CurrentRevisionId!.Value,
            Hash(seven.Parent.Text), seven.Members.Select(member => member.Sha256).ToArray(), TestContext.Current.CancellationToken);
        Assert.True(repeated.AlreadyComplete);
        Assert.Equal(seven.Preparation.GroupId, repeated.GroupId);
        await fixture.Groups.RegisterAsync(seven.Preparation.GroupId, seven.Members, TestContext.Current.CancellationToken);
        var match = Assert.Single(await fixture.Groups.QueryLibraryAsync("Seven-marker-19", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(seven.Parent.Id, match.Document.Id);
        Assert.Contains(match.Parts, part => part.Document.Id == seven.Children[19].Id);
        using var context = fixture.CreateDbContext();
        Assert.Equal(52, await context.Documents.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(52, await context.DocumentRevisions.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await context.BillableLedger.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IncompleteOrMismatchedPreparationStaysVisibleAndCannotBlindlyRepeat()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        var batch = await fixture.CreateBatchAsync("Incomplete", 2);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Groups.BeginAsync(batch.Parent.Id,
            batch.Parent.CurrentRevisionId!.Value, Hash(batch.Parent.Text), batch.Members.Select(member => member.Sha256).ToArray(), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Groups.RegisterAsync(batch.Preparation.GroupId,
            batch.Members.Reverse().ToArray(), TestContext.Current.CancellationToken));
        var visible = await fixture.Groups.QueryLibraryAsync(string.Empty, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(3, visible.Count);
        Assert.All(visible, article => Assert.Empty(article.Parts));
        await fixture.Groups.RegisterAsync(batch.Preparation.GroupId, batch.Members, TestContext.Current.CancellationToken);
        var child = batch.Children[0];
        _ = await fixture.Library.SaveAsync(new DocumentSaveRequest(child.Id, child.Title, child.Text + "edited",
            child.ConcurrencyVersion, DocumentRevisionKind.Checkpoint), TestContext.Current.CancellationToken);
        var grouped = Assert.Single(await fixture.Groups.QueryLibraryAsync(string.Empty, cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(grouped.ChangedSincePreparation);
        Assert.True(grouped.Parts[0].ChangedSincePreparation);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Groups.BeginAsync(batch.Parent.Id,
            batch.Parent.CurrentRevisionId!.Value, Hash(batch.Parent.Text), batch.Members.Select(member => member.Sha256).ToArray(), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Groups.BeginAsync(batch.Parent.Id,
            batch.Parent.CurrentRevisionId!.Value, Hash(batch.Parent.Text), new[] { Hash(batch.Parent.Text) }, TestContext.Current.CancellationToken));
        var changedChild = (await fixture.Library.OpenAsync(child.Id, TestContext.Current.CancellationToken))!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Groups.BeginAsync(child.Id,
            changedChild.CurrentRevisionId!.Value,
            Hash(child.Text + "edited"), new[] { Hash(child.Text + "edited") }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ArticleLimitIsAppliedAfterGroupingMoreThanTwoHundredChildren()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        for (int index = 0; index < 3; index++)
        {
            var batch = await fixture.CreateBatchAsync($"Many-{index}", 100);
            await fixture.Groups.RegisterAsync(batch.Preparation.GroupId, batch.Members, TestContext.Current.CancellationToken);
        }
        var articles = await fixture.Groups.QueryLibraryAsync(string.Empty, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(3, articles.Count);
        Assert.Equal(300, articles.Sum(article => article.Parts.Count));
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed record Batch(DocumentSnapshot Parent, DocumentSnapshot[] Children, NarrationPartPreparation Preparation, NarrationPartMember[] Members);

    private sealed class Fixture : IDbContextFactory<CloudScribeDbContext>, IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cloudscribe-group-tests", Guid.NewGuid().ToString("N"));
        private readonly AppPaths _paths;
        public Fixture()
        {
            _paths = new AppPaths(Options.Create(new CloudScribeOptions { AppDataDirectoryOverride = _root }));
            _paths.EnsureDirectories();
            Library = new EfDocumentLibrary(this, new DocumentContentStore(_paths), TimeProvider.System);
            Groups = new EfNarrationPartGroupStore(this, Library);
        }
        public EfDocumentLibrary Library { get; }
        public EfNarrationPartGroupStore Groups { get; }
        public CloudScribeDbContext CreateDbContext() => new(new DbContextOptionsBuilder<CloudScribeDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = _paths.DatabasePath, ForeignKeys = true, Pooling = false }.ConnectionString).Options);
        public async Task InitializeAsync()
        {
            using var context = CreateDbContext();
            await context.Database.MigrateAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        }
        public async Task<Batch> CreateBatchAsync(string title, int count)
        {
            string[] texts = Enumerable.Range(0, count).Select(index => $"{title}-marker-{index:D2} 中文 😀\n").ToArray();
            var parent = await Library.CreateAsync(title, string.Concat(texts), TestContext.Current.CancellationToken).ConfigureAwait(false);
            var preparation = await Groups.BeginAsync(parent.Id, parent.CurrentRevisionId!.Value, Hash(parent.Text),
                texts.Select(Hash).ToArray(), TestContext.Current.CancellationToken).ConfigureAwait(false);
            List<DocumentSnapshot> children = [];
            foreach (string text in texts)
                children.Add(await Library.CreateAsync($"{title} - Part {children.Count + 1:D3}", text, TestContext.Current.CancellationToken).ConfigureAwait(false));
            return new(parent, children.ToArray(), preparation,
                children.Select(child => new NarrationPartMember(child.Id, child.CurrentRevisionId!.Value, Hash(child.Text))).ToArray());
        }
        public void Dispose()
        {
            string expected = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cloudscribe-group-tests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(_root).StartsWith(expected, StringComparison.OrdinalIgnoreCase)
                || Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories).Any(path => File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)))
                throw new InvalidOperationException("Unexpected synthetic cleanup root.");
            Directory.Delete(_root, recursive: true);
        }
    }
}
