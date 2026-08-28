using System.Data;
using CloudScribe.Application.Pricing;
using CloudScribe.Domain.Pricing;
using CloudScribe.Infrastructure.Persistence;
using CloudScribe.Infrastructure.Pricing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Tests;

public sealed class PricingCatalogHistoryStoreTests
{
    [Fact]
    public async Task ValidUnsignedSnapshotPersistsWithoutSilentActivationAndDeduplicatesByHash()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        byte[] catalog = "{\"fixture\":1}"u8.ToArray();
        PricingCatalogSource source = new(PricingCatalogSourceKind.ImportedFile, "manual-import.json");

        PricingCatalogSnapshot first = await fixture.Store.SaveSnapshotAsync(
            catalog,
            PricingCatalogTrustState.ValidUnsigned,
            source,
            cancellationToken: TestContext.Current.CancellationToken);
        PricingCatalogSnapshot second = await fixture.Store.SaveSnapshotAsync(
            catalog,
            PricingCatalogTrustState.ValidUnsigned,
            source,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(64, first.Sha256.Length);
        Assert.True(first.RequiresManualApproval);
        Assert.Null(await fixture.Store.GetActiveSnapshotAsync(TestContext.Current.CancellationToken));
        Assert.Single(await fixture.Store.ListSnapshotsAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await fixture.Store.ListActivationsAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(PricingCatalogTrustState.ContractUnavailable)]
    [InlineData(PricingCatalogTrustState.ValidationFailed)]
    [InlineData(PricingCatalogTrustState.SignatureInvalid)]
    public async Task NonAdmissibleTrustStatesCannotEnterHistory(PricingCatalogTrustState trustState)
    {
        await using Fixture fixture = await Fixture.CreateAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.SaveSnapshotAsync(
            "{\"fixture\":true}"u8.ToArray(),
            trustState,
            new PricingCatalogSource(PricingCatalogSourceKind.ImportedFile, "blocked.json"),
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UnsignedActivationRequiresExplicitConfirmationManualApprovalAndExactHash()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        PricingCatalogSnapshot snapshot = await fixture.Store.SaveSnapshotAsync(
            "{\"fixture\":2}"u8.ToArray(),
            PricingCatalogTrustState.ValidUnsigned,
            new PricingCatalogSource(PricingCatalogSourceKind.ImportedFile, "unsigned.json"),
            cancellationToken: TestContext.Current.CancellationToken);

        PricingCatalogActivationRequest unconfirmed = Request(snapshot, 0, PricingCatalogApprovalKind.ManualUnsigned, userConfirmed: false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.ActivateAsync(unconfirmed, TestContext.Current.CancellationToken));

        PricingCatalogActivationRequest wrongApproval = Request(snapshot, 0, PricingCatalogApprovalKind.VerifiedSignature, userConfirmed: true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.ActivateAsync(wrongApproval, TestContext.Current.CancellationToken));

        PricingCatalogActivationRequest wrongHash = new(
            snapshot.Id,
            new string('0', 64),
            0,
            PricingCatalogActivationKind.Activate,
            PricingCatalogApprovalKind.ManualUnsigned,
            userConfirmed: true,
            "Approve unsigned fixture");
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.ActivateAsync(wrongHash, TestContext.Current.CancellationToken));

        PricingCatalogActivation activation = await fixture.Store.ActivateAsync(
            Request(snapshot, 0, PricingCatalogApprovalKind.ManualUnsigned, userConfirmed: true),
            TestContext.Current.CancellationToken);
        Assert.Equal(snapshot.Id, activation.SnapshotId);
        Assert.Equal(1, activation.Sequence);
        Assert.Equal(PricingCatalogActivationKind.Activate, activation.Kind);
        Assert.Equal(snapshot.Id, (await fixture.Store.GetActiveSnapshotAsync(TestContext.Current.CancellationToken))?.Id);
    }

    [Fact]
    public async Task StaleActivationSequenceFailsClosedInsteadOfOverwritingNewerChoice()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        PricingCatalogSnapshot first = await SaveUnsignedAsync(fixture, 3);
        PricingCatalogSnapshot second = await SaveUnsignedAsync(fixture, 4);

        PricingCatalogActivation initial = await fixture.Store.ActivateAsync(
            Request(first, 0, PricingCatalogApprovalKind.ManualUnsigned, userConfirmed: true),
            TestContext.Current.CancellationToken);

        PricingCatalogActivationRequest stale = Request(second, 0, PricingCatalogApprovalKind.ManualUnsigned, userConfirmed: true);
        await Assert.ThrowsAsync<DBConcurrencyException>(() => fixture.Store.ActivateAsync(stale, TestContext.Current.CancellationToken));

        PricingCatalogSnapshot? active = await fixture.Store.GetActiveSnapshotAsync(TestContext.Current.CancellationToken);
        Assert.Equal(initial.SnapshotId, active?.Id);
    }

    [Fact]
    public async Task RollbackTargetsOnlyPreviouslyActiveSnapshotAndAppendsAuditHistory()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        PricingCatalogSnapshot first = await SaveUnsignedAsync(fixture, 5);
        PricingCatalogSnapshot second = await SaveUnsignedAsync(fixture, 6);
        PricingCatalogSnapshot neverActive = await SaveUnsignedAsync(fixture, 7);

