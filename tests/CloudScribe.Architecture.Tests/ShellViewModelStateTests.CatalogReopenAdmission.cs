using CloudScribe.App.Composition;
using CloudScribe.App.ViewModels;
using CloudScribe.Application.Documents;
using CloudScribe.Application.Generation;
using CloudScribe.Application.Pricing;
using CloudScribe.Domain.Pricing;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Infrastructure.Pricing;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Architecture.Tests;

public sealed partial class ShellViewModelStateTests
{
    [Theory]
    [InlineData("unchanged")]
    [InlineData("explicit-refresh")]
    [InlineData("expired")]
    [InlineData("voice-metadata")]
    public async Task CatalogReopenActualAdmissionPreservesNativeReviewButChangedEvidenceRejectsIt(string scenario)
    {
        await using var f = await CatalogReopenFixture.CreateAsync();
        await using var installation = await AutosaveDiskFixture.CreateAsync();
        await f.InitializeNativeCompilerAsync();
        var first = await f.Disk.NewLibrary().CreateAsync("Synthetic exact Neural2 article", CatalogReopenFixture.Text, TestContext.Current.CancellationToken);
        var second = await f.Disk.NewLibrary().CreateAsync("Synthetic exact Aoede article", CatalogReopenFixture.Text, TestContext.Current.CancellationToken);
        var savedSetup = new VoiceSetup("Original Chinese setup", "cmn-CN", "cmn-CN-Chirp3-HD-Alnilam");
        new StudioPreferencesStore(f.Disk.Paths).Remember(savedSetup);
        List<CatalogReopenArticle> approved = [];
        using (var before = f.NewModel())
        {
            before.ConfigureSavedVoiceSetups(new StudioPreferencesStore(f.Disk.Paths));
            await f.OpenAsync(before);
            Assert.True(before.HasCurrentVoiceLanguageCatalog, before.ConnectedVoiceCatalogStatus);
            string chineseCapability = before.SelectedVoiceLabVoice!.CapabilityEvidenceId;
            before.ApplySavedVoiceSetup(new VoiceSetup("English comparison", "en-US", "en-US-Neural2-D"));
            Assert.True(await f.Loader.ApplyFiltersAsync(before, TestContext.Current.CancellationToken), before.ConnectedVoiceCatalogStatus);
            foreach (var item in new[] { (first, "en-US-Neural2-D"), (second, "en-US-Chirp3-HD-Aoede") })
            {
                var selection = await f.SelectionAsync(before, item.Item1, item.Item2);
                Assert.Equal(chineseCapability, selection.CapabilityEvidenceId);
                approved.Add(await f.CompileArticleAsync(selection, item.Item1));
            }
            before.ApplySavedVoiceSetup(savedSetup);
            Assert.Equal(savedSetup, new StudioPreferencesStore(f.Disk.Paths).State.LastUsed);
        }
        var installed = await f.InstallAdmissionAsync(installation.Paths.RootDirectory, approved);
        var service = new ChirpArticleService(f.Factory, installed.Store, f.Disk.Paths, f.Clock);
        var submission = new GoogleGenerationProductionSubmissionStateOwner(new GoogleGenerationSpendAuthorizationStore(f.Factory, f.Clock, articles: service));
        var preparation = new ChirpArticlePreparationService(service, f.NativeCompiler!, submission, f.Clock);
        using var reopened = f.NewModel();
        reopened.ConfigureSavedVoiceSetups(new StudioPreferencesStore(f.Disk.Paths));
        await f.ApplyReopenMutationAsync(scenario);
        if (string.Equals(scenario, "explicit-refresh", StringComparison.Ordinal)) Assert.True(await f.Loader.RefreshAsync(reopened, TestContext.Current.CancellationToken), reopened.ConnectedVoiceCatalogStatus);
        else await f.OpenAsync(reopened);
        Assert.True(reopened.HasCurrentVoiceLanguageCatalog, reopened.ConnectedVoiceCatalogStatus);
        await AssertAdmittedNativeReviewsAsync(f, approved, preparation, reopened, !string.Equals(scenario, "unchanged", StringComparison.Ordinal));
        await AssertCatalogReopenHasNoPaidEffectsAsync(f, installed, submission);
    }

