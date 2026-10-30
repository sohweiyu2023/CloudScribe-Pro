using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudScribe.Application.Providers;
using CloudScribe.Application.Security;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Infrastructure.Persistence;
using CloudScribe.Infrastructure.Providers;
using CloudScribe.Providers.Abstractions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CloudScribe.Architecture.Tests;

public sealed partial class ShellViewModelStateTests
{
    private sealed class RepairFixture : IAsyncDisposable
    {
        internal const string Account = "synthetic-google";
        internal const string Project = "synthetic-project";
        internal const string Email = "synthetic-tts@synthetic-project.iam.gserviceaccount.com";
        internal const string OldReference = "synthetic-old-missing-key";
        internal static readonly Uri Synthesis = new("https://texttospeech.googleapis.com/v1/text:synthesize");
        internal AutosaveDiskFixture Disk { get; }
        internal AutosaveContextFactory Factory { get; }
        internal RepairClock Clock { get; } = new();
        internal RepairVault Vault { get; } = new();
        internal RSA Rsa { get; } = RSA.Create(2048);
        internal RepairHttp Handler { get; }
        internal GoogleCredentialRepairVerifier Verifier { get; }
        internal string Json { get; }
        private readonly List<GoogleCredentialRepairService> _services = [];

        private RepairFixture(AutosaveDiskFixture disk, ILoggerFactory? loggerFactory)
        {
            Disk = disk;
            Factory = new(new DbContextOptionsBuilder<CloudScribeDbContext>().UseSqlite(
                new SqliteConnectionStringBuilder { DataSource = disk.Paths.DatabasePath, ForeignKeys = true }.ConnectionString).Options);
            Handler = new(Rsa);
            Verifier = new(new HttpClient(Handler), Clock, loggerFactory);
            Json = JsonSerializer.Serialize(new
            {
                type = "service_account",
                project_id = Project,
                client_email = Email,
                private_key_id = new string('a', 40),
                private_key = Rsa.ExportPkcs8PrivateKeyPem(),
                token_uri = "https://oauth2.googleapis.com/token"
            });
        }

        internal static async Task<RepairFixture> CreateAsync(ILoggerFactory? loggerFactory = null)
        {
            var fixture = new RepairFixture(await AutosaveDiskFixture.CreateAsync().ConfigureAwait(false), loggerFactory);
            var accounts = new EfProviderAccountStore(fixture.Factory, fixture.Clock);
            var reference = new ProviderAccountReference(GoogleGenerationProvider.StableProviderId, Account, "Synthetic Google",
                new CredentialReference(OldReference), GoogleTextToSpeechEndpointIdentity.Create(Synthesis), "global", new Uri("https://texttospeech.googleapis.com"));
            await accounts.CreateAsync(reference, true, TestContext.Current.CancellationToken).ConfigureAwait(false);
            await fixture.SqlAsync("""
                INSERT INTO google_generation_project_authorizations VALUES(
                    'synthetic-google','synthetic-project','cmn-CN-Chirp3-HD-Alnilam','synthetic-old-missing-key',
                    'synthetic-old-capability','synthetic-endpoint','global','https://texttospeech.googleapis.com',1,1,2);
                INSERT INTO google_generation_queue_states(AccountId,OperationStableId,IdempotencyKey,UnresolvedSubmission,ProviderRequestId,SubmissionAttempted)
                    VALUES('synthetic-google','synthesize-speech','synthetic-retained-failed',0,NULL,1);
                INSERT INTO google_generation_spend_authorizations VALUES(
                    '6d3de9c2-8952-4103-bf58-408e327b095f','synthetic-google','synthetic-old-missing-key','old-capability','old-pricing',
                    1,'cmn-CN-Chirp3-HD-Alnilam','MP3','synthetic-old-payload',1,'USD',2,3,3,1);
                """).ConfigureAwait(false);
            return fixture;
        }

        internal GoogleCredentialRepairService NewService()
        {
            var service = new GoogleCredentialRepairService(new(Factory, Clock), Vault, Vault, Verifier, Disk.Paths, Clock);
            _services.Add(service);
            return service;
        }

        internal async Task<object?> SqlAsync(string sql)
        {
            using var context = Factory.CreateDbContext();
            var connection = (SqliteConnection)context.Database.GetDbConnection();
            await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
            using var command = connection.CreateCommand(); command.CommandText = sql;
            return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        }

        internal async Task<ProviderAccountSnapshot> CurrentAsync() =>
            (await new EfProviderAccountStore(Factory, Clock).FindAsync(GoogleGenerationProvider.StableProviderId, Account,
                TestContext.Current.CancellationToken).ConfigureAwait(false))!;

