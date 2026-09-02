using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Tests;

public sealed class VoiceLabAuditionAuthorizationStoreTests
{
    private static readonly DateTimeOffset CapturedAt = new(2026, 9, 2, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task VerifiedAuditionAuthorizationRoundTripsWithExactBindings()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "cloudscribe-voice-lab-audition-auth-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string databasePath = Path.Combine(root, "voice-lab.db");

        try
        {
            TestContextFactory factory = new(databasePath);
            await using (CloudScribeDbContext context = factory.CreateDbContext())
            {
                await context.Database.MigrateAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            }

            VoiceLabAuditionAuthorizationStore store = new(factory);
            VoiceLabCatalogSelection selection = CreateSelection(
                capabilityEvidenceId: Guid.NewGuid().ToString("D"),
                voiceFingerprint: "voice-fingerprint-1");
            VoiceLabAuditionPersistedAuthorization expected = CreateAuthorization(selection);

            Assert.Null(await store.LoadCurrentAsync(
                selection,
                TestContext.Current.CancellationToken).ConfigureAwait(true));

            await store.SaveVerifiedAsync(
                expected,
                TestContext.Current.CancellationToken).ConfigureAwait(true);

            VoiceLabAuditionPersistedAuthorization actual = Assert.IsType<VoiceLabAuditionPersistedAuthorization>(
                await store.LoadCurrentAsync(
                    selection,
                    TestContext.Current.CancellationToken).ConfigureAwait(true));

            Assert.Equal(expected, actual);
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

    [Fact]
    public async Task LoadCurrentAsyncRejectsVoiceFingerprintDrift()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "cloudscribe-voice-lab-audition-drift-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string databasePath = Path.Combine(root, "voice-lab.db");

        try
        {
            TestContextFactory factory = new(databasePath);
            await using (CloudScribeDbContext context = factory.CreateDbContext())
            {
                await context.Database.MigrateAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            }

            VoiceLabAuditionAuthorizationStore store = new(factory);
            string capabilityEvidenceId = Guid.NewGuid().ToString("D");
            VoiceLabCatalogSelection approvedSelection = CreateSelection(
                capabilityEvidenceId,
                voiceFingerprint: "voice-fingerprint-approved");
            await store.SaveVerifiedAsync(
                CreateAuthorization(approvedSelection),
                TestContext.Current.CancellationToken).ConfigureAwait(true);

            VoiceLabCatalogSelection currentSelection = CreateSelection(
                capabilityEvidenceId,
                voiceFingerprint: "voice-fingerprint-current");

            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.LoadCurrentAsync(
                    currentSelection,
                    TestContext.Current.CancellationToken)).ConfigureAwait(true);

            Assert.Contains("different current voice/capability evidence", error.Message, StringComparison.OrdinalIgnoreCase);
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

    private static VoiceLabCatalogSelection CreateSelection(
        string capabilityEvidenceId,
        string voiceFingerprint) => new(
            VoiceStableId: "voice-1",
            ProviderStableId: "google",
            AccountStableId: "primary",
            ProjectStableId: "project-1",
            CapabilityEvidenceId: capabilityEvidenceId,
            VoiceFingerprint: voiceFingerprint,
            CapabilityCurrent: true,
            VoiceEnabled: true,
            AccountProjectAuthorized: true);

    private static VoiceLabAuditionPersistedAuthorization CreateAuthorization(
        VoiceLabCatalogSelection selection) => new(
            Selection: selection,
            CredentialReferenceId: "credential.current",
            PricingEvidenceId: "pricing.current",
            SpendAuthorizationId: "spend-approved-1",
            AccountRevision: 7,
            CapturedAtUtc: CapturedAt,
            ExpiresAtUtc: CapturedAt.AddHours(1));

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
