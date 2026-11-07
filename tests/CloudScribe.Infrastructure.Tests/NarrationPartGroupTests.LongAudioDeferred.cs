using System.Net;
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
    [InlineData("none")]
    [InlineData("checkpoint")]
    [InlineData("claim")]
    [InlineData("guard")]
    [InlineData("range")]
    [InlineData("root")]
    [InlineData("extra")]
    [InlineData("second-unknown")]
    [InlineData("budget")]
    [InlineData("orphan")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051:Method is too long", Justification = "One composed synthetic lifecycle proves unchanged unknown evidence, real cumulative ledger, guarded disjoint POST, restart and final assembly refusal.")]
    public async Task LongAudioDeferredUnknownPreservesHoldAndOnlyContinuesProvenDisjointWork(string fault)
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await ChirpEvidenceAsync(f);
        var document = await f.Library.CreateAsync("Synthetic deferred", "Alpha first paragraph.\n\nBeta second paragraph.", TestContext.Current.CancellationToken);
        var p = await PrepareLongInstalledAsync(f, e, longDocument: document);
        var scope = JsonSerializer.Deserialize<GoogleLongAudioBudgetScope>(File.ReadAllBytes(p.ScopePath))!;
        var root = GoogleLongAudioRequest.Create(p.Request.AttemptId, scope.ProjectNumber, "global", scope.CredentialReferenceId,
            p.Request.OutputGcsUri.Replace(".mp3", ".wav", StringComparison.Ordinal), document.Text, scope.LanguageCode, scope.VoiceName, "LINEAR16");
        var first = GoogleLongAudioRecoveryPlanner.Split(document.Text,
            new(0, document.Text.EnumerateRunes().Count(), Hash(document.Text), null), root, AdaptiveRejected(root), false, 30);
        scope = scope with
        {
            AudioEncoding = "LINEAR16",
            TotalGoogleCapUsdCents = 919,
            ApprovedRequestCeilingsUsdCents = new Dictionary<string, long>(StringComparer.Ordinal) { [root.BindingSha256] = 1 },
            AdaptiveRecoveryRootBindings = [root.BindingSha256],
            DocumentJobs = [scope.DocumentJobs![0] with { OutputGcsUri = root.OutputGcsUri }]
        };
        string install = Path.GetDirectoryName(p.App)!, markerPath = Path.Combine(install, "cloudscribe-install.json");
        var parent = JsonSerializer.Deserialize<ChirpArticleAdmissionPolicy>(File.ReadAllBytes(p.ParentPath))!;
        RebindSyntheticScope([]);
        using var history = new GoogleTrendHistoryLoggerProvider(f.Paths, e.Clock);
        using var logs = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Information).AddProvider(history));
        var transport = new AdaptiveTransport([root, first[0].Request, first[1].Request], [root.AttemptId]);
        int posts = 0;
        var fixture = await PrepareLongRuntimeAsync(f, e, p.App, root,
            () => new DeferredHandler(transport, first[0].Request, fault is "second-unknown" ? first[1].Request : null, () => posts++),
            new AdaptiveNative(new()), logs);
        var allowance = new GoogleLongAudioOutputAllowance(4096, null, "pcm_s16le", 24000, 1);
        var initial = await fixture.Factory.CreateRecoveryRunnerAsync(root, fixture.Tools, allowance, true, history, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<HttpRequestException>(() => initial.RunAsync(root, new(), token: TestContext.Current.CancellationToken));
        Assert.Equal(2, posts);
        string operations = Path.Combine(p.Root, "operations"), unknownPath = Path.Combine(operations, first[0].Request.AttemptId.ToString("N") + ".json");
        string guardPath = Path.Combine(operations, "observation-pending.guard");
        byte[] unknownBytes = File.ReadAllBytes(unknownPath), guardBytes = File.ReadAllBytes(guardPath);
        var oldLedger = JsonSerializer.Deserialize<GoogleLongAudioBudgetLedger>(File.ReadAllBytes(p.LedgerPath))!;
        var claim = Assert.Single(oldLedger.Claims, x => x.AttemptId == first[0].Request.AttemptId);
        var descriptor = new GoogleLongAudioDeferredRange(first[0].Request.AttemptId, root.BindingSha256,
            first[0].Range.StartScalar, first[0].Range.EndScalarExclusive, Hash(Encoding.UTF8.GetString(unknownBytes)),
            Hash(JsonSerializer.Serialize(claim)), Hash(Encoding.ASCII.GetString(guardBytes)));
        if (fault is "range") descriptor = descriptor with { EndScalarExclusive = descriptor.EndScalarExclusive + 1 };
        if (fault is "root") descriptor = descriptor with { RootBindingSha256 = new string('a', 64) };
        scope = scope with { DeferredUnknownRanges = [descriptor], TotalGoogleCapUsdCents = fault is "budget" ? 916 : 919 }; RebindSyntheticScope(oldLedger.Claims);
        if (fault is "checkpoint") File.AppendAllText(unknownPath, " ");
        if (fault is "guard") File.WriteAllText(guardPath, Guid.NewGuid().ToString("N"));
        if (fault is "extra") File.WriteAllText(Path.Combine(operations, Guid.NewGuid().ToString("N") + ".observation-pending.guard"), "unexpected");
        if (fault is "orphan") File.WriteAllText(Path.Combine(p.Root, "output", claim.AttemptId.ToString("N") + ".wav"), "orphan synthetic output");
        if (fault is "claim")
        {
            var ledger = JsonSerializer.Deserialize<GoogleLongAudioBudgetLedger>(File.ReadAllBytes(p.LedgerPath))!;
            File.WriteAllBytes(p.LedgerPath, JsonSerializer.SerializeToUtf8Bytes(ledger with
            { Claims = ledger.Claims.Select(x => x.AttemptId == claim.AttemptId ? x with { MaximumUsdCents = 2 } : x).ToArray() }));
        }
        if (fault is not ("none" or "second-unknown"))
        {
            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                var rejected = await fixture.Factory.CreateRecoveryRunnerAsync(root, fixture.Tools, allowance, true, history, TestContext.Current.CancellationToken).ConfigureAwait(false);
                _ = await rejected.RunAsync(root, new(), token: TestContext.Current.CancellationToken).ConfigureAwait(false);
            });
            Assert.Equal(2, posts); return;
        }
        var runner = await fixture.Factory.CreateRecoveryRunnerAsync(root, fixture.Tools, allowance, true, history, TestContext.Current.CancellationToken);
        if (fault is "second-unknown")
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => runner.RunAsync(root, new(), token: TestContext.Current.CancellationToken));
            Assert.Equal(3, posts);
            byte[] stoppedLedger = File.ReadAllBytes(p.LedgerPath);
            await Assert.ThrowsAnyAsync<Exception>(() => runner.RunAsync(root, new(), token: TestContext.Current.CancellationToken));
            Assert.Equal(3, posts); Assert.Equal(stoppedLedger, File.ReadAllBytes(p.LedgerPath)); return;
        }
        var result = await runner.RunAsync(root, new(), token: TestContext.Current.CancellationToken);
        Assert.False(result.CoverageComplete); Assert.Single(result.Outputs);
        Assert.Equal(first[1].Range, result.Outputs[0].Range); Assert.Equal(claim.AttemptId, result.StoppedOutcome!.AttemptId);
        Assert.Equal(3, posts); Assert.Equal(unknownBytes, File.ReadAllBytes(unknownPath)); Assert.Equal(guardBytes, File.ReadAllBytes(guardPath));
        var after = JsonSerializer.Deserialize<GoogleLongAudioBudgetLedger>(File.ReadAllBytes(p.LedgerPath))!;
        Assert.Equal(claim, Assert.Single(after.Claims, x => x.AttemptId == claim.AttemptId)); Assert.Equal(3, after.Claims.Sum(x => x.MaximumUsdCents));
        byte[] held = File.ReadAllBytes(p.LedgerPath);
        var restarted = await runner.RunAsync(root, new(), token: TestContext.Current.CancellationToken);
        Assert.False(restarted.CoverageComplete); Assert.Equal(result.Outputs, restarted.Outputs); Assert.Equal(3, posts); Assert.Equal(held, File.ReadAllBytes(p.LedgerPath));
        var service = new GoogleLongAudioDocumentService(new(p.App, f.Paths, e.Clock), f, new CloudScribe.Infrastructure.Files.DocumentContentStore(f.Paths),
            fixture.Factory, e.Clock, logs.CreateLogger<GoogleLongAudioDocumentService>(), history, new AdaptiveAssemblyNative());
        var review = await service.ReviewAsync(document.Id, TestContext.Current.CancellationToken);
        var incomplete = await service.RunAsync(review, fixture.Tools, true, new(), token: TestContext.Current.CancellationToken);
        Assert.NotEqual(LongAudioWorkflowStage.Completed, incomplete.Stage); Assert.Null(incomplete.ArticleOutput);
        Assert.False(File.Exists(Path.Combine(p.Root, "output", root.AttemptId.ToString("N") + ".article.mp3"))); Assert.Equal(3, posts);
        var parity = await ReadAdaptiveClaimsWithPowerShellAsync(f, p.App, p.ScopePath, Hash(File.ReadAllText(p.ScopePath)), "valid");
        Assert.True(parity.Exit == 0, parity.Error);
        // A separate inert installation transaction introduces the reviewed descriptor with no provider transport.
        var retained = JsonSerializer.Deserialize<GoogleLongAudioBudgetLedger>(File.ReadAllBytes(p.LedgerPath))!;
        scope = scope with { DeferredUnknownRanges = null }; RebindSyntheticScope(retained.Claims);
        string descriptorPath = Path.Combine(f.Root, "synthetic-deferred-approval.json"); File.WriteAllBytes(descriptorPath, JsonSerializer.SerializeToUtf8Bytes(descriptor));
        logs.Dispose(); history.Dispose(); // Installation requires a genuinely closed synthetic profile too.
        await VerifySyntheticDeferredInstallationAsync(p.App, f.Paths.RootDirectory, descriptorPath);
        Assert.Equal(unknownBytes, File.ReadAllBytes(unknownPath)); Assert.Equal(guardBytes, File.ReadAllBytes(guardPath));

        void RebindSyntheticScope(IReadOnlyList<GoogleLongAudioBudgetClaim> claims)
        {
            File.WriteAllBytes(p.ScopePath, JsonSerializer.SerializeToUtf8Bytes(scope)); string scopeSha = Hash(File.ReadAllText(p.ScopePath));
            var marker = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllBytes(markerPath))!; marker["GoogleLongAudioScopeSha256"] = scopeSha;
            File.WriteAllBytes(markerPath, JsonSerializer.SerializeToUtf8Bytes(marker));
            File.WriteAllBytes(p.LedgerPath, JsonSerializer.SerializeToUtf8Bytes(new GoogleLongAudioBudgetLedger(1, scopeSha, claims)));
            WriteSyntheticLongProvisioningProof(install, parent, scope.ParentPolicySha256, scopeSha, p.LedgerPath, e.Clock.GetUtcNow());
        }
    }

    private static async Task VerifySyntheticDeferredInstallationAsync(string app, string profile, string descriptor)
    {
        using var applicationFiles = new Fixture();
        string oldInstall = Path.GetDirectoryName(app)!, install = Path.Combine(applicationFiles.Root, "installation");
        Assert.StartsWith(profile + Path.DirectorySeparatorChar, oldInstall, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(applicationFiles.Root + Path.DirectorySeparatorChar, install, StringComparison.OrdinalIgnoreCase);
        Directory.Move(oldInstall, install);
        string markerPath = Path.Combine(install, "cloudscribe-install.json");
        var marker = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllBytes(markerPath))!;
        marker["Profile"] = JsonSerializer.SerializeToElement(profile); File.WriteAllBytes(markerPath, JsonSerializer.SerializeToUtf8Bytes(marker));
        string scopePath = Path.Combine(install, "google-long-audio-scope.json"), ledgerPath = Path.Combine(profile, "data", "google-long-audio", "budget-ledger.json");
        SealSyntheticHistoricalPlan(install, ledgerPath, Hash(File.ReadAllText(scopePath)), File.ReadAllBytes(scopePath));
        File.WriteAllBytes(Path.Combine(profile, ".instance.lock"), []);
        string? repository = AppContext.BaseDirectory;
        while (repository is not null && !File.Exists(Path.Combine(repository, "tools", "long-audio", "BuildRebind.Transaction.ps1"))) repository = Path.GetDirectoryName(repository);
        Assert.NotNull(repository);
        var start = new System.Diagnostics.ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "-NoProfile", "-File", Path.Combine(repository, "tests", "CloudScribe.Infrastructure.Tests", "LongAudioBuildRebind.Synthetic.ps1"),
            "-Repository", repository, "-Install", install, "-Profile", profile, "-Checkpoint", "AppPublished", "-Rollback", "False", "-DeferredDescriptorPath", descriptor }) start.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken); var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        string stdout = await output.ConfigureAwait(false), stderr = await error.ConfigureAwait(false);
        Assert.True(process.ExitCode == 0, stdout + stderr); Assert.Contains("SyntheticBuildRebindVerified", stdout, StringComparison.Ordinal);
    }

    private sealed class DeferredHandler(AdaptiveTransport transport, GoogleLongAudioRequest unknown, GoogleLongAudioRequest? secondUnknown, Action post) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
            {
                post(); byte[] body = await request.Content!.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                if (body.AsSpan().SequenceEqual(unknown.CopyPayload()) || secondUnknown is not null && body.AsSpan().SequenceEqual(secondUnknown.CopyPayload()))
                    throw new HttpRequestException("Synthetic lost response after POST; never resend.");
            }
            return await transport.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }
}