        internal async Task<string> RetainedAsync()
        {
            using var context = Factory.CreateDbContext();
            var connection = (SqliteConnection)context.Database.GetDbConnection();
            await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
            List<object?[]> rows = [];
            foreach (string table in new[] { "documents", "document_revisions", "google_generation_queue_states",
                "google_generation_spend_authorizations", "google_recovery_attempts", "google_recovery_scopes",
                "google_recovery_grant_transitions", "chirp_article_attempts", "chirp_article_receipts", "chirp_article_legacy_associations" })
            {
                using var command = connection.CreateCommand(); command.CommandText = "SELECT * FROM " + table + " ORDER BY rowid;";
                using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(false))
                {
                    object[] values = new object[reader.FieldCount]; reader.GetValues(values);
                    rows.Add(values.Select(x => x is DBNull ? null : x).ToArray());
                }
            }
            return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(rows)));
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var service in _services) service.Dispose();
            Verifier.Dispose(); Vault.Dispose(); Rsa.Dispose();
            await Disk.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class RepairClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = new(2026, 10, 29, 14, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class RepairVault : ICredentialVault, ICredentialPresenceReader, IDisposable
    {
        internal ConcurrentDictionary<string, char[]> Items { get; } = new(StringComparer.Ordinal);
        internal int Writes { get; private set; }
        internal int Attempts { get; private set; }
        internal int FailAttempt { get; set; }
        internal bool FailAfterWrite { get; set; }
        internal Func<int, Task>? BeforeStore { get; set; }
        internal CredentialRolePresence? ForcedPresence { get; set; }
        internal Action<string>? OnInspect { get; set; }

        public ValueTask<CredentialRolePresence> InspectAsync(CredentialReference reference, CancellationToken cancellationToken = default)
        {
            OnInspect?.Invoke(reference.TargetName);
            return ValueTask.FromResult(ForcedPresence ?? (Items.ContainsKey(reference.TargetName) ? CredentialRolePresence.Present : CredentialRolePresence.Absent));
        }

        public async ValueTask StoreAsync(CredentialReference reference, ReadOnlyMemory<char> secret, CancellationToken cancellationToken = default)
        {
            int attempt = ++Attempts;
            if (BeforeStore is { } before) await before(attempt).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (attempt == FailAttempt && !FailAfterWrite) throw new IOException("Synthetic unavailable write.");
            if (!Items.TryAdd(reference.TargetName, secret.ToArray())) throw new InvalidOperationException("Synthetic collision: no overwrite.");
            Writes++;
            if (attempt == FailAttempt && FailAfterWrite) throw new IOException("Synthetic lost acknowledgement.");
        }

        public ValueTask<CredentialSecret?> ReadAsync(CredentialReference reference, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Items.TryGetValue(reference.TargetName, out var value) ? new CredentialSecret(value.ToArray()) : null);

        public ValueTask<bool> DeleteAsync(CredentialReference reference, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Repair is not allowed to delete credentials.");

        public void Dispose() { foreach (var value in Items.Values) Array.Clear(value); Items.Clear(); }
    }

    private sealed class RepairHttp(RSA rsa) : HttpMessageHandler
    {
        internal int Requests { get; private set; }
        internal bool RejectProbe { get; set; }
        internal TaskCompletionSource? WaitBeforeResponse { get; set; }
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++; Entered.TrySetResult();
            if (WaitBeforeResponse is { } wait) await wait.Task.WaitAsync(token).ConfigureAwait(false);
            if (request.RequestUri == new Uri("https://oauth2.googleapis.com/token"))
            {
                var fields = (await request.Content!.ReadAsStringAsync(token).ConfigureAwait(false)).Split('&').Select(x => x.Split('=', 2))
                    .ToDictionary(x => x[0], x => Uri.UnescapeDataString(x[1]), StringComparer.Ordinal);
                string[] jwt = fields["assertion"].Split('.');
                Assert.True(rsa.VerifyData(Encoding.ASCII.GetBytes(jwt[0] + "." + jwt[1]), Decode(jwt[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
                using var payload = JsonDocument.Parse(Decode(jwt[1]));
                Assert.Equal(RepairFixture.Email, payload.RootElement.GetProperty("iss").GetString());
                return Reply(HttpStatusCode.OK, """{"access_token":"SYNTHETIC-PRIVATE-TOKEN","expires_in":3600,"token_type":"Bearer"}""");
            }
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            if (request.Method == HttpMethod.Get && request.RequestUri == new Uri("https://texttospeech.googleapis.com/v1/voices"))
                return Reply(HttpStatusCode.OK, """{"voices":[{"name":"cmn-CN-Chirp3-HD-Alnilam","languageCodes":["cmn-CN"],"ssmlGender":"MALE","naturalSampleRateHertz":24000}]}""");
            Assert.Equal(RepairFixture.Synthesis, request.RequestUri);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("{}", await request.Content!.ReadAsStringAsync(token).ConfigureAwait(false));
            Assert.Equal(RepairFixture.Project, Assert.Single(request.Headers.GetValues("x-goog-user-project")));
            return RejectProbe ? Reply(HttpStatusCode.Forbidden, """{"error":{"status":"PERMISSION_DENIED"}}""")
                : Reply(HttpStatusCode.BadRequest, """{"error":{"code":400,"status":"INVALID_ARGUMENT","message":"Input required."}}""");
        }

        private static HttpResponseMessage Reply(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        private static byte[] Decode(string value) => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/').PadRight((value.Length + 3) / 4 * 4, '='));
    }
}
