using CloudScribe.Infrastructure.Generation;
using CloudScribe.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Tests;

public sealed class VoiceLabProjectAuthorizationStoreTests
{
    private static readonly DateTimeOffset CapturedAt = new(2026, 9, 2, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task VerifiedProjectAuthorizationRoundTripsWithExactBindings()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "cloudscribe-voice-lab-project-auth-tests",
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

            VoiceLabProjectAuthorizationStore store = new(factory);
            VoiceLabProjectAuthorizationEvidence expected = CreateEvidence(
                projectId: "project-1",
                accountRevision: 7,
                privateVoiceAccessAuthorized: true);

            Assert.Null(await store.LoadCurrentAsync(
                "google",
                "primary",
                "project-1",
                TestContext.Current.CancellationToken).ConfigureAwait(true));

            await store.SaveVerifiedAsync(
                expected,
                TestContext.Current.CancellationToken).ConfigureAwait(true);

            VoiceLabProjectAuthorizationEvidence actual = Assert.IsType<VoiceLabProjectAuthorizationEvidence>(
                await store.LoadCurrentAsync(
                    "google",
                    "primary",
                    "project-1",
                    TestContext.Current.CancellationToken).ConfigureAwait(true));

            Assert.Equal(expected, actual);
            Assert.Null(await store.LoadCurrentAsync(
                "google",
                "primary",
                "different-project",
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

    [Fact]
    public async Task SaveVerifiedAsyncRejectsUnverifiedProjectAuthorization()
    {
        TestContextFactory factory = new(Path.Combine(
            Path.GetTempPath(),
            $"cloudscribe-voice-lab-project-auth-reject-{Guid.NewGuid():N}.db"));
        VoiceLabProjectAuthorizationStore store = new(factory);
        VoiceLabProjectAuthorizationEvidence evidence = CreateEvidence(
            projectId: "project-1",
            accountRevision: 7,
            privateVoiceAccessAuthorized: false) with
        {
            ProjectAuthorized = false,
        };

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.SaveVerifiedAsync(
                evidence,
                TestContext.Current.CancellationToken)).ConfigureAwait(true);

        Assert.Contains("positively verified", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static VoiceLabProjectAuthorizationEvidence CreateEvidence(
        string projectId,
        long accountRevision,
        bool privateVoiceAccessAuthorized) => new(
            ProviderId: "google",
            AccountId: "primary",
            ProjectId: projectId,
            AccountRevision: accountRevision,
            CredentialReferenceId: "credential.current",
            CapabilityEvidenceId: Guid.NewGuid().ToString("D"),
            ProjectAuthorized: true,
            PrivateVoiceAccessAuthorized: privateVoiceAccessAuthorized,
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
