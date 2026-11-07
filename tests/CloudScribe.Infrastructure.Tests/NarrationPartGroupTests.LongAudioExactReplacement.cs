using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Diagnostics;
using CloudScribe.Infrastructure.Generation;
using Microsoft.Extensions.Logging;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Fact]
    public Task ExactFailedChildReplacementPreservesSiblingHoldAndAssemblesWithoutRestartPost() => RunExactFailedChildReplacementAsync(false);

    [Fact]
    public Task ExactReplacementSentenceRejectionUsesOrdinaryStrictlySmallerChildrenWithoutUnchangedRetry() => RunExactFailedChildReplacementAsync(true);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051:Method is too long", Justification = "One isolated fixture proves exact scope replacement through the actual ledger, resolver, artifact store, assembly and restart; all HTTP and media tools are synthetic.")]
    private static async Task RunExactFailedChildReplacementAsync(bool splitReplacement)
    {
        using var f = new Fixture(); await f.InitializeAsync().ConfigureAwait(false); var e = await ChirpEvidenceAsync(f).ConfigureAwait(false);
        var document = await f.Library.CreateAsync("Synthetic exact replacement", "First complete paragraph.\n\nSecond complete sentence. Another complete sentence.", TestContext.Current.CancellationToken).ConfigureAwait(false);
        var p = await PrepareLongInstalledAsync(f, e, longDocument: document).ConfigureAwait(false);
        var scope = JsonSerializer.Deserialize<GoogleLongAudioBudgetScope>(File.ReadAllBytes(p.ScopePath))!;
        var root = GoogleLongAudioRequest.Create(p.Request.AttemptId, scope.ProjectNumber, "global", scope.CredentialReferenceId,
            p.Request.OutputGcsUri.Replace(".mp3", ".wav", StringComparison.Ordinal), document.Text, scope.LanguageCode, scope.VoiceName, "LINEAR16");
        var range = new ChirpNarrationRange(0, document.Text.EnumerateRunes().Count(), Hash(document.Text), null);
        var children = GoogleLongAudioRecoveryPlanner.Split(document.Text, range, root, AdaptiveRejected(root), false, 30);
        var failed = children[1]; string failedText = ChirpNarrationPlanner.Slice(document.Text, failed.Range);
        Guid freshId = Guid.NewGuid();
        var replacement = GoogleLongAudioRequest.Create(freshId, root.ProjectNumber, "global", root.CredentialReferenceId,
            root.OutputGcsUri[..(root.OutputGcsUri.LastIndexOf('/') + 1)] + freshId.ToString("N") + ".wav", failedText, root.LanguageCode, root.VoiceName, "LINEAR16");
        var smaller = GoogleLongAudioRecoveryPlanner.Split(document.Text, failed.Range, replacement, AdaptiveRejected(replacement), false, 30);
        int expectedPosts = splitReplacement ? 6 : 4, expectedOutputs = splitReplacement ? 3 : 2;
        scope = scope with
        {
            AudioEncoding = "LINEAR16",
            TotalGoogleCapUsdCents = splitReplacement ? 920 : 918,
            ApprovedRequestCeilingsUsdCents = new Dictionary<string, long>(StringComparer.Ordinal) { [root.BindingSha256] = 1 },
            AdaptiveRecoveryRootBindings = [root.BindingSha256],
            DocumentJobs = [scope.DocumentJobs![0] with { OutputGcsUri = root.OutputGcsUri }]
        };
        string install = Path.GetDirectoryName(p.App)!;
        var parent = JsonSerializer.Deserialize<ChirpArticleAdmissionPolicy>(File.ReadAllBytes(p.ParentPath))!;
        void SealScope(bool preserveClaims)
        {
            File.WriteAllBytes(p.ScopePath, JsonSerializer.SerializeToUtf8Bytes(scope)); string scopeSha = Hash(File.ReadAllText(p.ScopePath));
            string markerPath = Path.Combine(install, "cloudscribe-install.json");
            var marker = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllBytes(markerPath))!;
            marker["Profile"] = f.Root;
            marker["GoogleLongAudioScopeSha256"] = scopeSha; File.WriteAllBytes(markerPath, JsonSerializer.SerializeToUtf8Bytes(marker));
            var ledger = preserveClaims ? JsonSerializer.Deserialize<GoogleLongAudioBudgetLedger>(File.ReadAllBytes(p.LedgerPath))! : new(1, scopeSha, []);
            File.WriteAllBytes(p.LedgerPath, JsonSerializer.SerializeToUtf8Bytes(ledger with { ScopeSha256 = scopeSha }));
            WriteSyntheticLongProvisioningProof(install, parent, scope.ParentPolicySha256, scopeSha, p.LedgerPath, e.Clock.GetUtcNow());
        }
        SealScope(false);
        SealSyntheticHistoricalPlan(install, p.LedgerPath, Hash(File.ReadAllText(p.ScopePath)), File.ReadAllBytes(p.ScopePath));
        string? mediaDirectory = Environment.GetEnvironmentVariable("CLOUDSCRIBE_SYNTHETIC_LONG_AUDIO_MEDIA_TOOLS");
        GoogleLongAudioRequest[] allowed = [root, children[0].Request, failed.Request, replacement, .. smaller.Select(x => x.Request)];
        var transport = new AdaptiveTransport(allowed, splitReplacement ? [root.AttemptId, replacement.AttemptId] : [root.AttemptId],
            pcm: mediaDirectory is null ? null : AdaptivePcm(), writeFailure: failed.Request.AttemptId);
        var native = new ExactReplacementNative(mediaDirectory is null ? null : new BoundedNativeMediaTool(), expectedOutputs);
        using var history = new GoogleTrendHistoryLoggerProvider(f.Paths, e.Clock);
        using var logs = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Information).AddProvider(history));
        var original = await PrepareLongRuntimeAsync(f, e, p.App, root, () => new AdaptiveHandler(transport), native, logs).ConfigureAwait(false);
        var tools = mediaDirectory is null ? original.Tools : await AdaptiveActualToolsAsync(mediaDirectory).ConfigureAwait(false);
        var runner = await original.Factory.CreateRecoveryRunnerAsync(root, tools, new(4096, null, "pcm_s16le", 24000, 1), true, history, TestContext.Current.CancellationToken).ConfigureAwait(false);
        var stopped = await runner.RunAsync(root, new(), token: TestContext.Current.CancellationToken).ConfigureAwait(false);
        Assert.False(stopped.CoverageComplete); Assert.Single(stopped.Outputs); Assert.Equal(3, transport.Posts);
        string operations = Path.Combine(p.Root, "operations"), failedPath = Path.Combine(operations, failed.Request.AttemptId.ToString("N") + ".json");
        byte[] retainedFailure = File.ReadAllBytes(failedPath);
        var before = JsonSerializer.Deserialize<GoogleLongAudioBudgetLedger>(File.ReadAllBytes(p.LedgerPath))!;
        scope = scope with
        {
            SourceApprovalSha256 = new string('f', 64),
            FailedRangeReplacements = [new(root.BindingSha256, failed.Request.AttemptId, failed.Request.BindingSha256,
            Hash(System.Text.Encoding.UTF8.GetString(retainedFailure)), failed.Range.StartScalar, failed.Range.EndScalarExclusive, failed.Range.TextSha256,
            freshId, replacement.OutputGcsUri, replacement.BindingSha256, 1)]
        };
        await ApplySyntheticExactReplacementTransitionAsync(f, p.App, p.ScopePath, p.LedgerPath, scope, failed.Request.AttemptId).ConfigureAwait(false);
        var fresh = await PrepareLongRuntimeAsync(f, e, p.App, root, () => new AdaptiveHandler(transport), native, logs).ConfigureAwait(false);
        var service = new GoogleLongAudioDocumentService(new(p.App, f.Paths, e.Clock), f, new CloudScribe.Infrastructure.Files.DocumentContentStore(f.Paths),
            fresh.Factory, e.Clock, logs.CreateLogger<GoogleLongAudioDocumentService>(), history, native);
        var review = await service.ReviewAsync(document.Id, TestContext.Current.CancellationToken).ConfigureAwait(false);
        Assert.Equal(1, review.ApprovedReplacementCount); Assert.Equal(1, review.AdditionalReplacementHoldCents);
        var complete = await service.RunAsync(review, tools, true, new(), token: TestContext.Current.CancellationToken).ConfigureAwait(false);
        Assert.Equal(LongAudioWorkflowStage.Completed, complete.Stage); Assert.Equal(expectedOutputs, complete.ArticleOutput!.Ordered.Count); Assert.Equal(expectedPosts, transport.Posts);
        Assert.Equal(retainedFailure, File.ReadAllBytes(failedPath));
        var after = JsonSerializer.Deserialize<GoogleLongAudioBudgetLedger>(File.ReadAllBytes(p.LedgerPath))!;
        Assert.Equal(expectedPosts, after.Claims.Sum(x => x.MaximumUsdCents));
        foreach (var old in before.Claims) Assert.Equal(old, Assert.Single(after.Claims, x => x.AttemptId == old.AttemptId));
        byte[] retainedLedger = File.ReadAllBytes(p.LedgerPath);
        foreach (string fault in new[] { "valid", "range", "parent", "planner", "amount", "missing", "extra" })
        {
            var readback = await ReadAdaptiveClaimsWithPowerShellAsync(f, p.App, p.ScopePath, Hash(File.ReadAllText(p.ScopePath)), "replacement-" + fault).ConfigureAwait(false);
            Assert.True(fault is "valid" ? readback.Exit == 0 : readback.Exit != 0, readback.Error);
            Assert.Equal(retainedLedger, File.ReadAllBytes(p.LedgerPath));
        }
        int calls = transport.Calls;
        var reopened = await service.RunAsync(await service.ReviewAsync(document.Id, TestContext.Current.CancellationToken).ConfigureAwait(false), tools, true, new(), token: TestContext.Current.CancellationToken).ConfigureAwait(false);
        Assert.Equal(complete.ArticleOutput.Sha256, reopened.ArticleOutput!.Sha256); Assert.Equal(calls, transport.Calls); Assert.Equal(expectedPosts, transport.Posts);
        logs.Dispose(); history.Dispose(); // Mirror normal app closure before snapshotting the profile for installation.
        using var applicationFiles = new Fixture();
        string separateInstall = Path.Combine(applicationFiles.Root, "installation");
        Assert.StartsWith(f.Root + Path.DirectorySeparatorChar, install, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(applicationFiles.Root + Path.DirectorySeparatorChar, separateInstall, StringComparison.OrdinalIgnoreCase);
        Directory.Move(install, separateInstall);
        foreach (bool rollback in new[] { false, true })
            await RebindSyntheticReplacementHistoryAsync(separateInstall, f.Root, rollback).ConfigureAwait(false);
        Assert.Equal(calls, transport.Calls); Assert.Equal(retainedFailure, File.ReadAllBytes(failedPath));
        await VerifySyntheticFundingAsync(separateInstall, f.Root).ConfigureAwait(false);
        var funded = await new InstalledGoogleLongAudioStore(Path.Combine(separateInstall, Path.GetFileName(p.App)), f.Paths, e.Clock)
            .ReadInstallationAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        Assert.Equal(GoogleLongAudioInstallationStatus.Ready, funded.Status);
        Assert.NotNull(funded.Context);
        Assert.Equal(expectedPosts, JsonSerializer.Deserialize<GoogleLongAudioBudgetLedger>(File.ReadAllBytes(funded.Context.LedgerPath))!.Claims.Count);
    }

    private static async Task VerifySyntheticFundingAsync(string install, string profile)
    {
        string? repository = AppContext.BaseDirectory;
        while (repository is not null && !File.Exists(Path.Combine(repository, "tools", "long-audio", "Funding.Transition.ps1"))) repository = Path.GetDirectoryName(repository);
        Assert.NotNull(repository);
        var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string value in new[] { "-NoLogo", "-NoProfile", "-File", Path.Combine(repository, "tests", "CloudScribe.Infrastructure.Tests", "LongAudioFunding.Synthetic.ps1"), repository, install, profile }) start.ArgumentList.Add(value);
        using var process = Process.Start(start) ?? throw new IOException("Synthetic funding check did not start.");
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        string text = await output.ConfigureAwait(false), details = await error.ConfigureAwait(false);
        Assert.True(process.ExitCode == 0, text + details);
        Assert.Contains("SyntheticFundingVerified", text, StringComparison.Ordinal);
    }

    private static async Task RebindSyntheticReplacementHistoryAsync(string install, string profile, bool rollback)
    {
        string? repository = AppContext.BaseDirectory;
        while (repository is not null && !File.Exists(Path.Combine(repository, "tools", "long-audio", "BuildRebind.Transaction.ps1"))) repository = Path.GetDirectoryName(repository);
        Assert.NotNull(repository);
        var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        string script = Path.Combine(repository, "tests", "CloudScribe.Infrastructure.Tests", "LongAudioBuildRebind.Synthetic.ps1");
        foreach (string value in new[] { "-NoLogo", "-NoProfile", "-File", script, repository, install, profile, rollback ? "LedgerWritten" : "AppPublished", rollback.ToString() }) start.ArgumentList.Add(value);
        using var process = Process.Start(start) ?? throw new IOException("Synthetic history rebind did not start.");
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        string text = await output.ConfigureAwait(false), details = await error.ConfigureAwait(false);
        Assert.True(process.ExitCode == 0, text + details); Assert.Contains("SyntheticBuildRebindVerified", text, StringComparison.Ordinal);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051:Method is too long", Justification = "Inline inert PowerShell fixture exercises rejected plans, preserved bytes and interrupted marker-last transition as one auditable no-provider script.")]
    private static async Task ApplySyntheticExactReplacementTransitionAsync(Fixture f, string app, string oldScopePath,
        string ledgerPath, GoogleLongAudioBudgetScope next, Guid failedId)
    {
        string install = Path.GetDirectoryName(app)!, operations = Path.Combine(Path.GetDirectoryName(ledgerPath)!, "operations");
        string Pin(string path) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
        string nextPath = Path.Combine(f.Root, "replacement-scope.json"); File.WriteAllBytes(nextPath, JsonSerializer.SerializeToUtf8Bytes(next));
        var claims = JsonSerializer.Deserialize<GoogleLongAudioBudgetLedger>(File.ReadAllBytes(ledgerPath))!.Claims;
        string planPath = Path.Combine(f.Root, "replacement-plan.json");
        File.WriteAllBytes(planPath, JsonSerializer.SerializeToUtf8Bytes(new
        {
            Version = 5,
            next.SourceApprovalSha256,
            PreviousScopeSha256 = Pin(oldScopePath),
            PreviousLedgerSha256 = Pin(ledgerPath),
            PreviousMarkerSha256 = Pin(Path.Combine(install, "cloudscribe-install.json")),
            PreviousProvisioningPlanSha256 = Pin(Path.Combine(install, "long-audio-provisioning", "plan.json")),
            PreviousProvisioningReceiptSha256 = Pin(Path.Combine(install, "long-audio-provisioning", "receipt.json")),
            PreviousOperationSha256 = Pin(Path.Combine(operations, failedId.ToString("N") + ".json")),
            PreviousAttemptId = failedId,
            NewScopePath = nextPath,
            NewScopeSha256 = Pin(nextPath),
            FailedRangeReplacement = Assert.Single(next.FailedRangeReplacements!),
            RetainedOperationPins = claims.Select(x =>
            {
                string operationPath = Path.Combine(operations, x.AttemptId.ToString("N") + ".json");
                Dictionary<string, object> pin = new(StringComparer.Ordinal) { ["AttemptId"] = x.AttemptId, ["Sha256"] = Pin(operationPath) };
                var operation = JsonSerializer.Deserialize<GoogleLongAudioCheckpoint>(File.ReadAllBytes(operationPath))!;
                if (operation.State == GoogleLongAudioState.CompletedUnverified)
                {
                    string stem = Path.Combine(Path.GetDirectoryName(ledgerPath)!, "output", x.AttemptId.ToString("N"));
                    pin["CompletedReceiptSha256"] = Pin(stem + ".recovery-verified.json"); pin["CompletedObjectSha256"] = Pin(stem + ".object.json");
                }
                return pin;
            }).ToArray()
        }));
        string? repository = AppContext.BaseDirectory;
        while (repository is not null && !File.Exists(Path.Combine(repository, "scripts", "Update-LongAudioExcerpt.ps1"))) repository = Path.GetDirectoryName(repository);
        Assert.NotNull(repository);
        string script = Path.Combine(f.Root, "apply-exact-replacement.ps1");
        File.WriteAllText(script, """
            param($Repository,$Install,$Profile,$Plan,$Sha)
            $ErrorActionPreference='Stop';$writer=Join-Path $Repository 'scripts/Update-LongAudioExcerpt.ps1'
            $parameters=@{InstallRoot=$Install;ProfileRoot=$Profile;ReviewedPlanPath=$Plan;ReviewedPlanSha256=$Sha}
            $before=& $writer @parameters -ValidateOnly
            if(-not $before.Valid -or $before.ProviderCalls -ne 0){throw 'SyntheticPreflightFailed'}
            $originalPlan=Get-Content -LiteralPath $Plan -Raw|ConvertFrom-Json -AsHashtable
            $live=@((Join-Path $Install 'google-long-audio-scope.json'),(Join-Path $Install 'cloudscribe-install.json'),(Join-Path $Profile 'data/google-long-audio/budget-ledger.json'))
            $pins=@($live|ForEach-Object {(Get-FileHash -LiteralPath $_).Hash})
            foreach($fault in @('source','cap','replay','history','completed-pins','completed-drift','pending')){
                $bad=Get-Content -LiteralPath $Plan -Raw|ConvertFrom-Json -AsHashtable
                $badScope=Get-Content -LiteralPath $bad.NewScopePath -Raw|ConvertFrom-Json -AsHashtable
                $restores=@{}
                try{
                    switch($fault){
                        'source' {$badScope.FailedRangeReplacements[-1].SourceSha256=('a'*64)}
                        'cap' {$badScope.TotalGoogleCapUsdCents++}
                        'replay' {$badScope.FailedRangeReplacements[-1].ReplacementAttemptId=$bad.PreviousAttemptId}
                        'history' {$bad.RetainedOperationPins=@($bad.RetainedOperationPins|Select-Object -First 1)}
                        'completed-pins' {foreach($item in $bad.RetainedOperationPins){if($item.Contains('CompletedReceiptSha256')){[void]$item.Remove('CompletedReceiptSha256');[void]$item.Remove('CompletedObjectSha256')}}}
                        'completed-drift' {
                            $completed=@($bad.RetainedOperationPins|Where-Object {$_.Contains('CompletedReceiptSha256')})[0]
                            $stem=Join-Path $Profile ('data/google-long-audio/output/'+([Guid]$completed.AttemptId).ToString('N'))
                            foreach($suffix in @('.recovery-verified.json','.object.json')){$path=$stem+$suffix;$restores[$path]=[IO.File]::ReadAllBytes($path)}
                            $media=Get-Content -LiteralPath ($stem+'.recovery-verified.json') -Raw|ConvertFrom-Json -AsHashtable
                            $object=Get-Content -LiteralPath ($stem+'.object.json') -Raw|ConvertFrom-Json -AsHashtable
                            $media.Artifact.Identity.Generation='2';$object.Generation='2'
                            [IO.File]::WriteAllText($stem+'.recovery-verified.json',($media|ConvertTo-Json -Depth 64 -Compress))
                            [IO.File]::WriteAllText($stem+'.object.json',($object|ConvertTo-Json -Depth 64 -Compress))
                        }
                        'pending' {
                            $path=Join-Path $Profile ('data/google-long-audio/operations/'+([Guid]$bad.PreviousAttemptId).ToString('N')+'.json')
                            $restores[$path]=[IO.File]::ReadAllBytes($path);$op=Get-Content -LiteralPath $path -Raw|ConvertFrom-Json -AsHashtable;$op.State=1
                            [IO.File]::WriteAllText($path,($op|ConvertTo-Json -Depth 64 -Compress));$changed=(Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant()
                            foreach($item in $bad.RetainedOperationPins){if([Guid]$item.AttemptId -eq [Guid]$bad.PreviousAttemptId){$item.Sha256=$changed}}
                            $bad.PreviousOperationSha256=$changed;$badScope.FailedRangeReplacements[-1].FailedCheckpointSha256=$changed
                        }
                    }
                    $badScopePath=$Plan+'.bad-scope.json';[IO.File]::WriteAllText($badScopePath,($badScope|ConvertTo-Json -Depth 64 -Compress))
                    $bad.NewScopePath=$badScopePath;$bad.NewScopeSha256=(Get-FileHash -LiteralPath $badScopePath).Hash.ToLowerInvariant()
                    $bad.FailedRangeReplacement=$badScope.FailedRangeReplacements[-1]
                    $badPlanPath=$Plan+'.bad-plan.json';[IO.File]::WriteAllText($badPlanPath,($bad|ConvertTo-Json -Depth 64 -Compress))
                    $rejected=$false
                    try{& $writer -InstallRoot $Install -ProfileRoot $Profile -ReviewedPlanPath $badPlanPath -ReviewedPlanSha256 (Get-FileHash -LiteralPath $badPlanPath).Hash.ToLowerInvariant() -ValidateOnly|Out-Null}
                    catch{$rejected=$true}
                    if(-not $rejected){throw ('UnsafeSyntheticTransitionAccepted:'+ $fault)}
                    for($i=0;$i -lt $live.Count;$i++){if((Get-FileHash -LiteralPath $live[$i]).Hash -cne $pins[$i]){throw 'SyntheticRejectionChangedLiveFixture'}}
                }finally{foreach($path in $restores.Keys){[IO.File]::WriteAllBytes($path,$restores[$path])}}
            }
            $interrupted=$false
            try{& $writer @parameters -AfterCheckpoint {param($phase) if($phase -ceq 'ScopeWritten'){throw 'SyntheticInterruption'}}}
            catch{if($_.Exception.Message -cne 'SyntheticInterruption'){throw};$interrupted=$true}
            if(-not $interrupted){throw 'ExpectedSyntheticInterruption'}
            $result=& $writer @parameters -Resume
            if(-not $result.Updated -or $result.ProviderCalls -ne 0 -or $result.NewCommitments -ne 0){throw 'SyntheticResumeFailed'}
            'SyntheticExactReplacementTransitionVerified'
            """);
        var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string value in new[] { "-NoLogo", "-NoProfile", "-File", script, repository, install, f.Root, planPath, Pin(planPath) }) start.ArgumentList.Add(value);
        using var process = Process.Start(start) ?? throw new IOException("Synthetic exact transition did not start.");
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        Assert.True(process.ExitCode == 0, await output.ConfigureAwait(false) + await error.ConfigureAwait(false));
        Assert.Equal(Pin(nextPath), Pin(oldScopePath));
    }

    private sealed class ExactReplacementNative(INativeMediaTool? actual = null, int expectedOutputs = 2) : INativeMediaTool
    {
        public Task<NativeMediaToolResult> RunAsync(NativeMediaToolInvocation invocation, CancellationToken cancellationToken = default)
        {
            if (actual is not null) return actual.RunAsync(invocation, cancellationToken);
            bool probe = invocation.Arguments.Contains("-show_entries", StringComparer.Ordinal);
            string file = invocation.Arguments[^1];
            if (invocation.Arguments.Contains("libmp3lame", StringComparer.Ordinal)) File.WriteAllText(file, "Synthetic inert MP3 fixture only.");
            bool mp3 = file.EndsWith(".mp3", StringComparison.Ordinal);
            return Task.FromResult(new NativeMediaToolResult(0, false, probe ? JsonSerializer.Serialize(new
            { streams = new[] { new { codec_type = "audio", codec_name = mp3 ? "mp3" : "pcm_s16le", sample_rate = "24000", channels = 1 } }, format = new { duration = mp3 ? expectedOutputs.ToString(System.Globalization.CultureInfo.InvariantCulture) : "1" } }) : "", "", TimeSpan.Zero));
        }
    }
}
