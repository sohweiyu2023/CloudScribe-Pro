using CloudScribe.Application.Providers;
using CloudScribe.Infrastructure.Persistence;
using CloudScribe.Infrastructure.Providers;
using CloudScribe.Providers.Abstractions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Tests;

public sealed class ProviderAccountStoreTests
{
    [Fact]
    public async Task AccountMetadataPersistsCredentialReferenceWithoutSecretAndRequiresRevisionBoundUpdates()
    {
        await using Fixture fixture = await Fixture.CreateAsync().ConfigureAwait(true);
        CredentialReference credential = new("google.primary.api-key");
        ProviderAccountReference account = new("google", "primary", "Primary account", credential, "default", "us-central1");

        ProviderAccountSnapshot created = await fixture.Store
            .CreateAsync(account, isEnabled: true, cancellationToken: TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Equal(1, created.Revision);
        Assert.True(created.IsEnabled);
        Assert.Equal(credential.TargetName, created.Reference.CredentialReference?.TargetName);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.CreateAsync(
            account, isEnabled: true, cancellationToken: TestContext.Current.CancellationToken)).ConfigureAwait(true);

        fixture.Time.SetUtcNow(new DateTimeOffset(2026, 8, 17, 7, 0, 0, TimeSpan.Zero));
        ProviderAccountReference renamed = new("google", "primary", "Renamed account", credential, "default", "us-central1");
        ProviderAccountSnapshot updated = await fixture.Store
            .UpdateAsync(renamed, isEnabled: false, expectedRevision: created.Revision, cancellationToken: TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Equal(2, updated.Revision);
        Assert.False(updated.IsEnabled);
        Assert.Equal("Renamed account", updated.Reference.DisplayName);
        Assert.True(updated.UpdatedAtUtc > updated.CreatedAtUtc);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.UpdateAsync(
            renamed, isEnabled: true, expectedRevision: created.Revision, cancellationToken: TestContext.Current.CancellationToken)).ConfigureAwait(true);
    }

    [Fact]
    public async Task RegistryHasNoDefaultSelectionOrSecretBearingApi()
    {
        await using Fixture fixture = await Fixture.CreateAsync().ConfigureAwait(true);
        await fixture.Store.CreateAsync(
            new ProviderAccountReference("fake", "secondary", "Secondary", null),
            isEnabled: false,
            cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(true);
        await fixture.Store.CreateAsync(
            new ProviderAccountReference("fake", "primary", "Primary", new CredentialReference("fake.primary.key")),
            isEnabled: true,
            cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(true);

        IReadOnlyList<ProviderAccountSnapshot> accounts = await fixture.Store
            .ListAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Equal(["primary", "secondary"], accounts.Select(item => item.Reference.AccountId).ToArray());
        Assert.DoesNotContain(typeof(IProviderAccountStore).GetMethods(), method =>
            method.Name.Contains("Default", StringComparison.OrdinalIgnoreCase)
            || method.Name.Contains("Select", StringComparison.OrdinalIgnoreCase)
            || method.Name.Contains("Secret", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ProviderAccountTableStoresOnlyCredentialTargetReference()
    {
        await using Fixture fixture = await Fixture.CreateMigratedAsync().ConfigureAwait(true);
        CloudScribeDbContext context = fixture.Factory.CreateDbContext();
        await using (context.ConfigureAwait(true))
        {
            SqliteConnection connection = (SqliteConnection)context.Database.GetDbConnection();
            await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA table_info(provider_accounts);";
            using SqliteDataReader reader = await command
            .ExecuteReaderAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
            List<string> columns = [];
            while (await reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true))
            {
                columns.Add(reader.GetString(1));
            }

            Assert.Contains("CredentialTargetName", columns, StringComparer.Ordinal);
            Assert.DoesNotContain(columns, name =>
                name.Contains("Secret", StringComparison.OrdinalIgnoreCase)
                || name.Contains("ApiKey", StringComparison.OrdinalIgnoreCase)
                || name.Contains("AccessToken", StringComparison.OrdinalIgnoreCase));
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(string root, TestContextFactory factory, MutableTimeProvider time)
        {
            Root = root;
            Factory = factory;
            Time = time;
            Store = new EfProviderAccountStore(factory, time);
        }

        public string Root { get; }
        public TestContextFactory Factory { get; }
        public MutableTimeProvider Time { get; }
        public EfProviderAccountStore Store { get; }

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
            string root = Path.Combine(Path.GetTempPath(), "cloudscribe-provider-account-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            DbContextOptions<CloudScribeDbContext> options = new DbContextOptionsBuilder<CloudScribeDbContext>()
                .UseSqlite(new SqliteConnectionStringBuilder
                {
                    DataSource = Path.Combine(root, "accounts.db"),
                    ForeignKeys = true,
                    DefaultTimeout = 5,
                }.ConnectionString)
                .Options;
            MutableTimeProvider time = new(new DateTimeOffset(2026, 8, 17, 6, 0, 0, TimeSpan.Zero));
            return new Fixture(root, new TestContextFactory(options), time);
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

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void SetUtcNow(DateTimeOffset value) => _utcNow = value;
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
