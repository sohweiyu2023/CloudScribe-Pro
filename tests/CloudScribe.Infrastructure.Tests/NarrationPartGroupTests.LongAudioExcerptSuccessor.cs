using System.Text.Json;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    private static readonly JsonSerializerOptions ExcerptPlanJson = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
    [Theory]
    [InlineData("")]
    [InlineData("PredecessorArchived")]
    [InlineData("ScopeWritten")]
    [InlineData("MarkerWritten")]
    [InlineData("reject-uncertain")]
    [InlineData("reject-excerpt-changed")]
    [InlineData("completed")]
    [InlineData("completed-ScopeWritten")]
    [InlineData("reject-completed-output-changed")]
    [InlineData("reject-completed-receipt-missing")]
    [InlineData("reject-completed-source-changed")]
    [InlineData("reject-completed-generation-changed")]
    public Task LongAudioExcerptSuccessorRetainsAllFailuresAndRequiresAnExactOriginalSubstring(string stopAt) => RunLongAudioSourceSuccessorAsync(stopAt);

    [Theory]
    [InlineData("v4-completed")]
    [InlineData("v4-completed-ScopeWritten")]
    [InlineData("v4-completed-LedgerWritten")]
    [InlineData("reject-v4-uncertain")]
    [InlineData("reject-v4-completed-source")]
    [InlineData("reject-v4-completed-cap")]
    [InlineData("reject-v4-completed-replay")]
    [InlineData("reject-v4-completed-history")]
    public Task LongAudioTwoArticleSuccessorPreservesHistoryAndRequiresExactWholeSources(string stopAt) => RunLongAudioSourceSuccessorAsync(stopAt);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051:Method is too long", Justification = "One synthetic fixture verifies retained outcomes, exact source mapping and interrupted maintenance using the actual script.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms", Justification = "Matches GCS transport MD5 only; independent SHA256 binds saved media and approval evidence.")]
    private static async Task RunLongAudioSourceSuccessorAsync(string stopAt)
    {
        bool wholeArticles = stopAt.Contains("v4-", StringComparison.Ordinal);
        using var f = new Fixture(); await f.InitializeAsync().ConfigureAwait(false); var e = await ChirpEvidenceAsync(f).ConfigureAwait(false);
        const string paragraph = "\n\nSynthetic complete sentence. Another complete sentence.";
        const string prefix = "Synthetic opening.";
        var original = await f.Library.CreateAsync("Synthetic source", prefix + paragraph, TestContext.Current.CancellationToken).ConfigureAwait(false);
        var p = await PrepareLongInstalledAsync(f, e, longDocument: original).ConfigureAwait(false);
        string install = Path.GetDirectoryName(p.App)!;
        string HashFile(string path) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
        var originalScope = JsonSerializer.Deserialize<GoogleLongAudioBudgetScope>(File.ReadAllBytes(p.ScopePath))!;
        var originalJob = Assert.Single(originalScope.DocumentJobs!);
        Guid wavId = Guid.NewGuid(); string wavUri = "gs://" + originalScope.OutputBucket + "/comparison/" + wavId.ToString("N") + ".wav";
        var wav = GoogleLongAudioRequest.Create(wavId, originalScope.ProjectNumber, "global", originalScope.CredentialReferenceId, wavUri,
            original.Text, originalScope.LanguageCode, originalScope.VoiceName, "LINEAR16");
        var previousScope = originalScope with
        {
            AudioEncoding = "LINEAR16",
            SourceApprovalSha256 = new string('e', 64),
            ApprovedRequestCeilingsUsdCents = new Dictionary<string, long>(StringComparer.Ordinal) { [p.Request.BindingSha256] = 1, [wav.BindingSha256] = 1 },
            DocumentJobs = [originalJob with { AttemptId = wavId, OutputGcsUri = wavUri }]
        };
        File.WriteAllBytes(p.ScopePath, JsonSerializer.SerializeToUtf8Bytes(previousScope)); string previousSha = HashFile(p.ScopePath);
        GoogleLongAudioBudgetClaim[] claims = [new(p.Request.AttemptId, p.Request.BindingSha256, 1), new(wavId, wav.BindingSha256, 1)];
        File.WriteAllBytes(p.LedgerPath, JsonSerializer.SerializeToUtf8Bytes(new GoogleLongAudioBudgetLedger(1, previousSha, claims)));
        string markerPath = Path.Combine(install, "cloudscribe-install.json");
        var marker = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllBytes(markerPath))!;
        marker["Profile"] = JsonSerializer.SerializeToElement(f.Root); marker["GoogleLongAudioScopeSha256"] = JsonSerializer.SerializeToElement(previousSha);
        File.WriteAllBytes(markerPath, JsonSerializer.SerializeToUtf8Bytes(marker));
        var parent = JsonSerializer.Deserialize<ChirpArticleAdmissionPolicy>(File.ReadAllBytes(p.ParentPath))!;
        WriteSyntheticLongProvisioningProof(install, parent, previousScope.ParentPolicySha256, previousSha, p.LedgerPath, e.Clock.GetUtcNow());
        string operations = Path.Combine(Path.GetDirectoryName(p.LedgerPath)!, "operations"); Directory.CreateDirectory(operations);
        bool completed = stopAt.Contains("completed", StringComparison.Ordinal);
        string output = Path.Combine(Path.GetDirectoryName(p.LedgerPath)!, "output"); Directory.CreateDirectory(output);
        string syntheticAudio = Path.Combine(output, wavId.ToString("N") + ".wav");
        if (completed)
        {
            File.WriteAllText(syntheticAudio, "Synthetic verified bytes; this fixture does not claim codec validation.");
            var identity = new
            {
                BindingSha256 = wav.BindingSha256,
                Bucket = previousScope.OutputBucket,
                ObjectName = "comparison/" + wavId.ToString("N") + ".wav",
                Generation = "123",
                Bytes = new FileInfo(syntheticAudio).Length,
                Md5Base64 = Convert.ToBase64String(System.Security.Cryptography.MD5.HashData(File.ReadAllBytes(syntheticAudio)))
            };
            File.WriteAllBytes(Path.Combine(output, wavId.ToString("N") + ".object.json"), JsonSerializer.SerializeToUtf8Bytes(identity));
            var receiptSource = string.Equals(stopAt, "reject-completed-source-changed", StringComparison.Ordinal)
                ? await f.Library.CreateAsync("Another saved source", "A different source.", TestContext.Current.CancellationToken).ConfigureAwait(false) : original;
            if (!string.Equals(stopAt, "reject-completed-receipt-missing", StringComparison.Ordinal))
                File.WriteAllBytes(Path.Combine(output, wavId.ToString("N") + ".verified.json"), JsonSerializer.SerializeToUtf8Bytes(new
                {
                    Version = 1,
                    DocumentId = receiptSource.Id,
                    RevisionId = receiptSource.CurrentRevisionId,
                    SourceSha256 = Hash(receiptSource.Text),
                    RequestBindingSha256 = wav.BindingSha256,
                    Artifact = new
                    {
                        AudioSha256 = HashFile(syntheticAudio),
                        LocalPath = syntheticAudio,
                        Bytes = new FileInfo(syntheticAudio).Length,
                        Identity = identity
                    },
                }));
            if (string.Equals(stopAt, "reject-completed-generation-changed", StringComparison.Ordinal))
                File.WriteAllBytes(Path.Combine(output, wavId.ToString("N") + ".object.json"), JsonSerializer.SerializeToUtf8Bytes(identity with { Generation = "0123" }));
            if (string.Equals(stopAt, "reject-completed-output-changed", StringComparison.Ordinal)) File.AppendAllText(syntheticAudio, "changed");
        }
        foreach (var claim in claims)
        {
            bool first = claim.AttemptId == p.Request.AttemptId;
            File.WriteAllBytes(Path.Combine(operations, claim.AttemptId.ToString("N") + ".json"), JsonSerializer.SerializeToUtf8Bytes(new
            {
                claim.AttemptId,
                BindingSha256 = claim.RequestBindingSha256,
                State = stopAt.Contains("uncertain", StringComparison.Ordinal) && !first ? 0 : completed && !first ? 2 : 3,
                OutputGcsUri = first ? p.Request.OutputGcsUri : wavUri,
                OperationName = first ? null : "projects/" + previousScope.ProjectNumber + "/locations/global/operations/synthetic",
                HttpStatus = first ? 400 : 200,
                RpcCode = first || completed ? (int?)null : 3,
                OperationParseFailed = false,
                Observation = new { ReadbackVerified = true, Truncated = false, DetailsOmitted = !first },
            }));
        }
        var excerpt = await f.Library.CreateAsync("Synthetic comparison", paragraph, TestContext.Current.CancellationToken).ConfigureAwait(false);
        Guid nextId = Guid.NewGuid(); string nextUri = "gs://" + previousScope.OutputBucket + "/comparison/" + nextId.ToString("N") + ".wav";
        var nextRequest = GoogleLongAudioRequest.Create(nextId, previousScope.ProjectNumber, "global", previousScope.CredentialReferenceId,
            nextUri, paragraph, previousScope.LanguageCode, previousScope.VoiceName, "LINEAR16");
        var nextScope = previousScope with
        {
            SourceApprovalSha256 = new string('f', 64),
            ApprovedRequestCeilingsUsdCents = new Dictionary<string, long>(previousScope.ApprovedRequestCeilingsUsdCents, StringComparer.Ordinal) { [nextRequest.BindingSha256] = 1 },
            DocumentJobs = [new(excerpt.Id, excerpt.CurrentRevisionId!.Value, Hash(paragraph), nextId, nextUri, "text", 4096, 24000, 1)]
        };
        var secondOriginal = wholeArticles ? await f.Library.CreateAsync("Second complete original", "Another original. Its complete second sentence.", TestContext.Current.CancellationToken).ConfigureAwait(false) : null;
        var secondCopy = wholeArticles ? await f.Library.CreateAsync("Second comparison copy", secondOriginal!.Text, TestContext.Current.CancellationToken).ConfigureAwait(false) : null;
        Guid secondId = Guid.NewGuid();
        GoogleLongAudioRequest? secondRequest = wholeArticles ? GoogleLongAudioRequest.Create(secondId, previousScope.ProjectNumber, "global", previousScope.CredentialReferenceId,
            "gs://" + previousScope.OutputBucket + "/comparison/" + secondId.ToString("N") + ".wav", secondCopy!.Text, previousScope.LanguageCode, previousScope.VoiceName, "LINEAR16") : null;
        if (wholeArticles)
        {
            var amounts = new Dictionary<string, long>(nextScope.ApprovedRequestCeilingsUsdCents, StringComparer.Ordinal) { [secondRequest!.BindingSha256] = 1 };
            if (stopAt.EndsWith("-cap", StringComparison.Ordinal)) amounts[secondRequest.BindingSha256] = previousScope.TotalGoogleCapUsdCents;
            if (stopAt.EndsWith("-history", StringComparison.Ordinal)) amounts.Remove(p.Request.BindingSha256);
            nextScope = nextScope with
            {
                ApprovedRequestCeilingsUsdCents = amounts,
                DocumentJobs = [nextScope.DocumentJobs![0], new(secondCopy!.Id, secondCopy.CurrentRevisionId!.Value, Hash(secondCopy.Text),
                    stopAt.EndsWith("-replay", StringComparison.Ordinal) ? wavId : secondId, secondRequest.OutputGcsUri, "text", 4096, 24000, 1)],
                AdaptiveRecoveryRootBindings = [nextRequest.BindingSha256, secondRequest.BindingSha256]
            };
        }
        string reviewed = Path.Combine(f.Root, "reviewed-excerpt-scope.json"); File.WriteAllBytes(reviewed, JsonSerializer.SerializeToUtf8Bytes(nextScope));
        var pins = claims.Select(c => new
        {
            c.AttemptId,
            Sha256 = HashFile(Path.Combine(operations, c.AttemptId.ToString("N") + ".json")),
            CompletedReceiptSha256 = completed && c.AttemptId == wavId && File.Exists(Path.Combine(output, wavId.ToString("N") + ".verified.json"))
                ? HashFile(Path.Combine(output, wavId.ToString("N") + ".verified.json")) : null,
            CompletedObjectSha256 = completed && c.AttemptId == wavId ? HashFile(Path.Combine(output, wavId.ToString("N") + ".object.json")) : null
        }).ToArray();
        // A separately pinned source revision may be larger than the successful current comparison.
        var sourceParent = completed ? await f.Library.CreateAsync("Separate reviewed original", wholeArticles ? paragraph : original.Text, TestContext.Current.CancellationToken).ConfigureAwait(false) : original;
        var plan = new
        {
            Version = completed ? 3 : 2,
            nextScope.SourceApprovalSha256,
            PreviousScopeSha256 = previousSha,
            PreviousLedgerSha256 = HashFile(p.LedgerPath),
            PreviousMarkerSha256 = HashFile(markerPath),
            PreviousProvisioningPlanSha256 = HashFile(Path.Combine(install, "long-audio-provisioning/plan.json")),
            PreviousProvisioningReceiptSha256 = HashFile(Path.Combine(install, "long-audio-provisioning/receipt.json")),
            PreviousOperationSha256 = pins.Single(x => x.AttemptId == wavId).Sha256,
            PreviousAttemptId = wavId,
            NewScopePath = reviewed,
            NewScopeSha256 = HashFile(reviewed),
            RetainedOperationPins = pins,
            SourceExcerpt = new
            {
                ParentDocumentId = sourceParent.Id,
                ParentRevisionId = sourceParent.CurrentRevisionId!.Value,
                ParentSourceSha256 = Hash(sourceParent.Text),
                StartUtf16 = string.Equals(stopAt, "reject-excerpt-changed", StringComparison.Ordinal) ? prefix.Length + 1 : prefix.Length,
                EndUtf16Exclusive = original.Text.Length,
                ExcerptSha256 = Hash(paragraph)
            }
        };
        var planFields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.SerializeToUtf8Bytes(plan, ExcerptPlanJson))!;
        if (wholeArticles)
        {
            planFields["Version"] = JsonSerializer.SerializeToElement(4); planFields.Remove("SourceExcerpt");
            planFields["SourceRevisions"] = JsonSerializer.SerializeToElement(new[]
            {
                new { ParentDocumentId = sourceParent.Id, ParentRevisionId = sourceParent.CurrentRevisionId!.Value, ParentSourceSha256 = Hash(sourceParent.Text), TargetDocumentId = excerpt.Id, TargetRevisionId = excerpt.CurrentRevisionId!.Value },
                new { ParentDocumentId = secondOriginal!.Id, ParentRevisionId = secondOriginal.CurrentRevisionId!.Value, ParentSourceSha256 = stopAt.EndsWith("-source", StringComparison.Ordinal) ? Hash("Changed") : Hash(secondOriginal.Text), TargetDocumentId = secondCopy!.Id, TargetRevisionId = secondCopy.CurrentRevisionId!.Value }
            });
        }
        string planPath = Path.Combine(f.Root, "reviewed-excerpt-plan.json"); File.WriteAllBytes(planPath, JsonSerializer.SerializeToUtf8Bytes(planFields, ExcerptPlanJson));
        byte[] before = File.ReadAllBytes(p.LedgerPath), parentBefore = File.ReadAllBytes(p.ParentPath);
        var check = await RunLongFormatSuccessorAsync(f, install, planPath, HashFile(planPath), "", true, false, "Update-LongAudioExcerpt.ps1").ConfigureAwait(false);
        Assert.Equal(before, File.ReadAllBytes(p.LedgerPath));
        if (stopAt.StartsWith("reject-", StringComparison.Ordinal)) { Assert.NotEqual(0, check.ExitCode); return; }
        Assert.True(check.ExitCode == 0, check.Error);
        string step = stopAt.Replace("v4-", "", StringComparison.Ordinal);
        string interrupt = completed ? step.Replace("completed-", "", StringComparison.Ordinal).Replace("completed", "", StringComparison.Ordinal) : step;
        var run = await RunLongFormatSuccessorAsync(f, install, planPath, HashFile(planPath), interrupt, false, false, "Update-LongAudioExcerpt.ps1").ConfigureAwait(false);
        if (interrupt.Length > 0)
        {
            Assert.NotEqual(0, run.ExitCode); Assert.Contains("SyntheticInterruption", run.Error, StringComparison.Ordinal);
            Assert.Null(await new InstalledGoogleLongAudioStore(p.App, f.Paths, e.Clock).LoadVerifiedAsync(TestContext.Current.CancellationToken).ConfigureAwait(false));
            run = await RunLongFormatSuccessorAsync(f, install, planPath, HashFile(planPath), "", false, true, "Update-LongAudioExcerpt.ps1").ConfigureAwait(false);
        }
        Assert.True(run.ExitCode == 0, run.Error);
        var loaded = await new InstalledGoogleLongAudioStore(p.App, f.Paths, e.Clock).LoadVerifiedAsync(TestContext.Current.CancellationToken).ConfigureAwait(false); Assert.NotNull(loaded);
        var after = JsonSerializer.Deserialize<GoogleLongAudioBudgetLedger>(File.ReadAllBytes(p.LedgerPath))!;
        Assert.Equal(claims, after.Claims); Assert.Equal(plan.NewScopeSha256, after.ScopeSha256); Assert.Equal(parentBefore, File.ReadAllBytes(p.ParentPath));
        foreach (var pin in pins) Assert.Equal(pin.Sha256, HashFile(Path.Combine(operations, pin.AttemptId.ToString("N") + ".json")));
        var admission = new GoogleLongAudioLedgerAdmission(p.ParentPath, previousScope.ParentPolicySha256, loaded.ScopePath, loaded.ScopeSha256,
            p.LedgerPath, (_, _, _) => throw new InvalidOperationException("No live readiness in offline fixture"), e.Clock);
        var cost = await admission.ReviewAsync(nextRequest, TestContext.Current.CancellationToken).ConfigureAwait(false);
        Assert.Equal(1, cost.AdditionalCommitmentUsdCents); Assert.False(cost.IsAlreadyCommitted);
        if (wholeArticles)
        {
            Assert.Equal(2, loaded.Scope.DocumentJobs!.Count);
            var secondCost = await admission.ReviewAsync(secondRequest!, TestContext.Current.CancellationToken).ConfigureAwait(false);
            Assert.Equal(1, secondCost.AdditionalCommitmentUsdCents); Assert.False(secondCost.IsAlreadyCommitted);
            Assert.Equal(nextScope.AdaptiveRecoveryRootBindings, loaded.Scope.AdaptiveRecoveryRootBindings);
        }
    }
}