    private static async Task AssertAdmittedNativeReviewsAsync(CatalogReopenFixture f, List<CatalogReopenArticle> approved,
        ChirpArticlePreparationService preparation, ShellViewModel reopened, bool explicitRefresh)
    {
        foreach (var article in approved)
        {
            var selection = await f.SelectionAsync(reopened, article.Document, article.Article.Proposal.VoiceName).ConfigureAwait(false);
            var recompiled = await f.CompileArticleAsync(selection, article.Document).ConfigureAwait(false);
            for (int index = 0; index < article.Pending.Count; index++)
            {
                Assert.Equal(article.Pending[index].Envelope.CompiledPayloadSha256, recompiled.Pending[index].Envelope.CompiledPayloadSha256);
                Assert.Equal(article.Pending[index].CurrentEstimateMinorUnits, recompiled.Pending[index].CurrentEstimateMinorUnits);
                if (explicitRefresh)
                {
                    Assert.NotEqual(article.Pending[index].Envelope.RequestRevision, recompiled.Pending[index].Envelope.RequestRevision);
                    var error = await Assert.ThrowsAsync<InvalidOperationException>(() => preparation.PrepareAsync(selection, index + 1, TestContext.Current.CancellationToken)).ConfigureAwait(false);
                    Assert.Contains("Additional family credential, capability, pricing, revision, payload or estimate changed", error.Message, StringComparison.Ordinal);
                }
                else
                {
                    Assert.Equal(article.Pending[index].Envelope, recompiled.Pending[index].Envelope);
                    Assert.Equal(article.Pending[index].Snapshot.ProviderRequest.IdempotencyKey, recompiled.Pending[index].Snapshot.ProviderRequest.IdempotencyKey);
                    var review = await preparation.PrepareAsync(selection, index + 1, TestContext.Current.CancellationToken).ConfigureAwait(false);
                    Assert.Equal(article.Pending[index].CurrentEstimateMinorUnits, review.EstimateMinorUnits);
                    preparation.Discard(review.PreviewId);
                    await Assert.ThrowsAsync<InvalidOperationException>(() => preparation.ApproveWholeArticleRequestAsync(selection, Guid.NewGuid(), review.PreviewId, TestContext.Current.CancellationToken)).ConfigureAwait(false);
                }
            }
        }
    }

    private static async Task AssertCatalogReopenHasNoPaidEffectsAsync(CatalogReopenFixture f, CatalogReopenInstallation installed,
        GoogleGenerationProductionSubmissionStateOwner submission)
    {
        Assert.Null(await f.PendingOwner.ResolveCurrentAsync(TestContext.Current.CancellationToken).ConfigureAwait(false));
        Assert.Null(await submission.ResolveCurrentAsync(TestContext.Current.CancellationToken).ConfigureAwait(false));
        using var db = f.Factory.CreateDbContext();
        foreach (string sql in new[] { "SELECT COUNT(*) AS Value FROM chirp_article_attempts", "SELECT COUNT(*) AS Value FROM google_generation_queue_states",
            "SELECT COUNT(*) AS Value FROM google_generation_spend_authorizations", "SELECT COUNT(*) AS Value FROM chirp_whole_article_jobs" })
            Assert.Equal(0, await db.Database.SqlQueryRaw<int>(sql).SingleAsync(TestContext.Current.CancellationToken).ConfigureAwait(false));
        Assert.Equal(installed.MarkerBefore, File.ReadAllBytes(installed.MarkerPath));
        Assert.Equal(installed.LedgerBefore, File.ReadAllBytes(installed.LedgerPath));
        Assert.Equal(installed.FamilySha256, ChirpPolicyValidation.Hash(Assert.Single((await installed.Store.LoadCatalogVerifiedAsync(
            TestContext.Current.CancellationToken).ConfigureAwait(false)).Families)));
        Assert.Equal(0, f.Handler.PaidRequests);
    }

    private sealed record CatalogReopenArticle(DocumentSnapshot Document, AdmittedChirpArticle Article,
        IReadOnlyList<GoogleGenerationProductionPendingApprovalStateOwner.PendingState> Pending);

    private sealed partial class CatalogReopenFixture
    {
        internal const string Text = "Synthetic exact same saved comparison. " +
            "The first paragraph is a deliberately ordinary local fixture with no personal content. " +
            "A second sentence carries the same independently compiled words to both voices. " +
            "The third sentence ensures Chirp needs more than one request without changing native source boundaries. " +
            "The final sentence finishes this synthetic comparison and must be preserved exactly.";
        internal GoogleRecoveryCurrentCompilation? NativeCompiler { get; private set; }
        internal GoogleGenerationProductionPendingApprovalStateOwner PendingOwner { get; } = new();

