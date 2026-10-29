using System.Security.Cryptography;
using System.Text.Json;
using CloudScribe.App.Composition;
using CloudScribe.App.ViewModels;
using CloudScribe.Application.Documents;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Configuration;
using CloudScribe.Infrastructure.Files;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Infrastructure.Persistence;
using CloudScribe.Providers.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CloudScribe.Architecture.Tests;

public sealed partial class GoogleRecoveryProductionRoutingTests
{
    private sealed partial class Fixture : IDbContextFactory<CloudScribeDbContext>, IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cloudscribe-recovery-routing-tests", Guid.NewGuid().ToString("N"));
        private readonly AppPaths _paths;
        public Fixture()
        {
            _paths = new(Options.Create(new CloudScribeOptions { AppDataDirectoryOverride = _root })); _paths.EnsureDirectories();
            string app = Path.Combine(_root, "install", "app"); Directory.CreateDirectory(app);
            File.WriteAllText(Path.Combine(app, "CloudScribe.dll"), "synthetic-module"); File.WriteAllText(Path.Combine(app, "CloudScribe.Infrastructure.dll"), "synthetic-module");
            Admission = new(app + Path.DirectorySeparatorChar, _paths, TimeProvider.System);
        }
        public string RootKey { get; } = "synthetic-held-key";
        public InstalledRecoveryAdmissionStore Admission { get; }
        public FakeCompilation Compiler { get; } = new();
        public ShellViewModel.GoogleTtsStudioRequestSelection Selection { get; private set; } = null!;
        public CloudScribeDbContext CreateDbContext() => new(new DbContextOptionsBuilder<CloudScribeDbContext>().UseSqlite(
            $"Data Source={_paths.DatabasePath};Pooling=False;Default Timeout=3").Options);
        public GoogleExplicitRecoveryService CreateRecovery() => new(this, TimeProvider.System, new InstalledRecoveryBudgetEvidenceResolver(Admission, this), Admission);
        public void ChangeSyntheticReviewedPolicy(Func<RecoveryAdmissionPolicy, RecoveryAdmissionPolicy> change)
        {
            string install = Path.Combine(_root, "install");
            var policy = JsonSerializer.Deserialize<RecoveryAdmissionPolicy>(File.ReadAllBytes(Path.Combine(install, "recovery-admission-policy.json")))!;
            var changed = change(policy); byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(changed);
            File.WriteAllBytes(Path.Combine(install, "recovery-admission-policy.json"), bytes);
            File.WriteAllText(Path.Combine(install, "cloudscribe-install.json"), JsonSerializer.Serialize(new
            {
                Product = "CloudScribe Pro",
                AppSha256 = changed.AppSha256,
                RecoveryPolicySha256 = Convert.ToHexStringLower(SHA256.HashData(bytes))
            }));
        }
        public async Task InitializeAsync()
        {
            using var db = CreateDbContext(); await db.Database.MigrateAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
            var library = new EfDocumentLibrary(this, new DocumentContentStore(_paths), TimeProvider.System);
            var doc = await library.CreateAsync("Synthetic narration", "Synthetic recovery text", TestContext.Current.CancellationToken).ConfigureAwait(false);
            Selection = new(doc.Id, doc.CurrentRevisionId, doc.Text, GoogleGenerationProvider.StableProviderId, "synthetic-account", "synthetic-project",
                "synthetic-voice", "synthetic-capability", "synthetic-fingerprint", "en-US", "en-US", DateTimeOffset.UtcNow);
            var pending = await Compiler.CompileReadonlyAsync(Selection, TestContext.Current.CancellationToken).ConfigureAwait(false);
            var queues = new GoogleGenerationPersistedQueueStateStore(this);
            var initial = new GoogleGenerationPersistedQueueState(Selection.AccountStableId, GoogleGenerationProvider.SynthesizeOperationStableId, RootKey, false, null);
            await queues.SaveAsync(initial, TestContext.Current.CancellationToken).ConfigureAwait(false);
            var held = initial with
            {
                SubmissionAttempted = true,
                UnresolvedSubmission = true,
                LocalAttemptId = Guid.NewGuid().ToString("N"),
                DiagnosticCode = "google-dispatch-claimed",
                PayloadSha256 = pending.Envelope.CompiledPayloadSha256,
                PayloadBytes = pending.Envelope.CompiledPayloadBytes,
                OutcomeObservedAtUnixMilliseconds = 1
            };
            if (!await queues.TryClaimSubmissionAsync(held, TestContext.Current.CancellationToken).ConfigureAwait(false)) throw new InvalidOperationException("Synthetic claim failed.");
            await new GoogleGenerationSpendAuthorizationStore(this, TimeProvider.System).SaveApprovedAsync(
                GoogleGenerationSpendAuthorization.Create(pending.Envelope, "USD", 2, 3, 3), TestContext.Current.CancellationToken).ConfigureAwait(false);
            var prior = new GoogleRecoveryPredecessorEvidence("synthetic-logical", Selection.AccountStableId, initial.OperationStableId, RootKey,
                JsonHash(held), Selection.ProjectStableId, doc.Id, doc.CurrentRevisionId!.Value, ArticleAudioManifest.HashText(doc.Text),
                pending.Envelope.CompiledPayloadSha256, new string('b', 64), DateTimeOffset.UtcNow.AddHours(1), 1, 3);
            string moduleHash = Convert.ToHexStringLower(SHA256.HashData("synthetic-module"u8));
            var baseline = new RecoveryBudgetBaseline(250, 1000, 10, 80, new string('a', 64),
                await PinsAsync("google_generation_queue_states", 3).ConfigureAwait(false), await PinsAsync("google_generation_spend_authorizations", 1).ConfigureAwait(false), []);
            var policy = new RecoveryAdmissionPolicy(2, moduleHash, moduleHash, InstalledRecoveryAdmissionStore.ProfileHash(_root),
                Selection.AccountStableId, Selection.ProjectStableId, "USD", 2, DateTimeOffset.UtcNow.AddHours(1), baseline, [], [prior]);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(policy);
            File.WriteAllBytes(Path.Combine(_root, "install", "recovery-admission-policy.json"), bytes);
            File.WriteAllText(Path.Combine(_root, "install", "cloudscribe-install.json"), JsonSerializer.Serialize(new
            {
                Product = "CloudScribe Pro",
                AppSha256 = moduleHash,
                RecoveryPolicySha256 = Convert.ToHexStringLower(SHA256.HashData(bytes))
            }));
        }
        private async Task<RecoveryFinancialRowPin[]> PinsAsync(string table, int columns)
        {
            using var db = CreateDbContext(); var connection = db.Database.GetDbConnection(); await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
            using var command = connection.CreateCommand(); command.CommandText = "SELECT * FROM " + table + " ORDER BY rowid;";
            using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
            List<RecoveryFinancialRowPin> pins = [];
            while (await reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(false))
            {
                object[] values = new object[reader.FieldCount]; reader.GetValues(values); var row = values.Select(x => x is DBNull ? null : x).ToArray();
                pins.Add(new(JsonHash(row.Take(columns).ToArray()), JsonHash(row)));
            }
            return pins.ToArray();
        }
        public void Dispose()
        {
            string expected = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cloudscribe-recovery-routing-tests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(_root).StartsWith(expected, StringComparison.OrdinalIgnoreCase)
                || Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories).Any(x => File.GetAttributes(x).HasFlag(FileAttributes.ReparsePoint)))
                throw new InvalidOperationException("Unexpected synthetic routing cleanup root.");
            Directory.Delete(_root, recursive: true);
        }
    }

    private static string JsonHash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    private sealed class FakeCompilation : IGoogleRecoveryCurrentCompilation
    {
        public string Capability { get; set; } = "synthetic-capability";
        public Task<GoogleGenerationProductionPendingApprovalStateOwner.PendingState> CompileReadonlyAsync(
            ShellViewModel.GoogleTtsStudioRequestSelection s, CancellationToken cancellationToken)
        {
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new { input = new { text = s.ExactText }, voice = new { languageCode = "en-US", name = s.VoiceStableId }, audioConfig = new { audioEncoding = "MP3" } });
            var envelope = new GoogleGenerationSubmissionEnvelope(s.AccountStableId, "synthetic-credential", Capability, "synthetic-pricing", 42,
                s.VoiceStableId, "MP3", Convert.ToHexStringLower(SHA256.HashData(payload)), payload.Length);
            var request = new GenerationProviderRequest(GoogleGenerationProvider.StableProviderId, GoogleGenerationProvider.SynthesizeOperationStableId,
                s.AccountStableId, "synthetic-preview-only", payload, "MP3");
            var queue = new GoogleGenerationPersistedQueueState(request.AccountId, request.OperationStableId, request.IdempotencyKey, false, null);
            var trust = new GenerationCacheTrustContext(request.ProviderStableId, s.AccountStableId, s.ProjectStableId, "synthetic-endpoint", "global",
                request.OperationStableId, "synthetic-model", s.VoiceStableId, s.VoiceFingerprint, "synthetic-plan", "en-US", "synthetic-controls", "MP3",
                "synthetic-sample", "synthetic-adapter", "synthetic-compiler", "synthetic-ast", "synthetic-normalization", "synthetic-pricing", Capability,
                "synthetic-governance", "synthetic-feature", "synthetic-account-capability");
            var snapshot = new GoogleGenerationUiExecutionSnapshot(new(s.AccountStableId, s.ProjectStableId, s.VoiceStableId, "synthetic-model", Capability, "MP3"),
                true, true, true, true, request, trust, queue, queue, GoogleGenerationReconciliationResolutionEvidence.None, true, true, false, true);
            return Task.FromResult(new GoogleGenerationProductionPendingApprovalStateOwner.PendingState(envelope, snapshot, "USD", 2, 3).Validate());
        }
    }
}
