using CloudScribe.Application.Pricing;
using CloudScribe.Infrastructure.Persistence;
using CloudScribe.Infrastructure.Pricing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Tests;

public sealed class PricingContractOverrideStoreTests
{
    [Fact]
    public async Task StructurallyValidOverrideIsStoredInactiveAndDeduplicatedByExactHash()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        byte[] bytes = "{\"provider\":\"example\",\"note\":\"contractual rate\"}"u8.ToArray();
        CloudScribe.Application.Pricing.PricingContractOverrideSnapshot first = await fixture.Store.SaveInactiveAsync(
            bytes, "Private contract", "user:pricing-contract-1", TestContext.Current.CancellationToken);
        CloudScribe.Application.Pricing.PricingContractOverrideSnapshot second = await fixture.Store.SaveInactiveAsync(
            bytes, "Ignored duplicate label", "user:duplicate", TestContext.Current.CancellationToken);

        Assert.Equal(first.Id, second.Id);
        Assert.False(PricingContractOverrideSnapshot.AffectsPricing);
        Assert.Equal(64, first.Sha256.Length);
        Assert.Single(await fixture.Store.ListInactiveAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task HostileJsonIsRejectedBeforeOverridePersistence()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        byte[] duplicate = "{\"rate\":1,\"rate\":2}"u8.ToArray();
        await Assert.ThrowsAsync<PricingCatalogFormatException>(() => fixture.Store.SaveInactiveAsync(
            duplicate, "Duplicate", "test:duplicate", TestContext.Current.CancellationToken));
        Assert.Empty(await fixture.Store.ListInactiveAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OverrideMetadataRejectsControlCharacters()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Store.SaveInactiveAsync(
            "{\"x\":1}"u8.ToArray(), "bad\nlabel", "test:control", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OverrideTableIsSeparateFromCatalogTruthTables()
    {
        await using Fixture fixture = await Fixture.CreateMigratedAsync();
        await using CloudScribeDbContext context = fixture.Factory.CreateDbContext();
        SqliteConnection connection = (SqliteConnection)context.Database.GetDbConnection();
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        List<string> tables = [];
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            tables.Add(reader.GetString(0));
        }

        Assert.Contains("pricing_catalog_snapshots", tables);
        Assert.Contains("pricing_catalog_activations", tables);
        Assert.Contains("pricing_contract_overrides", tables);
    }

    [Fact]
    public async Task ListingIsNewestFirstAndDoesNotExposeAnActivationApi()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.Store.SaveInactiveAsync("{\"x\":1}"u8.ToArray(), "First", "test:first", TestContext.Current.CancellationToken);
        await fixture.Store.SaveInactiveAsync("{\"x\":2}"u8.ToArray(), "Second", "test:second", TestContext.Current.CancellationToken);
        IReadOnlyList<CloudScribe.Application.Pricing.PricingContractOverrideSnapshot> items = await fixture.Store.ListInactiveAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, items.Count);
        Assert.All(items, _ => Assert.False(PricingContractOverrideSnapshot.AffectsPricing));
        Assert.DoesNotContain(typeof(CloudScribe.Application.Pricing.IPricingContractOverrideStore).GetMethods(),
            method => method.Name.Contains("Activate", StringComparison.Ordinal));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(string root, TestContextFactory factory)
        {
            Root = root;
            Factory = factory;
            Store = new EfPricingContractOverrideStore(factory, new StrictJsonObjectReader(), TimeProvider.System);
        }

        public string Root { get; }
        public TestContextFactory Factory { get; }
        public EfPricingContractOverrideStore Store { get; }

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
            string root = Path.Combine(Path.GetTempPath(), "cloudscribe-pricing-override-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            DbContextOptions<CloudScribeDbContext> options = new DbContextOptionsBuilder<CloudScribeDbContext>()
                .UseSqlite(new SqliteConnectionStringBuilder
                {
                    DataSource = Path.Combine(root, "override.db"),
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
