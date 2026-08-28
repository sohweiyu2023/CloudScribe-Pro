using CloudScribe.Application.Providers;
using CloudScribe.Infrastructure.Persistence;
using CloudScribe.Infrastructure.Providers;
using CloudScribe.Providers.Abstractions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Tests;

public sealed class ProviderCapabilitySnapshotStoreTests
{
    [Fact]
    public async Task CapabilityEvidenceRequiresRegisteredAccountAndRemainsAppendOnly()
    {
        await using Fixture fixture = await Fixture.CreateAsync().ConfigureAwait(true);
        ProviderAccountReference account = new("fake", "primary", "Primary", null, "default", "global");
        ProviderCapabilitySnapshot snapshot = CreateSnapshot(account, new DateTimeOffset(2026, 8, 17, 6, 0, 0, TimeSpan.Zero), "fake:test-1");

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Capabilities.SaveAsync(
            snapshot,
            new DateTimeOffset(2026, 8, 17, 7, 0, 0, TimeSpan.Zero),
            TestContext.Current.CancellationToken)).ConfigureAwait(true);

        await fixture.Accounts.CreateAsync(account, isEnabled: true, cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(true);
        StoredProviderCapabilitySnapshot first = await fixture.Capabilities.SaveAsync(
            snapshot,
            new DateTimeOffset(2026, 8, 17, 7, 0, 0, TimeSpan.Zero),
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        StoredProviderCapabilitySnapshot second = await fixture.Capabilities.SaveAsync(
            CreateSnapshot(account, new DateTimeOffset(2026, 8, 17, 6, 30, 0, TimeSpan.Zero), "fake:test-2"),
            new DateTimeOffset(2026, 8, 17, 8, 0, 0, TimeSpan.Zero),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.NotEqual(first.Id, second.Id);
        StoredProviderCapabilitySnapshot? latest = await fixture.Capabilities
            .GetLatestAsync("fake", "primary", TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Equal(second.Id, latest?.Id);
        Assert.False(second.IsStale(new DateTimeOffset(2026, 8, 17, 7, 59, 59, TimeSpan.Zero)));
        Assert.True(second.IsStale(new DateTimeOffset(2026, 8, 17, 8, 0, 0, TimeSpan.Zero)));
        Assert.Equal(2, (await fixture.Capabilities
            .ListRecentAsync("fake", "primary", cancellationToken: TestContext.Current.CancellationToken)
            .ConfigureAwait(true)).Count);
    }

    [Fact]
    public async Task HistoricalCapabilityEvidencePreservesAccountMetadataAtCaptureTime()
    {
        await using Fixture fixture = await Fixture.CreateAsync().ConfigureAwait(true);
        CredentialReference oldCredential = new("fake.primary.old-key");
        ProviderAccountReference original = new("fake", "primary", "Original", oldCredential, "default", "global");
        ProviderAccountSnapshot account = await fixture.Accounts
            .CreateAsync(original, isEnabled: true, cancellationToken: TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        StoredProviderCapabilitySnapshot saved = await fixture.Capabilities.SaveAsync(
            CreateSnapshot(original, new DateTimeOffset(2026, 8, 17, 6, 0, 0, TimeSpan.Zero), "fake:captured"),
            new DateTimeOffset(2026, 8, 17, 7, 0, 0, TimeSpan.Zero),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        ProviderAccountReference renamed = new("fake", "primary", "Renamed", new CredentialReference("fake.primary.new-key"), "secondary", "eu-west1");
        await fixture.Accounts.UpdateAsync(
            renamed, isEnabled: false, expectedRevision: account.Revision, cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(true);

        StoredProviderCapabilitySnapshot? historical = await fixture.Capabilities
            .GetLatestAsync("fake", "primary", TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Equal(saved.Id, historical?.Id);
        Assert.Equal("Original", historical?.Snapshot.Account.DisplayName);
        Assert.Equal(oldCredential.TargetName, historical?.Snapshot.Account.CredentialReference?.TargetName);
        Assert.Equal("default", historical?.Snapshot.Account.EndpointId);
        Assert.Equal("global", historical?.Snapshot.Account.RegionId);
    }

    [Fact]
    public async Task CapabilityHistoryRejectsInvalidExpiryAndBoundsQueries()
    {
        await using Fixture fixture = await Fixture.CreateAsync().ConfigureAwait(true);
        ProviderAccountReference account = new("fake", "primary", "Primary", null);
        await fixture.Accounts.CreateAsync(account, isEnabled: true, cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(true);
        ProviderCapabilitySnapshot snapshot = CreateSnapshot(account, new DateTimeOffset(2026, 8, 17, 6, 0, 0, TimeSpan.Zero), "fake:bounded");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.Capabilities.SaveAsync(
            snapshot,
            new DateTimeOffset(2026, 8, 17, 5, 59, 0, TimeSpan.Zero),
            TestContext.Current.CancellationToken)).ConfigureAwait(true);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.Capabilities.ListRecentAsync(
            "fake", "primary", maximumCount: 101, cancellationToken: TestContext.Current.CancellationToken)).ConfigureAwait(true);
    }

    [Fact]
    public async Task MigrationCreatesAccountAndCapabilityTablesWithForeignKeys()
    {
        await using Fixture fixture = await Fixture.CreateMigratedAsync().ConfigureAwait(true);
        CloudScribeDbContext context = fixture.Factory.CreateDbContext();
        await using (context.ConfigureAwait(true))
        {
            SqliteConnection connection = (SqliteConnection)context.Database.GetDbConnection();
            await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            List<string> tables = [];
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;";
            using SqliteDataReader reader = await command
            .ExecuteReaderAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
            while (await reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true))
            {
                tables.Add(reader.GetString(0));
            }

            Assert.Contains("provider_accounts", tables);
            Assert.Contains("provider_capability_snapshots", tables);
            Assert.Contains("provider_capability_entries", tables);
        }
    }

    private static ProviderCapabilitySnapshot CreateSnapshot(
        ProviderAccountReference account,
        DateTimeOffset capturedAtUtc,
        string provenanceId) => new(
        account,
        capturedAtUtc,
        provenanceId,
        [
            new ProviderCapability("synthesize-speech", ProviderCapabilityState.Supported, ProviderLifecycleState.Available),
            new ProviderCapability("multi-speaker", ProviderCapabilityState.Unknown, ProviderLifecycleState.Unknown, "Not observed in this snapshot."),
        ]);

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(string root, TestContextFactory factory)
        {
            Root = root;
            Factory = factory;
            Accounts = new EfProviderAccountStore(factory, TimeProvider.System);
            Capabilities = new EfProviderCapabilitySnapshotStore(factory);
        }

        public string Root { get; }
        public TestContextFactory Factory { get; }
        public EfProviderAccountStore Accounts { get; }
        public EfProviderCapabilitySnapshotStore Capabilities { get; }

        public static async Task<Fixture> CreateAsync()
        {
            Fixture fixture = CreateCore();
            CloudScribeDbContext context = fixture.Factory.CreateDbContext();
            await using (context.ConfigureAwait(false))
            {
                await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
            }
            return fixture;
        }

        public static async Task<Fixture> CreateMigratedAsync()
        {
            Fixture fixture = CreateCore();
            CloudScribeDbContext context = fixture.Factory.CreateDbContext();
            await using (context.ConfigureAwait(false))
            {
                await context.Database.MigrateAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
            }
            return fixture;
        }

        private static Fixture CreateCore()
        {
            string root = Path.Combine(Path.GetTempPath(), "cloudscribe-capability-history-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            DbContextOptions<CloudScribeDbContext> options = new DbContextOptionsBuilder<CloudScribeDbContext>()
                .UseSqlite(new SqliteConnectionStringBuilder
                {
                    DataSource = Path.Combine(root, "capabilities.db"),
                    ForeignKeys = true,
                    DefaultTimeout = 5,
                }.ConnectionString)
                .Options;
            return new Fixture(root, new TestContextFactory(options));
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

    public sealed class TestContextFactory(DbContextOptions<CloudScribeDbContext> options) : IDbContextFactory<CloudScribeDbContext>
    {
        public CloudScribeDbContext CreateDbContext() => new(options);

        public Task<CloudScribeDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateDbContext());
        }
    }
}