        PricingCatalogActivation firstActivation = await fixture.Store.ActivateAsync(
            Request(first, 0, PricingCatalogApprovalKind.ManualUnsigned, userConfirmed: true),
            TestContext.Current.CancellationToken);
        PricingCatalogActivation secondActivation = await fixture.Store.ActivateAsync(
            Request(second, firstActivation.Sequence, PricingCatalogApprovalKind.ManualUnsigned, userConfirmed: true),
            TestContext.Current.CancellationToken);

        PricingCatalogActivationRequest invalidRollback = Request(
            neverActive,
            secondActivation.Sequence,
            PricingCatalogApprovalKind.ManualUnsigned,
            userConfirmed: true,
            kind: PricingCatalogActivationKind.Rollback);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.ActivateAsync(invalidRollback, TestContext.Current.CancellationToken));

        PricingCatalogActivation rollback = await fixture.Store.ActivateAsync(
            Request(
                first,
                secondActivation.Sequence,
                PricingCatalogApprovalKind.ManualUnsigned,
                userConfirmed: true,
                kind: PricingCatalogActivationKind.Rollback),
            TestContext.Current.CancellationToken);

        Assert.Equal(PricingCatalogActivationKind.Rollback, rollback.Kind);
        Assert.Equal(second.Id, rollback.PreviousSnapshotId);
        Assert.Equal(first.Id, (await fixture.Store.GetActiveSnapshotAsync(TestContext.Current.CancellationToken))?.Id);
        Assert.Equal(3, (await fixture.Store.ListActivationsAsync(TestContext.Current.CancellationToken)).Count);
    }

    [Fact]
    public async Task VerifiedSignatureSnapshotRetainsExternalKeyProvenance()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        PricingCatalogSnapshot snapshot = await fixture.Store.SaveSnapshotAsync(
            "{\"fixture\":8}"u8.ToArray(),
            PricingCatalogTrustState.SignatureVerified,
            new PricingCatalogSource(PricingCatalogSourceKind.RemoteUpdate, "provider-catalog"),
            signatureKeyId: "trusted-external-key-1",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(snapshot.RequiresManualApproval);
        Assert.Equal("trusted-external-key-1", snapshot.SignatureKeyId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.ActivateAsync(
            Request(snapshot, 0, PricingCatalogApprovalKind.ManualUnsigned, userConfirmed: true),
            TestContext.Current.CancellationToken));

        PricingCatalogActivation activation = await fixture.Store.ActivateAsync(
            Request(snapshot, 0, PricingCatalogApprovalKind.VerifiedSignature, userConfirmed: true),
            TestContext.Current.CancellationToken);
        Assert.Equal(PricingCatalogApprovalKind.VerifiedSignature, activation.ApprovalKind);
    }

    [Fact]
    public async Task MigrationCreatesCatalogHistoryTablesAndForeignKeys()
    {
        await using Fixture fixture = await Fixture.CreateMigratedAsync();
        await using CloudScribeDbContext context = fixture.Factory.CreateDbContext();
        SqliteConnection connection = (SqliteConnection)context.Database.GetDbConnection();
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        List<string> tables = [];
        await using SqliteCommand tablesCommand = connection.CreateCommand();
        tablesCommand.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;";
        await using SqliteDataReader tableReader = await tablesCommand.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await tableReader.ReadAsync(TestContext.Current.CancellationToken))
        {
            tables.Add(tableReader.GetString(0));
        }

        Assert.Contains("pricing_catalog_snapshots", tables);
        Assert.Contains("pricing_catalog_activations", tables);
    }

    private static PricingCatalogActivationRequest Request(
        PricingCatalogSnapshot snapshot,
        long currentSequence,
        PricingCatalogApprovalKind approvalKind,
        bool userConfirmed,
        PricingCatalogActivationKind kind = PricingCatalogActivationKind.Activate) => new(
        snapshot.Id,
        snapshot.Sha256,
        currentSequence,
        kind,
        approvalKind,
        userConfirmed,
        kind == PricingCatalogActivationKind.Rollback ? "Rollback to prior admitted catalog" : "Activate admitted catalog");

    private static Task<PricingCatalogSnapshot> SaveUnsignedAsync(Fixture fixture, int marker) =>
        fixture.Store.SaveSnapshotAsync(
            System.Text.Encoding.UTF8.GetBytes($"{{\"fixture\":{marker}}}"),
            PricingCatalogTrustState.ValidUnsigned,
            new PricingCatalogSource(PricingCatalogSourceKind.ImportedFile, $"fixture-{marker}.json"),
            cancellationToken: TestContext.Current.CancellationToken);

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(string root, TestContextFactory factory)
        {
            Root = root;
            Factory = factory;
            Store = new EfPricingCatalogHistoryStore(factory, TimeProvider.System);
        }

        public string Root { get; }
        public TestContextFactory Factory { get; }
        public EfPricingCatalogHistoryStore Store { get; }

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
            string root = Path.Combine(Path.GetTempPath(), "cloudscribe-pricing-history-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            DbContextOptions<CloudScribeDbContext> options = new DbContextOptionsBuilder<CloudScribeDbContext>()
                .UseSqlite(new SqliteConnectionStringBuilder
                {
                    DataSource = Path.Combine(root, "catalog-history.db"),
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

    public sealed class TestContextFactory(DbContextOptions<CloudScribeDbContext> options)
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
