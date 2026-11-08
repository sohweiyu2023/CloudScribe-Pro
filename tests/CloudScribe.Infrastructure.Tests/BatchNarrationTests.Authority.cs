#pragma warning disable MA0004 // Test code has no synchronization context to preserve.
using System.Text.Json;
using CloudScribe.Application.Batch;
using CloudScribe.Application.Batch.Authority;
using CloudScribe.Infrastructure.Batch;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class BatchNarrationTests
{
    private const string Account = "account-1", Project = "project-1";
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Defaults agreed for batches: reviewed mode, S$25 per batch and S$100 per 30 days (at 1.30 SGD per USD).</summary>
    private static BatchAuthorityProfile Profile(string root, BatchAuthorityMode mode = BatchAuthorityMode.Reviewed) => new(
        BatchAuthorityProfile.CurrentSchemaVersion, mode, InstalledRecoveryAdmissionStore.ProfileHash(root), Account, Project, [],
        [BatchEngines.CompleteArticle, BatchEngines.LongAudio], BatchFailurePolicy.SkipAndContinue, "USD", 2,
        PerBatchCeilingMinorUnits: BatchMoney.ToMinor(25m / 1.30m, 2), RollingCeilingMinorUnits: BatchMoney.ToMinor(100m / 1.30m, 2),
        RollingWindowDays: 30, MaximumFilesPerBatch: 500, ConsentLifetime: TimeSpan.FromDays(2), ExpiresUtc: Now.AddDays(90),
        DisplayCurrency: "SGD", DisplayRate: 1.30m, EstimateMinorUnitsPerMillionCharacters: 3000);

    private static BatchConsent Consent(NarrationBatch batch, BatchAuthorityProfile profile, long perFile = 100,
        BatchConsentSource source = BatchConsentSource.ReviewedInstall) =>
        BatchConsent.Create(Guid.NewGuid(), batch, source, profile, Account, Project,
            batch.Included.Select(x => new BatchConsentItem(x.ItemId, x.SourceSha256, x.DocumentId!.Value, x.RevisionId!.Value, "plan-" + x.Order, perFile)).ToArray(), Now);

    private static string Code(BatchAuthorityProfile? profile, BatchConsent? consent, NarrationBatch batch, long others = 0,
        string account = Account, DateTimeOffset? at = null) =>
        BatchAuthorityEvaluator.Evaluate(profile, consent, batch, account, Project, others, at ?? Now).ReasonCode;

    [Fact]
    public async Task EvaluatorEnforcesProfileConsentAndCeilings()
    {
        using var w = new Workspace();
        var batch = await w.CreateAsync(["a.txt", "b.txt"]);
        var profile = Profile(w.Root);
        Assert.Null(profile.Problem());
        Assert.Equal(1924, profile.PerBatchCeilingMinorUnits); // US$19.24 is about S$25
        Assert.Equal("batch.authority-not-configured", Code(null, null, batch));
        Assert.Equal("batch.authority-off", Code(profile with { Mode = BatchAuthorityMode.Off }, null, batch));
        Assert.Equal("batch.authority-expired", Code(profile, null, batch, at: profile.ExpiresUtc));
        Assert.Equal("batch.authority-account-mismatch", Code(profile, null, batch, account: "other"));
        Assert.Equal("batch.authority-voice", Code(profile with { AllowedVoiceStableIds = ["en-US-Chirp3-HD-Kore"] }, null, batch));
        Assert.Equal("batch.authority-engine", Code(profile with { AllowedEngines = [BatchEngines.LongAudio] }, null, batch));
        Assert.Equal("batch.authority-file-limit", Code(profile with { MaximumFilesPerBatch = 1 }, null, batch));
        Assert.Equal("batch.consent-missing-reviewed", Code(profile, null, batch));

        var consent = Consent(batch, profile);
        Assert.Equal("batch.authorized", Code(profile, consent, batch));
        Assert.Equal("batch.consent-needs-review", Code(profile, Consent(batch, profile, source: BatchConsentSource.InApp), batch));
        Assert.Equal("batch.authorized", Code(profile with { Mode = BatchAuthorityMode.InAppWithinLimits }, Consent(batch, profile with { Mode = BatchAuthorityMode.InAppWithinLimits }, source: BatchConsentSource.InApp), batch));
        Assert.Equal("batch.consent-invalid", Code(profile, consent with { TotalMaximumMinorUnits = 1 }, batch));
        Assert.Equal("batch.consent-profile-changed", Code(profile with { DisplayRate = 1.31m }, consent, batch));
        Assert.Equal("batch.consent-expired", Code(profile, consent, batch, at: consent.ExpiresUtc));
        var edited = batch.WithItem(batch.Items[0] with { SourceSha256 = Sha("edited") });
        Assert.Equal("batch.consent-files-changed", Code(profile, consent, edited));
        var other = await w.CreateAsync(["x.txt"], Settings with { VoiceStableId = "cmn-CN-Chirp3-HD-Kore" });
        Assert.Equal("batch.consent-profile-changed", Code(profile, consent, other));
        Assert.Equal("batch.ceiling-batch", Code(profile, Consent(batch, profile, perFile: 1000), batch));
        Assert.Equal("batch.ceiling-rolling", Code(profile, consent, batch, others: profile.RollingCeilingMinorUnits - 199));
        Assert.Equal("batch.authorized", Code(profile, consent, batch, others: profile.RollingCeilingMinorUnits - 200));
        // A completed file no longer needs approval, so finishing the rest after a change of files is still checked.
        var finishedFirst = batch.WithItem(batch.Items[0] with { State = BatchItemState.Completed, SourceSha256 = Sha("edited") });
        Assert.Equal("batch.authorized", Code(profile, consent, finishedFirst));
    }

    [Fact]
    public void LedgerCountsHoldsInsideTheWindowAndNeverUnderCountsUnfinishedFiles()
    {
        var item = new BatchItem(Guid.NewGuid(), 1, "a.txt", "s", 1, 1, "a.mp3", BatchItemState.Completed, BatchExclusion.None, Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), null, 1, 0, 40, null, 0, null, Now.AddDays(-1), null, 0);
        var running = item with { ItemId = Guid.NewGuid(), State = BatchItemState.Uncertain, ReservedMinorUnits = 10 };
        var old = item with { ItemId = Guid.NewGuid(), StartedUtc = Now.AddDays(-40) };
        var never = item with { ItemId = Guid.NewGuid(), JobId = null, ReservedMinorUnits = 999 };
        var batch = new NarrationBatch(1, Guid.NewGuid(), "b", 1, BatchQueueState.Paused, Settings, Settings.Sha256(), "/tmp", "USD", 2,
            [item, running, old, never], Now);
        var consent = new BatchConsent(1, Guid.NewGuid(), batch.BatchId, BatchConsentSource.InApp, "p", "s", Account, Project, "USD", 2,
            [new(running.ItemId, "s", Guid.NewGuid(), Guid.NewGuid(), "plan", 75)], 75, Now, Now.AddDays(1), "h");
        Assert.Equal(40 + 75, BatchSpendLedger.Committed([batch], [consent], Now, 30));
        Assert.Equal(0, BatchSpendLedger.Committed([batch], [consent], Now, 30, excludeBatchId: batch.BatchId));
    }

    [Fact]
    public async Task InstalledStoreNeedsPinsRejectsAnotherProfileRootAndAProfileCopyPosingAsReviewed()
    {
        using var w = new Workspace();
        string install = Path.Combine(w.Root, "install"), app = Path.Combine(install, "app"), authority = Path.Combine(install, "batch-authority");
        string profileRoot = Path.Combine(w.Root, "profile");
        Directory.CreateDirectory(Path.Combine(authority, "consents"));
        Directory.CreateDirectory(app);
        var consents = new Dictionary<string, string>(StringComparer.Ordinal);
        async Task<string> Install(string relative, object value)
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, InstalledBatchAuthorityStore.Json);
            await File.WriteAllBytesAsync(Path.Combine(authority, relative), bytes, Token);
            return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        }
        async Task Pin(string profileSha) =>
            await File.WriteAllTextAsync(Path.Combine(authority, "pins.json"), JsonSerializer.Serialize(new BatchAuthorityPins(1, profileSha, consents)), Token);
        var store = new InstalledBatchAuthorityStore(app, profileRoot);
        Assert.Null(await store.LoadProfileAsync(Token));
        var profile = Profile(profileRoot);
        await Pin(await Install("profile.json", Profile(w.Root)));
        Assert.Null(await store.LoadProfileAsync(Token)); // bound to a different profile folder
        string pinned = await Install("profile.json", profile);
        Assert.Null(await store.LoadProfileAsync(Token)); // installed but not pinned
        await Pin(pinned);
        Assert.Equal(profile.Sha256(), (await store.LoadProfileAsync(Token))!.Sha256());
        await File.WriteAllTextAsync(Path.Combine(authority, "profile.json"),
            (await File.ReadAllTextAsync(Path.Combine(authority, "profile.json"), Token)).Replace("500", "9999", StringComparison.Ordinal), Token);
        Assert.Null(await store.LoadProfileAsync(Token)); // edited after installation
        await Pin(await Install("profile.json", profile));

        var batch = await w.CreateAsync(["a.txt"]);
        var reviewed = Consent(batch, profile);
        string spoof = Path.Combine(profileRoot, "batches", "consents");
        Directory.CreateDirectory(spoof);
        await File.WriteAllTextAsync(Path.Combine(spoof, batch.BatchId.ToString("N") + ".json"), JsonSerializer.Serialize(reviewed, InstalledBatchAuthorityStore.Json), Token);
        Assert.Null(await store.LoadConsentAsync(batch.BatchId, Token)); // a user-profile copy cannot claim to be reviewed
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveInAppConsentAsync(reviewed, Token));
        var inApp = Consent(batch, profile, source: BatchConsentSource.InApp);
        await store.SaveInAppConsentAsync(inApp, Token);
        Assert.Equal(inApp.ConsentSha256, (await store.LoadConsentAsync(batch.BatchId, Token))!.ConsentSha256);
        string consentSha = await Install(Path.Combine("consents", batch.BatchId.ToString("N") + ".json"), reviewed);
        Assert.Equal(BatchConsentSource.InApp, (await store.LoadConsentAsync(batch.BatchId, Token))!.Source); // unpinned reviewed file ignored
        consents[batch.BatchId.ToString("N")] = consentSha;
        await Pin(await Install("profile.json", profile));
        Assert.Equal(BatchConsentSource.ReviewedInstall, (await store.LoadConsentAsync(batch.BatchId, Token))!.Source); // reviewed wins
    }

    [Fact]
    public async Task ExportedRequestInstalledLikeTheScriptIsAcceptedEndToEnd()
    {
        using var w = new Workspace();
        string install = Path.Combine(w.Root, "install"), authority = Path.Combine(install, "batch-authority"), profileRoot = Path.Combine(w.Root, "profile");
        Directory.CreateDirectory(Path.Combine(authority, "consents")); Directory.CreateDirectory(Path.Combine(install, "app"));
        // A profile written by hand in the documented shape (as a reviewer would), not by the app's serializer.
        string profileJson = $$"""
            { "SchemaVersion": 2, "Mode": "Reviewed", "ProfileRootSha256": "{{InstalledRecoveryAdmissionStore.ProfileHash(profileRoot)}}",
              "AccountStableId": "{{Account}}", "ProjectStableId": "{{Project}}", "AllowedVoiceStableIds": [], "AllowedEngines": ["long-audio", "complete-article"],
              "FailurePolicy": "SkipAndContinue", "Currency": "USD", "Scale": 2, "PerBatchCeilingMinorUnits": 1924, "RollingCeilingMinorUnits": 7693,
              "RollingWindowDays": 30, "MaximumFilesPerBatch": 500, "ConsentLifetime": "2.00:00:00", "ExpiresUtc": "2027-02-01T00:00:00+00:00",
              "DisplayCurrency": "SGD", "DisplayRate": 1.30, "EstimateMinorUnitsPerMillionCharacters": 3000 }
            """;
        byte[] profileBytes = System.Text.Encoding.UTF8.GetBytes(profileJson);
        await File.WriteAllBytesAsync(Path.Combine(authority, "profile.json"), profileBytes, Token);
        var store = new InstalledBatchAuthorityStore(Path.Combine(install, "app"), profileRoot);
        async Task Pin(Dictionary<string, string> consents) => await File.WriteAllTextAsync(Path.Combine(authority, "pins.json"),
            JsonSerializer.Serialize(new BatchAuthorityPins(1, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(profileBytes)), consents)), Token);
        await Pin(new(StringComparer.Ordinal));
        var profile = await store.LoadProfileAsync(Token);
        Assert.NotNull(profile);
        Assert.Equal(TimeSpan.FromDays(2), profile.ConsentLifetime);

        var library = new MemoryLibrary();
        var docA = await library.CreateAsync("A", "第一章。這是一段測試文字。\n第二段。", Token);
        var docB = await library.CreateAsync("B", "Second file. Two sentences here.", Token);
        var batch = await w.CreateAsync(["01 A.txt", "02 B.txt"]);
        batch = await w.Store.SaveAsync(batch with
        {
            Items = [batch.Items[0] with { DocumentId = docA.Id, RevisionId = docA.CurrentRevisionId }, batch.Items[1] with { DocumentId = docB.Id, RevisionId = docB.CurrentRevisionId }],
        }, Token);
        var request = await new BatchAdmissionRequestBuilder(library, TimeProvider.System).BuildAsync(batch, profile, Account, Project, cancellationToken: Token);
        Assert.All(request.Items, x => Assert.Equal(x.PlanSha256, x.Plan!.PlanSha256));
        Assert.Equal(2, request.ProposedConsent.Items.Count);
        string path = await BatchAdmissionRequestBuilder.WriteAsync(request, w.Output, Token);
        Assert.NotEqual(path, await BatchAdmissionRequestBuilder.WriteAsync(request, w.Output, Token), StringComparer.Ordinal); // never overwrites
        string written = await File.ReadAllTextAsync(path, Token);
        Assert.DoesNotContain("這是一段", written, StringComparison.Ordinal); // hashes and structure only, never article text

        // What Install-BatchAuthority.ps1 does: copy the ProposedConsent's raw JSON byte-for-byte and pin its hash.
        using var parsed = JsonDocument.Parse(await File.ReadAllBytesAsync(path, Token));
        byte[] consentBytes = System.Text.Encoding.UTF8.GetBytes(parsed.RootElement.GetProperty("ProposedConsent").GetRawText());
        await File.WriteAllBytesAsync(Path.Combine(authority, "consents", batch.BatchId.ToString("N") + ".json"), consentBytes, Token);
        await Pin(new(StringComparer.Ordinal) { [batch.BatchId.ToString("N")] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(consentBytes)) });
        var gate = new BatchAuthorityGate(store, w.Store, TimeProvider.System);
        var decision = await gate.CheckAsync(batch, Account, Project, Token);
        Assert.True(decision.Allowed, decision.ReasonCode);
        Assert.False((await gate.CheckAsync(batch, Account, "another-project", Token)).Allowed);
    }

    [Fact]
    public async Task InAppApprovalIsSavedOnlyInsideTheCeilings()
    {
        using var w = new Workspace();
        var batch = await w.CreateAsync(["a.txt", "b.txt"]);
        var authority = new MemoryAuthority { Profile = Profile(w.Root, BatchAuthorityMode.InAppWithinLimits) };
        var gate = new BatchAuthorityGate(authority, w.Store, new FixedClock(Now));
        var items = batch.Included.Select(x => new BatchConsentItem(x.ItemId, x.SourceSha256, x.DocumentId!.Value, x.RevisionId!.Value, "plan", 1500)).ToArray();
        Assert.Equal("batch.ceiling-batch", (await gate.ApproveInAppAsync(batch, Account, Project, RequestFor(batch, authority.Profile, items), Token)).ReasonCode);
        Assert.Null(authority.Saved);
        Assert.True((await gate.ApproveInAppAsync(batch, Account, Project, RequestFor(batch, authority.Profile, items.Select(x => x with { MaximumMinorUnits = 500 }).ToArray()), Token)).Allowed);
        Assert.NotNull(authority.Saved);
        Assert.True((await gate.CheckAsync(batch, Account, Project, Token)).Allowed);

        authority.Profile = authority.Profile with { Mode = BatchAuthorityMode.Reviewed };
        Assert.Equal("batch.consent-needs-review", (await gate.ApproveInAppAsync(batch, Account, Project, RequestFor(batch, authority.Profile, items), Token)).ReasonCode);
    }

    [Fact]
    public async Task NarratorChecksTheServiceReviewBeforeReservingAndContinuesTheSameJob()
    {
        using var w = new Workspace();
        var batch = await w.CreateAsync(["a.txt", "b.txt", "c.txt", "d.txt"]);
        var profile = Profile(w.Root);
        var authority = new MemoryAuthority { Profile = profile, Consent = Consent(batch, profile, perFile: 300) };
        var workflow = new FakeWorkflow(w.Output);
        workflow.Plans[batch.Items[1].DocumentId!.Value] = "different-plan";
        workflow.Maxima[batch.Items[2].DocumentId!.Value] = 301;
        var narrator = new CompleteArticleBatchNarrator(new BatchAuthorityGate(authority, w.Store, new FixedClock(Now)), workflow, () => (Account, Project));
        foreach (var item in batch.Items) workflow.Plans.TryAdd(item.DocumentId!.Value, "plan-" + item.Order);

        var result = await new BatchQueueRunner(w.Store, narrator, new FixedClock(Now)).RunAsync(batch.BatchId, Settings.Sha256(), cancellationToken: Token);
        Assert.Equal(BatchItemState.Completed, result.Items[0].State);
        Assert.Equal("batch.plan-changed-nothing-sent", result.Items[1].ReasonCode);
        Assert.Equal("batch.file-over-approved-maximum-nothing-sent", result.Items[2].ReasonCode);
        Assert.Equal(BatchItemState.Failed, result.Items[2].State);
        Assert.Equal(BatchItemState.Completed, result.Items[3].State);
        Assert.Equal(2, workflow.Started); // nothing was reserved for the refused files
        Assert.True(await narrator.VerifyOutputAsync(result.Items[0], Token));

        // A stopped, uncertain job is continued through the service's review; it is never replaced by a new job.
        workflow.StopNext = "article.provider-outcome-uncertain-no-retry";
        var again = await w.CreateAsync(["e.txt"]);
        authority.Consent = Consent(again, profile, perFile: 300);
        workflow.Plans[again.Items[0].DocumentId!.Value] = "plan-1";
        var first = await new BatchQueueRunner(w.Store, narrator, new FixedClock(Now)).RunAsync(again.BatchId, Settings.Sha256(), cancellationToken: Token);
        Assert.Equal(BatchItemState.Uncertain, first.Items[0].State);
        var job = first.Items[0].JobId;
        var reopened = await w.Store.SaveAsync(BatchQueueRunner.CheckAgain(first, first.Items[0].ItemId), Token);
        var second = await new BatchQueueRunner(w.Store, narrator, new FixedClock(Now)).RunAsync(reopened.BatchId, Settings.Sha256(), cancellationToken: Token);
        Assert.Equal(BatchItemState.Completed, second.Items[0].State);
        Assert.Equal(job, second.Items[0].JobId);
        Assert.Equal(4, workflow.Started); // 2 + first try + check again; Start on the stopped job returned the same job
        Assert.Single(workflow.Jobs, x => x.Value.DocumentId == again.Items[0].DocumentId);
    }

    [Fact]
    public async Task NarratorRefusesUnapprovedFilesAndOtherEngines()
    {
        using var w = new Workspace();
        var batch = await w.CreateAsync(["a.txt"]);
        var profile = Profile(w.Root);
        var authority = new MemoryAuthority { Profile = profile };
        var narrator = new CompleteArticleBatchNarrator(new BatchAuthorityGate(authority, w.Store, new FixedClock(Now)), new FakeWorkflow(w.Output), () => (Account, Project));
        var check = await narrator.CheckAuthorityAsync(batch, Token);
        Assert.False(check.Authorized);
        Assert.Contains("reviewed", check.Explanation, StringComparison.Ordinal);
        Assert.False((await narrator.CheckAuthorityAsync(batch with { Settings = Settings with { Engine = BatchEngines.LongAudio } }, Token)).Authorized);
        var outcome = await narrator.NarrateAsync(batch, batch.Items[0], _ => Task.CompletedTask, null, Token);
        Assert.Equal(BatchOutcomeKind.NotAuthorized, outcome.Kind);
    }

    [Fact]
    public void JobOutcomesNeverTurnAPossibleSubmissionIntoAFailure()
    {
        var job = new ChirpWholeArticleJob(Guid.NewGuid(), Guid.NewGuid(), "p", "pol", "rev", 250, Now, Now, ChirpWholeArticleState.Stopped, 3, "article.storage-failed-reconcile");
        Assert.Equal(BatchOutcomeKind.Uncertain, BatchArticleOutcomes.FromJob(job, 300, false).Kind);
        Assert.Equal(300, BatchArticleOutcomes.FromJob(job, 300, false).ReservedMinorUnits);
        Assert.Equal(BatchOutcomeKind.Failed, BatchArticleOutcomes.FromJob(job with { ReasonCode = "article.provider-rejected-no-retry" }, 300, false).Kind);
        Assert.Equal(BatchOutcomeKind.Paused, BatchArticleOutcomes.FromJob(job with { ReasonCode = "article.cancelled-reconcile" }, 300, true).Kind);
        Assert.Equal(BatchOutcomeKind.Uncertain, BatchArticleOutcomes.FromJob(job with { ReasonCode = "article.cancelled-reconcile" }, 300, false).Kind);
        Assert.Equal("article.stopped-reconcile", BatchArticleOutcomes.FromJob(job with { ReasonCode = "C:\\Users\\name\\secret path" }, 300, false).ReasonCode);
        Assert.Equal(BatchOutcomeKind.Uncertain, BatchArticleOutcomes.FromJob(job with { State = ChirpWholeArticleState.Complete }, 300, false).Kind); // no output receipt
    }

    [Fact]
    public void MoneyShowsSingaporeDollarsFirstAndLabelsTheConversion()
    {
        Assert.Equal("about S$13.00 (US$10.00)", BatchMoney.Format(1000, "USD", 2, "SGD", 1.30m));
        Assert.Equal("S$4.20", BatchMoney.Format(420, "SGD", 2, "SGD", 1m));
        Assert.Equal(1924, BatchMoney.ToMinor(19.231m, 2));
        var profile = Profile(Path.GetTempPath());
        Assert.Equal(0, profile.EstimateMinorUnits(0));
        Assert.Equal(3300, profile.EstimateMinorUnits(1_000_000)); // US$30 (3000 cents) per million characters, plus a 10% margin
    }

    /// <summary>A prepared request carrying exactly these approval items (as the builder would produce).</summary>
    private static BatchAdmissionRequest RequestFor(NarrationBatch batch, BatchAuthorityProfile profile, IReadOnlyList<BatchConsentItem> items)
    {
        var consent = BatchConsent.Create(Guid.NewGuid(), batch, BatchConsentSource.ReviewedInstall, profile, Account, Project, items, Now);
        var rows = items.Select(x => new BatchAdmissionRequestItem(x.ItemId, 1, x.SourceSha256, 10, x.DocumentId, x.RevisionId, x.Engine, x.PlanSha256, 1,
            x.MaximumMinorUnits, true, false, null, null, x.LongAudio)).ToArray();
        return new(BatchAdmissionRequest.CurrentFormat, batch.BatchId, profile.Sha256(), batch.SettingsSha256, Account, Project, "cmn-CN", "voice",
            profile.Currency, profile.Scale, rows, consent.TotalMaximumMinorUnits, consent);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class MemoryAuthority : IBatchAuthorityStore
    {
        public BatchAuthorityProfile? Profile { get; set; }
        public BatchConsent? Consent { get; set; }
        public BatchConsent? Saved { get; private set; }
        public Task<BatchAuthorityProfile?> LoadProfileAsync(CancellationToken cancellationToken = default) => Task.FromResult(Profile);
        public Task<BatchConsent?> LoadConsentAsync(Guid batchId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Saved is { } s && s.BatchId == batchId ? s : Consent is { } c && c.BatchId == batchId ? c : null);
        public Task SaveInAppConsentAsync(BatchConsent consent, CancellationToken cancellationToken = default) { Saved = consent; return Task.CompletedTask; }
    }

    /// <summary>Fake complete-article workflow: one job per document, like the real service; writes a synthetic MP3.</summary>
    private sealed class FakeWorkflow(string output) : IBatchArticleWorkflow
    {
        public Dictionary<Guid, string> Plans { get; } = [];
        public Dictionary<Guid, long> Maxima { get; } = [];
        public Dictionary<Guid, ChirpWholeArticleJob> Jobs { get; } = [];
        public int Started { get; private set; }
        public string? StopNext { get; set; }
        private readonly Dictionary<Guid, Guid> _previews = [];

        public Task<ChirpWholeArticleReview> PrepareAsync(Guid documentId, CancellationToken cancellationToken)
        {
            var id = Guid.NewGuid(); _previews[id] = documentId;
            return Task.FromResult(new ChirpWholeArticleReview(id, "review", documentId, Plans[documentId], "voice", "USD", 2, 0, 3, 3,
                Maxima.GetValueOrDefault(documentId, 250), 0, Now.AddHours(6), 0, Now.AddMinutes(10)));
        }

        public Task<ChirpWholeArticleJob> StartAsync(ChirpWholeArticleConfirmation confirmation, CancellationToken cancellationToken)
        {
            Started++;
            Guid document = _previews[confirmation.PreviewId];
            var existing = Jobs.Values.FirstOrDefault(x => x.DocumentId == document);
            var job = existing is not null ? existing with { State = ChirpWholeArticleState.Ready }
                : new ChirpWholeArticleJob(Guid.NewGuid(), document, Plans[document], "policy", "review", confirmation.MaximumAdditionalMinorUnits, Now.AddHours(6), Now, ChirpWholeArticleState.Ready, null, "article.ready");
            Jobs[job.JobId] = job;
            return Task.FromResult(job);
        }

        public Task<ChirpWholeArticleJob> ReadAsync(Guid jobId, CancellationToken cancellationToken) => Task.FromResult(Jobs[jobId]);

        public async Task<ChirpWholeArticleJob> RunAsync(Guid jobId, NarrationBatch batch, BatchItem item, string destination,
            IProgress<BatchItemProgress>? progress, CancellationToken cancellationToken)
        {
            if (StopNext is { } reason) { StopNext = null; return Jobs[jobId] = Jobs[jobId] with { State = ChirpWholeArticleState.Stopped, ReasonCode = reason }; }
            Assert.StartsWith(output, destination, StringComparison.Ordinal);
            await File.WriteAllTextAsync(destination, "synthetic mp3", cancellationToken);
            string sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(destination, cancellationToken)));
            return Jobs[jobId] = Jobs[jobId] with { State = ChirpWholeArticleState.Complete, Output = new(destination, new FileInfo(destination).Length, sha) };
        }
    }
}
