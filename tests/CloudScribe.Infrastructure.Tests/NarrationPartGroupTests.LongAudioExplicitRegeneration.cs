using System.Text;
using System.Text.Json;
using CloudScribe.Application.Generation;
using CloudScribe.Infrastructure.Diagnostics;
using CloudScribe.Infrastructure.Generation;
using Microsoft.Extensions.Logging;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Theory]
    [InlineData("success")]
    [InlineData("split")]
    [InlineData("new-unknown")]
    [InlineData("no-consent")]
    [InlineData("wrong-binding")]
    [InlineData("wrong-guard")]
    [InlineData("budget")]
    [InlineData("stale-identity")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051:Method is too long", Justification = "One synthetic composed lifecycle proves exact duplicate-risk consent, retained original uncertainty, independent accounting, restart and final coverage without provider access.")]
    public async Task ExplicitUnknownRegenerationUsesOneNewIdentityAndPreservesOriginal(string mode)
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await ChirpEvidenceAsync(f);
        var document = await f.Library.CreateAsync("Synthetic explicit regeneration",
            "Alpha first sentence. Alpha second sentence.\n\nBeta second paragraph.", TestContext.Current.CancellationToken);
        var p = await PrepareLongInstalledAsync(f, e, longDocument: document);
        var scope = JsonSerializer.Deserialize<GoogleLongAudioBudgetScope>(File.ReadAllBytes(p.ScopePath))!;
        var root = GoogleLongAudioRequest.Create(p.Request.AttemptId, scope.ProjectNumber, "global", scope.CredentialReferenceId,
            p.Request.OutputGcsUri.Replace(".mp3", ".wav", StringComparison.Ordinal), document.Text, scope.LanguageCode, scope.VoiceName, "LINEAR16");
        var first = GoogleLongAudioRecoveryPlanner.Split(document.Text,
            new(0, document.Text.EnumerateRunes().Count(), Hash(document.Text), null), root, AdaptiveRejected(root), false, 30);
        var unknownPart = mode is "stale-identity" ? first[1] : first[0];
        int initialPosts = mode is "stale-identity" ? 3 : 2;
        Guid newId = mode is "stale-identity" ? first[0].Request.AttemptId : Guid.NewGuid();
        string text = ChirpNarrationPlanner.Slice(document.Text, unknownPart.Range);
        var regenerated = GoogleLongAudioRequest.Create(newId, root.ProjectNumber, root.Location, root.CredentialReferenceId,
            root.OutputGcsUri[..(root.OutputGcsUri.LastIndexOf('/') + 1)] + newId.ToString("N") + ".wav", text, root.LanguageCode, root.VoiceName, "LINEAR16");
        IReadOnlyList<GoogleLongAudioRecoveryPart> smaller = mode is "split"
            ? GoogleLongAudioRecoveryPlanner.Split(document.Text, unknownPart.Range, regenerated, AdaptiveRejected(regenerated), false, 30) : [];
        scope = scope with
        {
            AudioEncoding = "LINEAR16",
            TotalGoogleCapUsdCents = 1000,
            ApprovedRequestCeilingsUsdCents = new Dictionary<string, long>(StringComparer.Ordinal) { [root.BindingSha256] = 1 },
            AdaptiveRecoveryRootBindings = [root.BindingSha256],
            DocumentJobs = [scope.DocumentJobs![0] with { OutputGcsUri = root.OutputGcsUri }]
        };
        string install = Path.GetDirectoryName(p.App)!, markerPath = Path.Combine(install, "cloudscribe-install.json");
        var parent = JsonSerializer.Deserialize<ChirpArticleAdmissionPolicy>(File.ReadAllBytes(p.ParentPath))!;
        Seal([]);
        using var history = new GoogleTrendHistoryLoggerProvider(f.Paths, e.Clock);
        using var logs = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Information).AddProvider(history));
        var transport = new AdaptiveTransport(mode is "stale-identity" ? [root, first[0].Request, first[1].Request]
            : [root, first[0].Request, first[1].Request, regenerated, .. smaller.Select(x => x.Request)],
            mode is "split" ? [root.AttemptId, regenerated.AttemptId] : [root.AttemptId]);
        int posts = 0, outputs = mode is "split" ? smaller.Count + 1 : 2;
        var native = new ExactReplacementNative(null, outputs);
        var fixture = await PrepareLongRuntimeAsync(f, e, p.App, root,
            () => new DeferredHandler(transport, unknownPart.Request, mode is "new-unknown" ? regenerated : null, () => posts++), native, logs);
        var allowance = new GoogleLongAudioOutputAllowance(4096, null, "pcm_s16le", 24000, 1);
        var initial = await fixture.Factory.CreateRecoveryRunnerAsync(root, fixture.Tools, allowance, true, history, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<HttpRequestException>(() => initial.RunAsync(root, new(), token: TestContext.Current.CancellationToken));
        Assert.Equal(initialPosts, posts);
        string operations = Path.Combine(p.Root, "operations"), unknownPath = Path.Combine(operations, unknownPart.Request.AttemptId.ToString("N") + ".json");
        string guardPath = Path.Combine(operations, "observation-pending.guard");
        byte[] unknown = File.ReadAllBytes(unknownPath), guard = File.ReadAllBytes(guardPath);
        var old = JsonSerializer.Deserialize<GoogleLongAudioBudgetLedger>(File.ReadAllBytes(p.LedgerPath))!;
        var originalClaim = Assert.Single(old.Claims, x => x.AttemptId == unknownPart.Request.AttemptId);
        var descriptor = new GoogleLongAudioDeferredRange(unknownPart.Request.AttemptId, root.BindingSha256,
            unknownPart.Range.StartScalar, unknownPart.Range.EndScalarExclusive, Hash(Encoding.UTF8.GetString(unknown)),
            Hash(JsonSerializer.Serialize(originalClaim)), Hash(Encoding.ASCII.GetString(guard)), mode is "no-consent" ? null
                : new(newId, mode is "wrong-binding" ? new string('a', 64) : regenerated.BindingSha256, regenerated.OutputGcsUri, 1));
        scope = scope with { DeferredUnknownRanges = [descriptor] };
        string previousScope = Path.Combine(f.Root, "before-explicit-regeneration.json");
        File.WriteAllBytes(previousScope, JsonSerializer.SerializeToUtf8Bytes(scope with { DeferredUnknownRanges = [descriptor with { ExplicitRegeneration = null }] }));
        if (mode is "budget") scope = scope with { TotalGoogleCapUsdCents = parent.Budget.ProjectCeilingMinorUnits + scope.TaxFxProvisionUsdCents + 2 };
        Seal(old.Claims);
        if (mode is "success" or "stale-identity")
        {
            var fresh = await CheckSyntheticRegenerationFreshnessAsync(f.Root, previousScope, p.ScopePath, p.LedgerPath);
            Assert.Equal(mode is "stale-identity", fresh.Exit != 0);
            if (mode is "stale-identity") Assert.Contains("LongAudioExplicitRegenerationAlreadyClaimed", fresh.Error, StringComparison.Ordinal);
            if (mode is "success")
            {
                string orphan = Path.Combine(p.Root, "output", newId.ToString("N") + ".object.json"); File.WriteAllText(orphan, "{}");
                var existingEvidence = await CheckSyntheticRegenerationFreshnessAsync(f.Root, previousScope, p.ScopePath, p.LedgerPath);
                Assert.NotEqual(0, existingEvidence.Exit); Assert.Contains("LongAudioExplicitRegenerationEvidenceAlreadyExists", existingEvidence.Error, StringComparison.Ordinal);
                File.Delete(orphan);
            }
        }
        if (mode is "wrong-guard") File.WriteAllText(guardPath, Guid.NewGuid().ToString("N"));
        if (mode is "success")
        {
            byte[] unchangedLedger = File.ReadAllBytes(p.LedgerPath); int readiness = 0;
            var mutation = MutationAdmission((_, _, _) => { readiness++; File.WriteAllText(guardPath, Guid.NewGuid().ToString("N")); return ValueTask.CompletedTask; });
            try { await Assert.ThrowsAnyAsync<Exception>(() => mutation.RequireSubmissionAuthorizedAsync(regenerated, TestContext.Current.CancellationToken).AsTask()); }
            finally { File.WriteAllBytes(guardPath, guard); }
            Assert.Equal(1, readiness); Assert.Equal(unchangedLedger, File.ReadAllBytes(p.LedgerPath)); Assert.Equal(initialPosts, posts);
        }
        var runner = await fixture.Factory.CreateRecoveryRunnerAsync(root, fixture.Tools, allowance, true, history, TestContext.Current.CancellationToken);
        if (mode is "wrong-binding" or "wrong-guard" or "budget" or "new-unknown" or "stale-identity")
        {
            await Assert.ThrowsAnyAsync<Exception>(() => runner.RunAsync(root, new(), token: TestContext.Current.CancellationToken));
            Assert.Equal(mode is "new-unknown" ? 3 : initialPosts, posts);
            byte[] retained = File.ReadAllBytes(p.LedgerPath);
            await Assert.ThrowsAnyAsync<Exception>(() => runner.RunAsync(root, new(), token: TestContext.Current.CancellationToken));
            Assert.Equal(retained, File.ReadAllBytes(p.LedgerPath)); Assert.Equal(mode is "new-unknown" ? 3 : initialPosts, posts);
            Assert.Equal(unknown, File.ReadAllBytes(unknownPath)); return;
        }
        var result = await runner.RunAsync(root, new(), token: TestContext.Current.CancellationToken);
        Assert.Equal(mode is not "no-consent", result.CoverageComplete);
        Assert.Equal(mode is "no-consent" ? 1 : outputs, result.Outputs.Count);
        Assert.Equal(mode is "no-consent" ? 3 : mode is "split" ? 4 + smaller.Count : 4, posts);
        Assert.Equal(unknown, File.ReadAllBytes(unknownPath)); Assert.Equal(guard, File.ReadAllBytes(guardPath));
        var after = JsonSerializer.Deserialize<GoogleLongAudioBudgetLedger>(File.ReadAllBytes(p.LedgerPath))!;
        Assert.Equal(originalClaim, Assert.Single(after.Claims, x => x.AttemptId == originalClaim.AttemptId));
        if (mode is not "no-consent")
            Assert.Equal(GoogleLongAudioExplicitRegeneration.PlannerVersion, Assert.Single(after.Claims, x => x.AttemptId == newId).Recovery!.PlannerVersion);
        byte[] ledger = File.ReadAllBytes(p.LedgerPath); int posted = posts;
        var reopened = await runner.RunAsync(root, new(), token: TestContext.Current.CancellationToken);
        Assert.Equal(result.Outputs, reopened.Outputs); Assert.Equal(posted, posts); Assert.Equal(ledger, File.ReadAllBytes(p.LedgerPath));
        var service = new GoogleLongAudioDocumentService(new(p.App, f.Paths, e.Clock), f, new CloudScribe.Infrastructure.Files.DocumentContentStore(f.Paths),
            fixture.Factory, e.Clock, logs.CreateLogger<GoogleLongAudioDocumentService>(), history, native);
        var review = await service.ReviewAsync(document.Id, TestContext.Current.CancellationToken);
        var final = await service.RunAsync(review, fixture.Tools, true, new(), token: TestContext.Current.CancellationToken);
        Assert.Equal(mode is "no-consent" ? LongAudioWorkflowStage.DeferredGap : LongAudioWorkflowStage.Completed, final.Stage);
        Assert.Equal(mode is "no-consent", final.ArticleOutput is null); Assert.Equal(posted, posts);
        var parity = await ReadAdaptiveClaimsWithPowerShellAsync(f, p.App, p.ScopePath, Hash(File.ReadAllText(p.ScopePath)), "valid");
        Assert.True(parity.Exit == 0, parity.Error);
        if (mode is "success")
        {
            int readiness = 0;
            var mutation = MutationAdmission((_, _, _) => { readiness++; File.AppendAllText(unknownPath, " "); return ValueTask.CompletedTask; });
            try { await Assert.ThrowsAnyAsync<Exception>(() => mutation.RequireExistingCommitmentAsync(regenerated, TestContext.Current.CancellationToken).AsTask()); }
            finally { File.WriteAllBytes(unknownPath, unknown); }
            Assert.Equal(1, readiness); Assert.Equal(ledger, File.ReadAllBytes(p.LedgerPath)); Assert.Equal(posted, posts);
            var context = await new InstalledGoogleLongAudioStore(p.App, f.Paths, e.Clock).LoadVerifiedAsync(TestContext.Current.CancellationToken);
            Assert.NotNull(context);
            var incorrect = after with { Claims = after.Claims.Select(x => x.AttemptId == newId ? x with { MaximumUsdCents = 2 } : x).ToArray() };
            File.WriteAllBytes(p.LedgerPath, JsonSerializer.SerializeToUtf8Bytes(incorrect));
            await Assert.ThrowsAsync<InvalidDataException>(() => GoogleLongAudioArticleAssembler.InspectAsync(review, context, TestContext.Current.CancellationToken));
            File.WriteAllBytes(p.LedgerPath, ledger);
            var retainedFreshness = await CheckSyntheticRegenerationFreshnessAsync(f.Root, p.ScopePath, p.ScopePath, p.LedgerPath);
            Assert.True(retainedFreshness.Exit == 0, retainedFreshness.Error);
            var replayedIntroduction = await CheckSyntheticRegenerationFreshnessAsync(f.Root, previousScope, p.ScopePath, p.LedgerPath);
            Assert.NotEqual(0, replayedIntroduction.Exit);
            string rewritten = Path.Combine(f.Root, "rewritten-explicit-approval.json");
            File.WriteAllBytes(rewritten, JsonSerializer.SerializeToUtf8Bytes(scope with
            {
                DeferredUnknownRanges =
                [descriptor with { ExplicitRegeneration = descriptor.ExplicitRegeneration! with { AttemptId = Guid.NewGuid() } }]
            }));
            var replacedApproval = await CheckSyntheticRegenerationFreshnessAsync(f.Root, p.ScopePath, rewritten, p.LedgerPath);
            Assert.NotEqual(0, replacedApproval.Exit); Assert.Contains("LongAudioExplicitRegenerationCannotBeReplacedOrCleared", replacedApproval.Error, StringComparison.Ordinal);
        }
        if (mode is not "no-consent")
        {
            File.WriteAllText(Path.Combine(p.Root, "output", first[0].Request.AttemptId.ToString("N") + ".wav"), "late original must never be mixed");
            await Assert.ThrowsAnyAsync<Exception>(() => runner.RunAsync(root, new(), token: TestContext.Current.CancellationToken));
            Assert.Equal(posted, posts);
        }
        void Seal(IReadOnlyList<GoogleLongAudioBudgetClaim> claims)
        {
            File.WriteAllBytes(p.ScopePath, JsonSerializer.SerializeToUtf8Bytes(scope)); string sha = Hash(File.ReadAllText(p.ScopePath));
            var marker = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllBytes(markerPath))!; marker["GoogleLongAudioScopeSha256"] = sha;
            File.WriteAllBytes(markerPath, JsonSerializer.SerializeToUtf8Bytes(marker));
            File.WriteAllBytes(p.LedgerPath, JsonSerializer.SerializeToUtf8Bytes(new GoogleLongAudioBudgetLedger(1, sha, claims)));
            WriteSyntheticLongProvisioningProof(install, parent, scope.ParentPolicySha256, sha, p.LedgerPath, e.Clock.GetUtcNow());
        }
        GoogleLongAudioLedgerAdmission MutationAdmission(Func<GoogleLongAudioRequest, ChirpArticleAdmissionPolicy, CancellationToken, ValueTask> ready)
        {
            var assembly = typeof(GoogleLongAudioLedgerAdmission).Assembly;
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            var authorityType = assembly.GetType("CloudScribe.Infrastructure.Generation.GoogleLongAudioRecoveryAuthority", throwOnError: true)!;
            object authority = Activator.CreateInstance(authorityType, flags, null, [root, operations, Path.Combine(p.Root, "output")], null)!;
            var deferredType = assembly.GetType("CloudScribe.Infrastructure.Generation.GoogleLongAudioDeferredWork", throwOnError: true)!;
            string sha = Hash(File.ReadAllText(p.ScopePath));
            object deferred = Activator.CreateInstance(deferredType, flags, null, [root, scope, operations, Path.Combine(p.Root, "output"), p.LedgerPath, sha, authority], null)!;
            authorityType.GetProperty("Deferred", flags)!.SetValue(authority, deferred);
            var admission = new GoogleLongAudioLedgerAdmission(p.ParentPath, scope.ParentPolicySha256, p.ScopePath, sha, p.LedgerPath, ready, e.Clock);
            typeof(GoogleLongAudioLedgerAdmission).GetMethod("WithRecovery", flags)!.Invoke(admission, [authority]);
            return admission;
        }
    }

    private static async Task<(int Exit, string Error)> CheckSyntheticRegenerationFreshnessAsync(string profile, string oldScope, string nextScope, string ledger)
    {
        string? repository = AppContext.BaseDirectory;
        while (repository is not null && !File.Exists(Path.Combine(repository, "tools", "long-audio", "Deferred.Scope.ps1"))) repository = Path.GetDirectoryName(repository);
        Assert.NotNull(repository);
        var start = new System.Diagnostics.ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "-NoProfile", "-File", Path.Combine(repository, "tests", "CloudScribe.Infrastructure.Tests", "LongAudioExplicitRegeneration.Freshness.ps1"),
            repository, profile, oldScope, nextScope, ledger }) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        return (process.ExitCode, await output.ConfigureAwait(false) + await error.ConfigureAwait(false));
    }
}
