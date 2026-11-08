#pragma warning disable MA0004 // Synthetic tests have no synchronization context to preserve.
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using CloudScribe.Application.Documents;
using CloudScribe.Infrastructure.Configuration;
using CloudScribe.Infrastructure.Files;
using CloudScribe.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Options;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationCopyStoreTests
{
    private const string SourceText = "Read https://example.test/article and finish.\r\nKeep www.example.test links in the source.";
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static Task<NarrationCopyPreparation> PrepareAsync(EfNarrationCopyStore store, DocumentSnapshot source) =>
        store.PrepareAsync(source.Id, source.CurrentRevisionId!.Value, Hash(source.Text), Token);

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(AppPaths paths, Factory factory, CommitFault fault)
        {
            Paths = paths;
            Factory = factory;
            Fault = fault;
            Library = new(factory, new(paths), TimeProvider.System);
        }

        public AppPaths Paths { get; }
        public Factory Factory { get; }
        public CommitFault Fault { get; }
        public EfDocumentLibrary Library { get; }
        public EfNarrationCopyStore Store() => new(Factory, new(Paths), TimeProvider.System);
        public Task<DocumentSnapshot> SourceAsync(string text = SourceText) => Library.CreateAsync("Source", text, Token);
        public string[] Blobs() => Directory.GetFiles(Paths.DocumentsDirectory, "*.utf8", SearchOption.AllDirectories);

        public static async Task<Fixture> CreateAsync(string? migration = null)
        {
            string root = Path.Combine(Path.GetTempPath(), "cloudscribe-narration-copy-tests", Guid.NewGuid().ToString("N"));
            AppPaths paths = new(Options.Create(new CloudScribeOptions { AppDataDirectoryOverride = root }));
            paths.EnsureDirectories();
            CommitFault fault = new();
            var options = new DbContextOptionsBuilder<CloudScribeDbContext>().UseSqlite(new SqliteConnectionStringBuilder
            {
                DataSource = paths.DatabasePath,
                ForeignKeys = true,
                DefaultTimeout = 30,
                Pooling = false,
            }.ConnectionString).AddInterceptors(fault).Options;
            Factory factory = new(options);
            using var context = factory.CreateDbContext();
            await context.GetService<IMigrator>().MigrateAsync(migration, Token);
            return new(paths, factory, fault);
        }

        public ValueTask DisposeAsync()
        {
            Directory.Delete(Paths.RootDirectory, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Factory(DbContextOptions<CloudScribeDbContext> options) : IDbContextFactory<CloudScribeDbContext>
    {
        public CloudScribeDbContext CreateDbContext() => new(options);
        public Task<CloudScribeDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateDbContext());
        }
    }

    private sealed class CommitFault : DbTransactionInterceptor
    {
        public bool Armed { get; set; }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (Armed)
            {
                Armed = false;
                throw new IOException("Simulated lost commit acknowledgement.");
            }
            return Task.CompletedTask;
        }
    }
}
