using System.Net;
using System.Text.Json;
using CloudScribe.App.Composition;
using CloudScribe.App.ViewModels;
using CloudScribe.Application.Documents;
using CloudScribe.Application.Generation;
using CloudScribe.Application.Providers;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Infrastructure.Persistence;
using CloudScribe.Infrastructure.Providers;
using CloudScribe.Providers.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Architecture.Tests;

public sealed partial class ShellViewModelStateTests
{
    [Fact]
    public async Task CatalogReopenPreservesExactNativeRequestIdentityAcrossClosedWindow()
    {
        await using var f = await CatalogReopenFixture.CreateAsync();
        GoogleGenerationProductionRequestIntent before;
        string capability;
        using (var model = f.NewModel())
        {
            await f.OpenAsync(model);
            Assert.True(model.HasCurrentVoiceLanguageCatalog, model.ConnectedVoiceCatalogStatus);
            before = await f.IntentAsync(model, "en-US-Neural2-D");
            capability = model.SelectedVoiceLabVoice!.CapabilityEvidenceId;
        }
        using var reopened = f.NewModel();
        await f.OpenAsync(reopened);
        var after = await f.IntentAsync(reopened, "en-US-Neural2-D");
        Assert.Equal(before.RequestRevision, after.RequestRevision);
        Assert.Equal(before.IdempotencyKey, after.IdempotencyKey);
        Assert.Equal(capability, reopened.SelectedVoiceLabVoice!.CapabilityEvidenceId);
        Assert.Equal(GoogleSpeechPlanCompiler.Compile(before.Plan, before.CompilationOptions).PayloadSha256,
            GoogleSpeechPlanCompiler.Compile(after.Plan, after.CompilationOptions).PayloadSha256);
        Assert.Equal(0, f.Handler.PaidRequests);
    }

    private sealed partial class CatalogReopenFixture : IAsyncDisposable
    {
        internal const string Account = "synthetic-reopen-account";
        internal const string Project = "synthetic-project";
        internal AutosaveDiskFixture Disk { get; }
        internal AutosaveContextFactory Factory { get; }
        internal TimeProvider Clock { get; } = TimeProvider.System;
        internal RepairVault Vault { get; } = new();
        internal CatalogReopenHttp Handler { get; } = new();
        internal HttpClient Http { get; }
        internal EfProviderAccountStore Accounts { get; }
        internal EfProviderCapabilitySnapshotStore Capabilities { get; }
        internal GoogleGenerationProjectAuthorizationStore Authorizations { get; }
        internal GoogleTextToSpeechCatalogBootstrapService Bootstrap { get; }
        internal GoogleConnectedVoiceCatalogLoader Loader { get; }
        internal Stage7VoiceLabCatalogShellBinder Binder { get; }
        internal Guid DocumentId { get; } = Guid.NewGuid();
        internal Guid RevisionId { get; } = Guid.NewGuid();
        private readonly List<DocumentAutosaveCoordinator> _autosaves = [];

        private CatalogReopenFixture(AutosaveDiskFixture disk)
        {
            Disk = disk;
            Factory = new(new DbContextOptionsBuilder<CloudScribeDbContext>().UseSqlite(
                $"Data Source={disk.Paths.DatabasePath};Pooling=False").Options);
            Accounts = new(Factory, Clock); Capabilities = new(Factory); Authorizations = new(Factory);
            var projects = new VoiceLabProjectAuthorizationStore(Factory);
            Http = new(Handler);
            var resolver = new CatalogReopenCredential();
            var onboarding = new GoogleServiceAccountCredentialOnboardingService(Vault);
            var catalog = new GoogleVoiceCatalogClient(Http, resolver);
            Bootstrap = new(onboarding, catalog, new(Http, resolver, Clock), Accounts, Capabilities, projects, Authorizations, Clock);
            Loader = new(Bootstrap, Accounts);
            var evidence = new VoiceLabCatalogCurrentEvidenceResolver(Accounts, Capabilities, projects, Clock);
            var adapters = new VoiceLabProviderAdapterResolver(new ProviderFactoryRegistry(
                [new GoogleVoiceLabProviderAdapterFactory(Accounts, catalog, onboarding)]));
            var transport = new VoiceLabProductionCatalogTransport(Accounts, Capabilities, evidence.ResolveAsync, adapters, Clock);
            Binder = new(new VoiceLabCatalogQueryService(transport.QueryAsync), projects, evidence, Clock);
        }

        internal static async Task<CatalogReopenFixture> CreateAsync(int additionalVoices = 0)
        {
            var f = new CatalogReopenFixture(await AutosaveDiskFixture.CreateAsync().ConfigureAwait(false));
            f.Handler.AdditionalVoices = additionalVoices;
            string json = JsonSerializer.Serialize(new
            {
                type = "service_account",
                project_id = Project,
                client_email = "synthetic@synthetic-project.iam.gserviceaccount.com",
                private_key_id = "synthetic-key",
                private_key = "-----BEGIN PRIVATE KEY-----\nsynthetic\n-----END PRIVATE KEY-----",
                token_uri = "https://oauth2.googleapis.com/token"
            });
            await f.Bootstrap.ConfigureFreshAsync(Account, "Synthetic", "synthetic-reopen-key", json.AsMemory(),
                new Uri("https://texttospeech.googleapis.com/v1/voices"), new Uri("https://texttospeech.googleapis.com/v1/text:synthesize"),
                "global", TestContext.Current.CancellationToken).ConfigureAwait(false);
            return f;
        }

