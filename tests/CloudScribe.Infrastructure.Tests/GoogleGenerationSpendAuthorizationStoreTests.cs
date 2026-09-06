using CloudScribe.Infrastructure.Generation;
using CloudScribe.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Tests;

public sealed class GoogleGenerationSpendAuthorizationStoreTests
{
    [Fact]
    public async Task ExactApprovedEnvelopeRoundTripsAndChangedEnvelopeFailsClosed()
    {
        string root = Path.Combine(Path.GetTempPath(), "cloudscribe-google-spend-approval-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string databasePath = Path.Combine(root, "approval.db");
        try
        {
            TestContextFactory factory = new(databasePath);
            await using (CloudScribeDbContext context = factory.CreateDbContext())
            {
                await context.Database.MigrateAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            }

            GoogleGenerationSpendAuthorizationStore store = new(factory, TimeProvider.System);
            GoogleGenerationSubmissionEnvelope approvedEnvelope = CreateEnvelope(requestRevision: 7, pricingProvenanceId: "price-v1", payloadHash: new string('a', 64));

            Assert.Null(await store.LoadApprovedAsync(approvedEnvelope, TestContext.Current.CancellationToken).ConfigureAwait(true));

            GoogleGenerationSpendAuthorization approved = GoogleGenerationSpendAuthorization.Create(
                approvedEnvelope,
                "USD",
                6,
                approvedEstimateMinorUnits: 1_250_000,
                authorizedMaximumMinorUnits: 1_500_000);
            await store.SaveApprovedAsync(approved, TestContext.Current.CancellationToken).ConfigureAwait(true);

            GoogleGenerationSpendAuthorization loaded = Assert.IsType<GoogleGenerationSpendAuthorization>(
                await store.LoadApprovedAsync(approvedEnvelope, TestContext.Current.CancellationToken).ConfigureAwait(true));
            Assert.Equal(approved, loaded);

            Assert.Null(await store.LoadApprovedAsync(
                CreateEnvelope(requestRevision: 8, pricingProvenanceId: "price-v1", payloadHash: new string('a', 64)),
                TestContext.Current.CancellationToken).ConfigureAwait(true));
            Assert.Null(await store.LoadApprovedAsync(
                CreateEnvelope(requestRevision: 7, pricingProvenanceId: "price-v2", payloadHash: new string('a', 64)),
                TestContext.Current.CancellationToken).ConfigureAwait(true));
            Assert.Null(await store.LoadApprovedAsync(
                CreateEnvelope(requestRevision: 7, pricingProvenanceId: "price-v1", payloadHash: new string('b', 64)),
                TestContext.Current.CancellationToken).ConfigureAwait(true));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static GoogleGenerationSubmissionEnvelope CreateEnvelope(
        int requestRevision,
        string pricingProvenanceId,
        string payloadHash) => new(
            AccountId: "account-1",
            CredentialReferenceId: "cred-1",
            CapabilityProvenanceId: "cap-v1",
            PricingProvenanceId: pricingProvenanceId,
            RequestRevision: requestRevision,
            VoiceName: "en-US-TestVoice",
            AudioEncoding: "MP3",
            CompiledPayloadSha256: payloadHash,
            CompiledPayloadBytes: 512);

    private sealed class TestContextFactory(string databasePath) : IDbContextFactory<CloudScribeDbContext>
    {
        public CloudScribeDbContext CreateDbContext()
        {
            DbContextOptions<CloudScribeDbContext> options = new DbContextOptionsBuilder<CloudScribeDbContext>()
                .UseSqlite(new SqliteConnectionStringBuilder
                {
                    DataSource = databasePath,
                    ForeignKeys = true,
                }.ConnectionString)
                .Options;
            return new CloudScribeDbContext(options);
        }
    }
}
