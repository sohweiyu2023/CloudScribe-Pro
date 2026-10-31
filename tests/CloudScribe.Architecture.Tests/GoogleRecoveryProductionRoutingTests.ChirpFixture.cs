using System.Security.Cryptography;
using System.Text.Json;
using CloudScribe.App.Composition;
using CloudScribe.App.ViewModels;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Files;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Architecture.Tests;

public sealed partial class GoogleRecoveryProductionRoutingTests
{
    private sealed partial class Fixture
    {
        public ChirpCompilation ChirpCompiler { get; } = new();
        public ChirpArticleService CreateArticles() => new(this,
            new InstalledChirpArticleAdmissionStore(Path.Combine(_root, "install", "app"), _paths, TimeProvider.System), _paths, TimeProvider.System);

        public async Task InitializeChirpAsync()
        {
            using var db = CreateDbContext(); await db.Database.MigrateAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
            var library = new EfDocumentLibrary(this, new DocumentContentStore(_paths), TimeProvider.System);
            string text = string.Join(' ', Enumerable.Repeat("Synthetic local clause;", 30));
            var document = await library.CreateAsync("Synthetic internal article", text, TestContext.Current.CancellationToken).ConfigureAwait(false);
            Selection = new(document.Id, document.CurrentRevisionId, text, GoogleGenerationProvider.StableProviderId, "synthetic-account", "synthetic-project",
                "cmn-CN-Chirp3-HD-Alnilam", "synthetic-capability", "synthetic-fingerprint", "cmn-CN", "cmn-CN", DateTimeOffset.UtcNow);
            var proposal = ChirpArticlePlanCompiler.Compile(document, Selection.LanguageCode, Selection.VoiceStableId);
            var lineage = ChirpPlanLineageMapper.Map(text, proposal.Parts.Select(x => x.Range).ToArray(),
                [new(document.Id, document.CurrentRevisionId!.Value, 0, text.EnumerateRunes().Count(), ArticleAudioManifest.HashText(text), [])]);
            string moduleHash = Convert.ToHexStringLower(SHA256.HashData("synthetic-module"u8));
            var policy = new ChirpArticleAdmissionPolicy(1, moduleHash, moduleHash, InstalledRecoveryAdmissionStore.ProfileHash(_root),
                Selection.AccountStableId, Selection.ProjectStableId, "USD", 2, DateTimeOffset.UtcNow.AddHours(1), new string('c', 64),
                new(148, 300, 18, 144, new string('a', 64), [], [], [], []), [], [],
                [new(proposal, lineage, [], proposal.Parts.Select(x => new ChirpSegmentAllowance(x.CoverageOrdinal, 1)).ToArray())], 3, 3);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(policy);
            File.WriteAllBytes(Path.Combine(_root, "install", "chirp-article-admission-policy.json"), bytes);
            File.WriteAllText(Path.Combine(_root, "install", "cloudscribe-install.json"), JsonSerializer.Serialize(new
            { Product = "CloudScribe Pro", AppSha256 = moduleHash, ChirpArticlePolicySha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) }));
        }
    }

    private sealed class ChirpCompilation : IGoogleRecoveryCurrentCompilation
    {
        public string Capability { get; set; } = "synthetic-capability";
        public int Revision { get; set; } = 42;
        public string PayloadSuffix { get; set; } = string.Empty;
        public string Pricing { get; set; } = "synthetic-pricing";
        public bool FullRequestIdentity { get; set; }
        public TaskCompletionSource<bool>? Block { get; set; }
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<GoogleGenerationProductionPendingApprovalStateOwner.PendingState> CompileReadonlyAsync(
            ShellViewModel.GoogleTtsStudioRequestSelection s, CancellationToken cancellationToken)
        {
            Entered.TrySetResult(true);
            if (Block is { } block) await block.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            var compiled = GoogleSpeechPlanCompiler.Compile(new SpeechPlan(s.LanguageCode, [new SpeechText(s.ExactText + PayloadSuffix)], "synthetic-current"),
                new(s.LanguageCode, s.VoiceStableId, "MP3", 5000));
            var envelope = new GoogleGenerationSubmissionEnvelope(s.AccountStableId, "synthetic-credential", Capability, Pricing, Revision,
                s.VoiceStableId, "MP3", compiled.PayloadSha256, compiled.Payload.Length);
            var request = new CloudScribe.Providers.Abstractions.GenerationProviderRequest(GoogleGenerationProvider.StableProviderId,
                GoogleGenerationProvider.SynthesizeOperationStableId, s.AccountStableId,
                FullRequestIdentity ? "studio-google-tts:" + ChirpResumeSelectionBinding.Hash(s) : "synthetic-preview-only", compiled.Payload, "MP3");
            var queue = new GoogleGenerationPersistedQueueState(request.AccountId, request.OperationStableId, request.IdempotencyKey, false, null);
            var trust = new GenerationCacheTrustContext(request.ProviderStableId, s.AccountStableId, s.ProjectStableId, "synthetic-endpoint", "global",
                request.OperationStableId, "synthetic-model", s.VoiceStableId, s.VoiceFingerprint, "synthetic-plan", s.LanguageCode, "synthetic-controls", "MP3",
                "synthetic-sample", "synthetic-adapter", "synthetic-compiler", "synthetic-ast", "synthetic-normalization", Pricing, Capability,
                "synthetic-governance", "synthetic-feature", "synthetic-account-capability");
            var snapshot = new GoogleGenerationUiExecutionSnapshot(new(s.AccountStableId, s.ProjectStableId, s.VoiceStableId, "synthetic-model", Capability, "MP3"),
                true, true, true, true, request, trust, queue, queue, GoogleGenerationReconciliationResolutionEvidence.None, true, true, false, true);
            return new GoogleGenerationProductionPendingApprovalStateOwner.PendingState(envelope, snapshot, "USD", 2, 1).Validate();
        }
    }
}
