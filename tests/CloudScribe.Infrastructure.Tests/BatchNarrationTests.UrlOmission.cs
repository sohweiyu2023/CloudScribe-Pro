#pragma warning disable MA0004 // Test code has no synchronization context to preserve.
using System.Text;
using System.Text.Json;
using CloudScribe.Application.Batch;
using CloudScribe.Application.Batch.Authority;
using CloudScribe.Application.Documents;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Batch;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class BatchNarrationTests
{
    [Fact]
    public void UrlSettingIsOptInVersionedAndLegacyHashIsUnchanged()
    {
        string canonical = string.Join('\n', "batch-settings-v1", Settings.LanguageCode, Settings.VoiceStableId, Settings.Engine, Settings.FailurePolicy.ToString());
        Assert.Equal(Sha(canonical), Settings.Sha256());
        Assert.Equal(Settings.Sha256(), (Settings with { SkipUrls = false }).Sha256());
        var enabled = Settings with { SkipUrls = true, UrlTransformVersion = NarrationUrlOmission.CurrentVersion };
        Assert.NotEqual(Settings.Sha256(), enabled.Sha256(), StringComparer.Ordinal);
        Assert.NotEqual(enabled.Sha256(), (enabled with { UrlTransformVersion = "other-version" }).Sha256(), StringComparer.Ordinal);
    }

    [Theory]
    [InlineData("url-omission-v1")]
    [InlineData("url-omission-v2")]
    public async Task ApprovalBindsCopyHashOriginalAndOptionAndRejectsTampering(string transformVersion)
    {
        using var w = new Workspace();
        var settings = Settings with { SkipUrls = true, UrlTransformVersion = transformVersion };
        var batch = await w.CreateAsync(["a.txt"], settings);
        var source = new BatchNarrationSource(Guid.NewGuid(), Guid.NewGuid(), Sha("original"), transformVersion, 1);
        var item = batch.Items[0] with { NarrationTextSha256 = Sha("narration"), NarrationSource = source };
        batch = batch.WithItem(item);
        var profile = Profile(w.Root);
        var approved = new BatchConsentItem(item.ItemId, item.SourceSha256, item.DocumentId!.Value, item.RevisionId!.Value, "plan", 1,
            NarrationTextSha256: item.NarrationTextSha256, NarrationSource: source);
        var consent = BatchConsent.Create(Guid.NewGuid(), batch, BatchConsentSource.ReviewedInstall, profile, Account, Project, [approved], Now);
        Assert.Equal("batch.authorized", Code(profile, consent, batch));
        string other = string.Equals(transformVersion, NarrationUrlOmission.LegacyVersion, StringComparison.Ordinal)
            ? NarrationUrlOmission.CurrentVersion : NarrationUrlOmission.LegacyVersion;
        var changed = settings with { UrlTransformVersion = other };
        Assert.Equal("batch.consent-settings-changed", Code(profile, consent, batch with { Settings = changed, SettingsSha256 = changed.Sha256() }));
        Assert.Equal("batch.consent-settings-changed", Code(profile, consent, batch with { Settings = settings with { SkipUrls = false } }));
        Assert.Equal("batch.consent-files-changed", Code(profile, consent, batch.WithItem(item with { NarrationTextSha256 = Sha("changed") })));
        Assert.Equal("batch.consent-files-changed", Code(profile, consent, batch.WithItem(item with { NarrationSource = source with { OmittedUrlCount = 2 } })));
        Assert.Equal("batch.consent-invalid", Code(profile, consent with { Items = [approved with { NarrationTextSha256 = Sha("changed") }] }, batch));
        Assert.Equal("batch.consent-invalid", Code(profile, consent with { Items = [approved with { NarrationSource = source with { RevisionId = Guid.NewGuid() } }] }, batch));
    }

    [Fact]
    public async Task ChangedOriginalOrChildBlocksUnstartedButKeepsStartedIdentityForReconciliation()
    {
        var library = new MemoryLibrary();
        var original = await library.CreateAsync("Original", "Read https://example.org/path then continue.", Token);
        var transformed = NarrationUrlOmission.Transform(original.Text);
        var child = await library.CreateAsync("Narration", transformed.OutputText, Token);
        var item = new BatchItem(Guid.NewGuid(), 1, "a.txt", Sha(original.Text), 1, child.Text.Length, "a.mp3", BatchItemState.Ready,
            DocumentId: child.Id, RevisionId: child.CurrentRevisionId, NarrationTextSha256: Sha(child.Text),
            NarrationSource: new(original.Id, original.CurrentRevisionId!.Value, Sha(original.Text), transformed.TransformVersion, transformed.OmittedUrlCount));
        var settings = Settings with { SkipUrls = true, UrlTransformVersion = NarrationUrlOmission.CurrentVersion };
        await BatchNarrationIdentity.ValidateAsync(library, settings, item, Token);
        library.Created[0] = original with { Text = "Edited original", CurrentRevisionId = Guid.NewGuid() };
        await Assert.ThrowsAsync<InvalidOperationException>(() => BatchNarrationIdentity.ValidateAsync(library, settings, item, Token));
        await BatchNarrationIdentity.ValidateAsync(library, settings, item with { JobId = Guid.NewGuid(), State = BatchItemState.Uncertain }, Token);
        library.Created[0] = original;
        library.Created[1] = child with { Text = "Edited child", CurrentRevisionId = Guid.NewGuid() };
        await Assert.ThrowsAsync<InvalidOperationException>(() => BatchNarrationIdentity.ValidateAsync(library, settings, item, Token));
        library.Created[1] = child;
        await Assert.ThrowsAsync<InvalidOperationException>(() => BatchNarrationIdentity.ValidateAsync(library, settings, item with { NarrationSource = item.NarrationSource! with { OmittedUrlCount = 99 } }, Token));
    }

    [Fact]
    public async Task CopyCompilesExactShortArticleAndLongAudioPayloadsWithoutChangingOriginal()
    {
        using var w = new Workspace();
        var library = new MemoryLibrary();
        var original = await library.CreateAsync("Original", "First https://example.org/private sentence.\n\nSecond sentence.", Token);
        string sourceBefore = original.Text;
        var result = NarrationUrlOmission.Transform(original.Text);
        var copy = await library.CreateAsync("Narration", result.OutputText, Token);
        var shortRequest = GoogleSpeechPlanCompiler.Compile(new SpeechPlan("cmn-CN", [new SpeechText(copy.Text)], "test"),
            new("cmn-CN", Settings.VoiceStableId, "MP3", 5_000));
        using var wire = JsonDocument.Parse(shortRequest.Payload);
        Assert.Equal(copy.Text, wire.RootElement.GetProperty("input").GetProperty("text").GetString());
        var article = ChirpArticlePlanCompiler.CompileSentences(copy, "cmn-CN", Settings.VoiceStableId);
        Assert.Equal(copy.Id, article.DocumentId); Assert.Equal(copy.CurrentRevisionId, article.RevisionId);
        Assert.Equal(Sha(copy.Text), article.SourceSha256);
        Assert.Equal(copy.Text, string.Concat(article.Parts.Select(x => ChirpNarrationPlanner.Slice(copy.Text, x.Range))));
        var originalArticle = ChirpArticlePlanCompiler.CompileSentences(original, "cmn-CN", Settings.VoiceStableId);
        Assert.Throws<InvalidOperationException>(() => ChirpArticlePlanCompiler.RequireCurrent(originalArticle, copy));
        var settings = Settings with { SkipUrls = true, UrlTransformVersion = NarrationUrlOmission.CurrentVersion };
        var preparedItem = new BatchItem(Guid.NewGuid(), 1, "copy.txt", Sha(sourceBefore), sourceBefore.Length, copy.Text.Length, "copy.mp3", BatchItemState.Ready,
            DocumentId: copy.Id, RevisionId: copy.CurrentRevisionId, NarrationTextSha256: Sha(copy.Text),
            NarrationSource: new(original.Id, original.CurrentRevisionId!.Value, Sha(sourceBefore), result.TransformVersion, result.OmittedUrlCount));
        var batch = new NarrationBatch(NarrationBatch.CurrentVersion, Guid.NewGuid(), "Copy", 1, BatchQueueState.Ready,
            settings, settings.Sha256(), w.Output, "USD", 2, [preparedItem], Now);
        var review = await new BatchAdmissionRequestBuilder(library, new FixedClock(Now)).BuildAsync(batch, Profile(w.Root), Account, Project, cancellationToken: Token);
        var approvedCopy = Assert.Single(review.ProposedConsent.Items);
        Assert.Equal(copy.Id, approvedCopy.DocumentId); Assert.Equal(copy.CurrentRevisionId, approvedCopy.RevisionId);
        Assert.Equal(Sha(copy.Text), approvedCopy.NarrationTextSha256); Assert.Equal(preparedItem.NarrationSource, approvedCopy.NarrationSource);
        Assert.Equal(article.PlanSha256, approvedCopy.PlanSha256);
        var rawHash = Sha("\uFEFF" + sourceBefore.Replace("\n", "\r\n", StringComparison.Ordinal));
        var id = Guid.NewGuid(); string uri = $"gs://private-bucket/batch/{id:N}.wav";
        var request = GoogleLongAudioRequest.Create(id, "123456", "global", "cred", uri, copy.Text, "cmn-CN", Settings.VoiceStableId, "LINEAR16");
        var job = new GoogleLongAudioBatchJob(Guid.NewGuid(), Guid.NewGuid(), copy.Id, copy.CurrentRevisionId!.Value, rawHash, id,
            uri, 1_000_000, 24000, 1, request.BindingSha256, 1, 3, true, NarrationTextSha256: Sha(copy.Text));
        var scope = BatchScope() with { ApprovedRequestCeilingsUsdCents = new Dictionary<string, long>(StringComparer.Ordinal) { [request.BindingSha256] = 1 } };
        var compiled = GoogleLongAudioDocumentCompiler.Compile(scope, job.ToDocumentJob(), copy.Text);
        using var longWire = JsonDocument.Parse(compiled.CopyPayload());
        Assert.Equal(copy.Text, longWire.RootElement.GetProperty("input").GetProperty("text").GetString());
        Assert.Equal(rawHash, job.SourceSha256); Assert.Equal(Sha(copy.Text), job.ToDocumentJob().SourceSha256);
        Assert.Equal(rawHash, (job with { NarrationTextSha256 = null }).ToDocumentJob().SourceSha256);
        Assert.Throws<InvalidDataException>(() => GoogleLongAudioDocumentCompiler.Compile(scope, job.ToDocumentJob(), sourceBefore));
        Assert.Equal(sourceBefore, (await library.OpenAsync(original.Id, Token))!.Text);
    }

    [Fact]
    public void LongAudioBoundsAndCostUseNarrationTextNotRemovedUrlCharacters()
    {
        string original = "Keep this sentence. https://example.org/" + new string('a', 1_000_001);
        string narration = NarrationUrlOmission.Transform(original).OutputText;
        var context = new BatchLongAudioPlanner.Context(new("p", new string('c', 64), "s", new string('d', 64), "l", "o", "u", BatchScope()), 100, 100, null);
        var batch = new NarrationBatch(2, Guid.NewGuid(), "b", 1, BatchQueueState.Ready, Settings, Settings.Sha256(), "out", "USD", 2, [], Now);
        var item = new BatchItem(Guid.NewGuid(), 1, "a.txt", Sha(original), original.Length, narration.Length, "a.mp3", BatchItemState.Ready);
        Assert.Null(BatchLongAudioPlanner.Plan(context, batch, item, original).Approval);
        var approved = Assert.IsType<BatchLongAudioApproval>(BatchLongAudioPlanner.Plan(context, batch, item, narration).Approval);
        Assert.Equal(1, approved.CeilingUsdCents);
    }
    [Fact]
    public void RecoveryChildrenStayInTheSavedNarrationCopyLineage()
    {
        const string original = "First paragraph https://example.org/first.\n\nSecond paragraph https://example.org/second.";
        string narration = NarrationUrlOmission.Transform(original).OutputText;
        var id = Guid.NewGuid();
        var root = GoogleLongAudioRequest.Create(id, "123456", "global", "cred", $"gs://private-bucket/{id:N}.wav",
            narration, "cmn-CN", Settings.VoiceStableId, "LINEAR16");
        var rejected = new GoogleLongAudioCheckpoint(root.AttemptId, root.BindingSha256, root.ProjectNumber, root.Location,
            root.CredentialReferenceId, root.OutputGcsUri, root.PayloadSha256, root.PayloadBytes, GoogleLongAudioState.Failed,
            "projects/123456/locations/global/operations/synthetic", 200, 3, 4)
        {
            FailureKind = GoogleLongAudioFailureKind.SentenceLengthReported,
            Observation = new(Guid.NewGuid(), GoogleResponseReadState.Complete, GoogleLongAudioParseState.Json,
                GoogleErrorStatus.NotProvided, 300, GoogleExplanationRetentionState.SavedPrivately, true, false, true),
        };
        var range = new ChirpNarrationRange(0, narration.EnumerateRunes().Count(), Sha(narration), null);
        var children = GoogleLongAudioRecoveryPlanner.Split(narration, range, root, rejected, false, 30m);
        var wires = children.Select(x => JsonDocument.Parse(x.Request.CopyPayload())).ToArray();
        try
        {
            Assert.Equal(narration, string.Concat(wires.Select(x => x.RootElement.GetProperty("input").GetProperty("text").GetString())));
            Assert.All(children, child => Assert.NotEqual(root.AttemptId, child.Request.AttemptId));
            Assert.DoesNotContain("https://", string.Concat(wires.Select(x => x.RootElement.GetRawText())), StringComparison.Ordinal);
            Assert.Throws<InvalidDataException>(() => GoogleLongAudioRecoveryPlanner.Split(original,
                new(0, original.EnumerateRunes().Count(), Sha(original), null), root, rejected, false, 30m));
        }
        finally { foreach (var wire in wires) wire.Dispose(); }
    }

    [Fact]
    public async Task StaleDerivedLongAudioApprovalDisappearsUntilCommittedThenKeepsExactJob()
    {
        using var w = new Workspace();
        var batch = await w.CreateAsync(["a.txt"], Settings with { SkipUrls = true, UrlTransformVersion = NarrationUrlOmission.CurrentVersion });
        var item = batch.Items[0];
        string authorityPath = Path.Combine(w.Root, "install", "batch-authority");
        Directory.CreateDirectory(authorityPath);
        var profile = Profile(w.Root, BatchAuthorityMode.InAppWithinLimits);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(profile, InstalledBatchAuthorityStore.Json);
        await File.WriteAllBytesAsync(Path.Combine(authorityPath, "profile.json"), bytes, Token);
        await File.WriteAllTextAsync(Path.Combine(authorityPath, "pins.json"), JsonSerializer.Serialize(new BatchAuthorityPins(1,
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), new Dictionary<string, string>(StringComparer.Ordinal))), Token);
        var authority = new InstalledBatchAuthorityStore(Path.Combine(w.Root, "install", "app"), w.Root);
        var la = BatchApproval('a');
        var source = new BatchNarrationSource(Guid.NewGuid(), Guid.NewGuid(), Sha("Read https://example.org"), NarrationUrlOmission.CurrentVersion, 1);
        var consent = BatchConsent.Create(Guid.NewGuid(), batch, BatchConsentSource.InApp, profile, Account, Project,
            [new(item.ItemId, item.SourceSha256, item.DocumentId!.Value, item.RevisionId!.Value, la.RequestBindingSha256,
                la.FamilyMaximumUsdCents, BatchEngines.LongAudio, la, Sha("Read "), source)], Now.AddMinutes(-3));
        item = item with { NarrationTextSha256 = Sha("Read "), NarrationSource = source };
        batch = await w.Store.SaveAsync(batch.WithItem(item), Token);
        await authority.SaveInAppConsentAsync(consent, Token);
        var copy = new DocumentSnapshot(item.DocumentId.Value, "Narration", "Read ", 0, 0,
            CloudScribe.Domain.Documents.DocumentStatus.Active, false, item.RevisionId, null, null, 1);
        var copies = new StubCopyStore(new(source.DocumentId, source.RevisionId, source.TextSha256, copy, 1, source.TransformVersion));
        var library = new MemoryLibrary();
        library.Created.Add(new(source.DocumentId, "Original", "Read https://example.org", 0, 0,
            CloudScribe.Domain.Documents.DocumentStatus.Active, false, source.RevisionId, null, null, 1));
        library.Created.Add(copy);
        var extension = new BatchLongAudioScopeExtensionSource(authority, new FixedClock(Now), copies, w.Store, library);
        string pin = new('d', 64), ledger = Path.Combine(w.Root, "url-ledger.json");
        Assert.Single((await extension.LoadAsync(BatchScope(), pin, ledger, 0, Token)).Jobs);
        await AssertUncommittedScopeChangesAsync(w, batch, item, consent, authority, extension, pin, ledger);
        copies.Stale = true;
        Assert.Empty((await extension.LoadAsync(BatchScope(), pin, ledger, 0, Token)).Jobs);
        int checkedBeforeCommit = copies.Reads;
        await File.WriteAllTextAsync(ledger, JsonSerializer.Serialize(new GoogleLongAudioBudgetLedger(1, pin,
            [new(la.AttemptId, la.RequestBindingSha256, la.CeilingUsdCents, null)])), Token);
        var retained = Assert.Single((await extension.LoadAsync(BatchScope(), pin, ledger, 0, Token)).Jobs);
        Assert.True(retained.Committed); Assert.Equal(la.AttemptId, retained.AttemptId);
        Assert.Equal(la.RequestBindingSha256, retained.RequestBindingSha256);
        Assert.Equal(Sha(copy.Text), retained.ToDocumentJob().SourceSha256);
        Assert.Equal(checkedBeforeCommit, copies.Reads);
    }

    private static async Task AssertUncommittedScopeChangesAsync(Workspace w, NarrationBatch batch, BatchItem item, BatchConsent consent,
        InstalledBatchAuthorityStore authority, BatchLongAudioScopeExtensionSource extension, string pin, string ledger)
    {
        var omitted = consent with { ConsentId = Guid.NewGuid(), CreatedUtc = consent.CreatedUtc.AddSeconds(1), Items = [consent.Items[0] with { NarrationSource = null }] };
        omitted = omitted with { ConsentSha256 = omitted.ComputeSha256() };
        await authority.SaveInAppConsentAsync(omitted, Token);
        var noSourceItem = item with { NarrationSource = null };
        batch = await w.Store.SaveAsync(batch.WithItem(noSourceItem), Token);
        Assert.Empty((await extension.LoadAsync(BatchScope(), pin, ledger, 0, Token)).Jobs);
        consent = await ReplaceScopeConsentAsync(authority, consent with { CreatedUtc = omitted.CreatedUtc }, consent.SettingsSha256);
        batch = await w.Store.SaveAsync(batch.WithItem(item), Token);
        var unchangedSettings = batch.Settings;
        var changedSettings = batch.Settings with { SkipUrls = false, UrlTransformVersion = null };
        batch = await w.Store.SaveAsync(batch with { Settings = changedSettings, SettingsSha256 = changedSettings.Sha256() }, Token);
        Assert.Empty((await extension.LoadAsync(BatchScope(), pin, ledger, 0, Token)).Jobs);
        foreach (var invalidSettings in new[] { changedSettings, unchangedSettings with { UrlTransformVersion = "unsupported" } })
        {
            consent = await ReplaceScopeConsentAsync(authority, consent, invalidSettings.Sha256());
            batch = await w.Store.SaveAsync(batch with { Settings = invalidSettings, SettingsSha256 = invalidSettings.Sha256() }, Token);
            Assert.Empty((await extension.LoadAsync(BatchScope(), pin, ledger, 0, Token)).Jobs);
        }
        consent = await ReplaceScopeConsentAsync(authority, consent, unchangedSettings.Sha256());
        batch = await w.Store.SaveAsync(batch with { Settings = unchangedSettings, SettingsSha256 = unchangedSettings.Sha256() }, Token);
        batch = await w.Store.SaveAsync(batch.WithItem(item with { State = BatchItemState.Skipped }), Token);
        Assert.Empty((await extension.LoadAsync(BatchScope(), pin, ledger, 0, Token)).Jobs);
        batch = await w.Store.SaveAsync(batch.WithItem(item), Token);
    }

    private static async Task<BatchConsent> ReplaceScopeConsentAsync(InstalledBatchAuthorityStore authority, BatchConsent consent, string settingsHash)
    {
        var next = consent with { ConsentId = Guid.NewGuid(), CreatedUtc = consent.CreatedUtc.AddSeconds(1), SettingsSha256 = settingsHash };
        next = next with { ConsentSha256 = next.ComputeSha256() };
        await authority.SaveInAppConsentAsync(next, Token);
        return next;
    }

    private sealed class StubCopyStore(NarrationCopyPreparation? copy) : INarrationCopyStore
    {
        public bool Stale { get; set; }
        public int Reads { get; private set; }
        public Task<NarrationCopyPreparation?> FindAsync(Guid narrationDocumentId, CancellationToken cancellationToken = default)
        {
            Reads++;
            if (Stale) throw new InvalidOperationException("Synthetic stale original.");
            return Task.FromResult<NarrationCopyPreparation?>(copy);
        }
        public Task<NarrationCopyPreparation> PrepareAsync(Guid sourceDocumentId, Guid sourceRevisionId, string sourceTextSha256, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<NarrationCopyPreparation> PrepareAsync(Guid sourceDocumentId, Guid sourceRevisionId, string sourceTextSha256, string transformVersion, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<DocumentSnapshot> ImportBatchSourceAsync(Guid batchId, Guid itemId, string rawSha256, string title, string normalizedText, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

}