        internal async Task InitializeNativeCompilerAsync()
        {
            var controls = new V222ControlSet(new StrictJsonObjectReader());
            var pricing = new EfPricingCatalogHistoryStore(Factory, Clock);
            var snapshot = await pricing.SaveSnapshotAsync(controls.PricingSeedUtf8, PricingCatalogTrustState.ValidUnsigned,
                new(PricingCatalogSourceKind.BuiltInSeed, "Authenticated built-in synthetic test"), cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(false);
            await pricing.ActivateAsync(new(snapshot.Id, snapshot.Sha256, 0, PricingCatalogActivationKind.Activate,
                PricingCatalogApprovalKind.ManualUnsigned, true, "Synthetic fixture activation"), TestContext.Current.CancellationToken).ConfigureAwait(false);
            var evidence = new GoogleGenerationProductionEvidenceResolver(Accounts, Capabilities, Clock);
            var preflight = new GoogleTtsStudioPreflightEvidenceResolver(evidence,
                new(Clock, new GoogleServiceAccountCredentialOnboardingService(Vault)), pricing, Vault, controls, Clock);
            NativeCompiler = new(preflight, new(Authorizations, Clock), new(controls),
                new(new GoogleGenerationProductionPendingApprovalPublisher(PendingOwner), evidence), controls, Clock);
        }

        internal async Task<ShellViewModel.GoogleTtsStudioRequestSelection> SelectionAsync(ShellViewModel model, DocumentSnapshot document, string voice)
        {
            model.VoiceLabLocaleFilter = "en-US";
            Assert.True(await Loader.ApplyFiltersAsync(model, TestContext.Current.CancellationToken).ConfigureAwait(false), model.ConnectedVoiceCatalogStatus);
            var selected = Assert.Single(model.VoiceLabCatalogResults, x => string.Equals(x.VoiceStableId, voice, StringComparison.Ordinal));
            await model.OpenDocumentCommand.ExecuteAsync(document.Id).ConfigureAwait(false);
            Assert.Equal(document.Id, model.CurrentDocumentId);
            Assert.Equal(document.CurrentRevisionId, model.CurrentRevisionId);
            Assert.False(model.IsDocumentDirty);
            var comparison = GoogleVoiceComparisonSelection.TryCreate(selected, "en-US"); Assert.NotNull(comparison);
            model.VoiceLabLocaleFilter = "cmn-CN";
            comparison.ApplyToStudio(model);
            Assert.Same(selected, model.SelectedVoiceLabVoice);
            var captured = model.CaptureGoogleTtsStudioRequestSelection();
            Assert.Equal(document.Text, captured.ExactText); Assert.Equal("en-US", captured.LanguageCode);
            return captured;
        }

        internal async Task ApplyReopenMutationAsync(string scenario)
        {
            if (string.Equals(scenario, "voice-metadata", StringComparison.Ordinal)) Handler.ChangedMetadata = true;
            if (!string.Equals(scenario, "expired", StringComparison.Ordinal)) return;
            using var db = Factory.CreateDbContext();
            long captured = Clock.GetUtcNow().AddHours(-1).ToUnixTimeMilliseconds(), expired = Clock.GetUtcNow().AddSeconds(-1).ToUnixTimeMilliseconds();
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE provider_capability_snapshots SET CapturedAtUnixMilliseconds={captured},ExpiresAtUnixMilliseconds={expired};",
                TestContext.Current.CancellationToken).ConfigureAwait(false);
        }

        internal async Task<CatalogReopenArticle> CompileArticleAsync(ShellViewModel.GoogleTtsStudioRequestSelection selection, DocumentSnapshot document)
        {
            var proposal = ChirpArticlePlanCompiler.Compile(document, selection.LanguageCode, selection.VoiceStableId);
            var texts = ChirpNarrationPlanner.SliceAll(document.Text, proposal.Parts.Select(x => x.Range).ToArray());
            List<GoogleGenerationProductionPendingApprovalStateOwner.PendingState> pending = [];
            foreach (var part in proposal.Parts)
            {
                var native = await NativeCompiler!.CompileArticleReadonlyAsync(selection,
                    new(document, proposal, part, texts[part.CoverageOrdinal - 1]), TestContext.Current.CancellationToken).ConfigureAwait(false);
                Assert.Equal(part.PayloadSha256, native.Envelope.CompiledPayloadSha256);
                Assert.Equal(part.PayloadBytes, native.Envelope.CompiledPayloadBytes);
                pending.Add(native);
            }
            var history = new[] { new ChirpHistoricalRange(document.Id, document.CurrentRevisionId!.Value, 0, proposal.SourceScalars, proposal.SourceSha256, []) };
            var article = new AdmittedChirpArticle(proposal, ChirpPlanLineageMapper.Map(document.Text, proposal.Parts.Select(x => x.Range).ToArray(), history), [],
                proposal.Parts.Select((part, index) => new ChirpSegmentAllowance(part.CoverageOrdinal, pending[index].CurrentEstimateMinorUnits)).ToArray());
            return new(document, article, pending);
        }
    }
}