        internal ShellViewModel NewModel()
        {
            var model = CreateViewModel(clock: Clock);
            var library = Disk.NewLibrary();
            var autosave = new DocumentAutosaveCoordinator(library, Clock); _autosaves.Add(autosave);
            model.ConfigureStage3DocumentWorkflow(library, autosave);
            Binder.Bind(model);
            model.ApplySavedVoiceSetup(new VoiceSetup("Synthetic English", "en-US", "en-US-Neural2-D"));
            return model;
        }

        internal Task OpenAsync(ShellViewModel model) => Loader.OpenAsync(model, TestContext.Current.CancellationToken);

        internal async Task<GoogleGenerationProductionRequestIntent> IntentAsync(ShellViewModel model, string voice)
        {
            model.SelectedVoiceLabVoice = Assert.Single(model.VoiceLabCatalogResults, x => string.Equals(x.VoiceStableId, voice, StringComparison.Ordinal));
            var selected = model.SelectedVoiceLabVoice;
            var selection = new ShellViewModel.GoogleTtsStudioRequestSelection(DocumentId, RevisionId,
                "Exact synthetic saved passage for both independently approved voices.", selected.ProviderStableId, selected.AccountStableId,
                selected.ProjectStableId, selected.VoiceStableId, selected.CapabilityEvidenceId, selected.VoiceFingerprint, "en-US", "en-US", Clock.GetUtcNow());
            var current = (await Capabilities.GetLatestAsync(GoogleGenerationProvider.StableProviderId, Account,
                TestContext.Current.CancellationToken).ConfigureAwait(false))!;
            var capability = new GoogleCapabilitySnapshot(Account, current.Snapshot.ProvenanceId, current.Snapshot.CapturedAtUtc,
                current.ExpiresAtUtc, new HashSet<string>(StringComparer.Ordinal) { voice }, new HashSet<string>(StringComparer.Ordinal) { "MP3" }, 5000);
            return await new GoogleTtsStudioRequestIntentFactory(Authorizations, Clock).CreateAsync(selection, capability,
                TestContext.Current.CancellationToken).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var autosave in _autosaves) await autosave.DisposeAsync().ConfigureAwait(false);
            Http.Dispose(); Vault.Dispose(); await Disk.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class CatalogReopenCredential : ITransientCredentialResolver
    {
        public ValueTask<string> ResolveAccessTokenAsync(string credentialReferenceId, CancellationToken cancellationToken) =>
            ValueTask.FromResult("synthetic-token-not-a-real-credential");
    }

    private sealed class CatalogReopenHttp : HttpMessageHandler
    {
        private static readonly string[] GermanLanguages = ["de-DE"];
        internal int PaidRequests { get; private set; }
        internal int CatalogRequests { get; private set; }
        internal int TotalRequests { get; private set; }
        internal int BlockCatalogAt { get; set; }
        internal int DriftCatalogAt { get; set; }
        internal bool ChangedMetadata { get; set; }
        internal int AdditionalVoices { get; set; }
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            TotalRequests++;
            Assert.Equal("texttospeech.googleapis.com", request.RequestUri!.Host);
            if (request.Method == HttpMethod.Get)
            {
                CatalogRequests++;
                if (CatalogRequests == BlockCatalogAt)
                {
                    Entered.TrySetResult();
                    await Release.Task.WaitAsync(token).ConfigureAwait(false);
                }
                string catalogBody = """
                    {"voices":[{"name":"cmn-CN-Chirp3-HD-Alnilam","languageCodes":["cmn-CN"],"ssmlGender":"MALE","naturalSampleRateHertz":24000},
                    {"name":"en-US-Neural2-D","languageCodes":["en-US"],"ssmlGender":"MALE","naturalSampleRateHertz":24000},
                    {"name":"en-US-Chirp3-HD-Aoede","languageCodes":["en-US"],"ssmlGender":"FEMALE","naturalSampleRateHertz":24000}]}
                    """;
                if (CatalogRequests == DriftCatalogAt || ChangedMetadata) catalogBody = catalogBody.Replace("24000", "48000", StringComparison.Ordinal);
                if (AdditionalVoices > 0) catalogBody = ExpandSyntheticCatalog(catalogBody, AdditionalVoices);
                return new(HttpStatusCode.OK) { Content = new StringContent(catalogBody) };
            }
            string body = await request.Content!.ReadAsStringAsync(token).ConfigureAwait(false);
            if (!string.Equals(body, "{}", StringComparison.Ordinal)) PaidRequests++;
            Assert.Equal("{}", body);
            return new(HttpStatusCode.BadRequest) { Content = new StringContent("""{"error":{"status":"INVALID_ARGUMENT"}}""") };
        }

        private static string ExpandSyntheticCatalog(string original, int additionalVoices)
        {
            using var parsed = JsonDocument.Parse(original);
            var voices = parsed.RootElement.GetProperty("voices").EnumerateArray().Select(x => x.Clone()).ToList();
            for (int i = 0; i < additionalVoices; i++) voices.Add(JsonSerializer.SerializeToElement(new
            {
                name = "de-DE-Standard-Synthetic" + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                languageCodes = GermanLanguages,
                ssmlGender = "MALE",
                naturalSampleRateHertz = 24000
            }));
            return JsonSerializer.Serialize(new { voices });
        }
    }
}
