using System.Diagnostics;
using System.Text.Json;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("duplicate")]
    [InlineData("unmapped")]
    [InlineData("wrong-format")]
    public async Task LongAudioProvisioningValidatesExplicitRecoveryRootsWithoutActivatingUnreviewedFamilies(string fault)
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await ChirpEvidenceAsync(f);
        var p = await PrepareLongProvisioningAsync(f, e);
        var scope = JsonSerializer.Deserialize<GoogleLongAudioBudgetScope>(File.ReadAllBytes(p.ScopePath))!;
        var original = scope.DocumentJobs![0];
        using var payload = JsonDocument.Parse(p.Request.CopyPayload());
        var request = GoogleLongAudioRequest.Create(p.Request.AttemptId, scope.ProjectNumber, "global", scope.CredentialReferenceId,
            original.OutputGcsUri.Replace(".mp3", ".wav", StringComparison.Ordinal), payload.RootElement.GetProperty("input").GetProperty("text").GetString()!,
            scope.LanguageCode, scope.VoiceName, "LINEAR16");
        scope = scope with
        {
            AudioEncoding = fault is "wrong-format" ? "MP3" : "LINEAR16",
            DocumentJobs = [original with { OutputGcsUri = request.OutputGcsUri }],
            ApprovedRequestCeilingsUsdCents = new Dictionary<string, long>(StringComparer.Ordinal) { [request.BindingSha256] = 1 },
            AdaptiveRecoveryRootBindings = fault switch
            {
                "duplicate" => [request.BindingSha256, request.BindingSha256],
                "unmapped" => [new string('a', 64)],
                _ => [request.BindingSha256]
            }
        };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(scope); File.WriteAllBytes(p.ScopePath, bytes);
        var result = await RunLongProvisioningAsync(f, p.App, p.ScopePath, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), "Install");
        Assert.Equal(fault is "valid", result.ExitCode == 0);
        if (fault is not "valid") Assert.False(File.Exists(p.LedgerPath));
        else
        {
            var context = await new InstalledGoogleLongAudioStore(p.App, f.Paths, e.Clock).LoadVerifiedAsync(TestContext.Current.CancellationToken);
            Assert.NotNull(context); Assert.Equal(request.BindingSha256, Assert.Single(context.Scope.AdaptiveRecoveryRootBindings!));
            Assert.Empty(JsonSerializer.Deserialize<GoogleLongAudioBudgetLedger>(File.ReadAllBytes(p.LedgerPath))!.Claims);
        }
    }

    [Theory]
    [InlineData("Staged-scope.json")]
    [InlineData("Staged-marker-before.json")]
    [InlineData("Staged-marker-after.json")]
    [InlineData("Staged-ledger-initial.json")]
    [InlineData("Staged-plan.json")]
    [InlineData("Staged-receipt.json")]
    [InlineData("Prepared")]
    [InlineData("ScopeWritten")]
    [InlineData("LedgerWritten")]
    [InlineData("MarkerPublished")]
    [InlineData("Complete")]
    public async Task LongAudioProvisioningInterruptionResumesSameReviewedPlanWithoutResettingHistory(string checkpoint)
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await ChirpEvidenceAsync(f);
        var p = await PrepareLongProvisioningAsync(f, e);
        byte[] parent = File.ReadAllBytes(p.ParentPath);
        var interrupted = await RunLongProvisioningAsync(f, p.App, p.ScopePath, p.ScopeSha, "Install", checkpoint);
        Assert.NotEqual(0, interrupted.ExitCode);
        Assert.Contains("SyntheticInterruption", interrupted.Error, StringComparison.Ordinal);
        var store = new InstalledGoogleLongAudioStore(p.App, f.Paths, e.Clock);
        if (checkpoint is not "Complete") Assert.Null(await store.LoadVerifiedAsync(TestContext.Current.CancellationToken));
        var resumed = await RunLongProvisioningAsync(f, p.App, p.ScopePath, p.ScopeSha, "Resume");
        Assert.True(resumed.ExitCode == 0, resumed.Error);
        Assert.NotNull(await store.LoadVerifiedAsync(TestContext.Current.CancellationToken));
        Assert.Equal(parent, File.ReadAllBytes(p.ParentPath));
        var ledger = JsonSerializer.Deserialize<GoogleLongAudioBudgetLedger>(File.ReadAllBytes(p.LedgerPath))!;
        Assert.Empty(ledger.Claims); Assert.Equal(p.ScopeSha, ledger.ScopeSha256);
        byte[] before = File.ReadAllBytes(p.LedgerPath);
        var again = await RunLongProvisioningAsync(f, p.App, p.ScopePath, p.ScopeSha, "Install");
        Assert.True(again.ExitCode == 0, again.Error); Assert.Equal(before, File.ReadAllBytes(p.LedgerPath));
    }

    [Theory]
    [InlineData("Prepared")]
    [InlineData("LedgerWritten")]
    [InlineData("MarkerPublished")]
    public async Task LongAudioProvisioningRollbackRestoresBlockedOriginalAndPreservesSealedBackup(string checkpoint)
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await ChirpEvidenceAsync(f);
        var p = await PrepareLongProvisioningAsync(f, e);
        string install = Path.GetDirectoryName(p.App)!; byte[] before = File.ReadAllBytes(Path.Combine(install, "cloudscribe-install.json"));
        var interrupted = await RunLongProvisioningAsync(f, p.App, p.ScopePath, p.ScopeSha, "Install", checkpoint);
        Assert.NotEqual(0, interrupted.ExitCode); Assert.Contains("SyntheticInterruption", interrupted.Error, StringComparison.Ordinal);
        var rollback = await RunLongProvisioningAsync(f, p.App, p.ScopePath, p.ScopeSha, "Rollback");
        Assert.True(rollback.ExitCode == 0, rollback.Error);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(install, "cloudscribe-install.json")));
        Assert.False(File.Exists(p.LedgerPath)); Assert.False(File.Exists(Path.Combine(install, "google-long-audio-scope.json")));
        Assert.True(File.Exists(Path.Combine(install, "long-audio-provisioning", "marker-before.json")));
        Assert.NotEqual(0, (await RunLongProvisioningAsync(f, p.App, p.ScopePath, p.ScopeSha, "Resume")).ExitCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LongAudioProvisioningCannotRollbackAClaimOrMutateWhileTheProfileIsOpen(bool uncertainOperation)
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await ChirpEvidenceAsync(f);
        var p = await PrepareLongProvisioningAsync(f, e);
        using (var active = new FileStream(f.Paths.InstanceLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            Assert.NotEqual(0, (await RunLongProvisioningAsync(f, p.App, p.ScopePath, p.ScopeSha, "Install")).ExitCode);
        Assert.False(File.Exists(p.LedgerPath));
        var installed = await RunLongProvisioningAsync(f, p.App, p.ScopePath, p.ScopeSha, "Install"); Assert.True(installed.ExitCode == 0, installed.Error);
        if (uncertainOperation)
        {
            string operations = Path.Combine(Path.GetDirectoryName(p.LedgerPath)!, "operations"); Directory.CreateDirectory(operations);
            File.WriteAllText(Path.Combine(operations, "observation-pending.guard"), "synthetic unknown evidence");
        }
        else File.WriteAllBytes(p.LedgerPath, JsonSerializer.SerializeToUtf8Bytes(new GoogleLongAudioBudgetLedger(1, p.ScopeSha,
            [new(p.Request.AttemptId, p.Request.BindingSha256, 1)])));
        byte[] before = File.ReadAllBytes(p.LedgerPath);
        Assert.NotEqual(0, (await RunLongProvisioningAsync(f, p.App, p.ScopePath, p.ScopeSha, "Rollback")).ExitCode);
        Assert.Equal(before, File.ReadAllBytes(p.LedgerPath));
    }

    [Fact]
    public async Task LongAudioProvisioningRejectsAnExactlyPinnedButInvalidParentBeforeAnyEffect()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await ChirpEvidenceAsync(f);
        e.Admission.Policy = e.Admission.Policy! with { MaximumNewAttempts = 0 };
        var p = await PrepareLongProvisioningAsync(f, e);
        string marker = Path.Combine(Path.GetDirectoryName(p.App)!, "cloudscribe-install.json"); byte[] before = File.ReadAllBytes(marker);
        var result = await RunLongProvisioningAsync(f, p.App, p.ScopePath, p.ScopeSha, "Install");
        Assert.NotEqual(0, result.ExitCode); Assert.Contains("Article grant identity", result.Error, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(marker)); Assert.False(File.Exists(p.LedgerPath));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(p.App)!, "long-audio-provisioning.lock")));
    }

    private static async Task<(string App, string ParentPath, string ScopePath, string ScopeSha, string LedgerPath,
        GoogleLongAudioRequest Request)> PrepareLongProvisioningAsync(Fixture f, ChirpEvidence e)
    {
        var document = await f.Library.CreateAsync("Synthetic provisioning", "\u4f60\u597d\u3002 <&> \"quote\" " + char.ConvertFromUtf32(0x1f600), TestContext.Current.CancellationToken).ConfigureAwait(false);
        var p = await PrepareLongInstalledAsync(f, e, longDocument: document).ConfigureAwait(false);
        string install = Path.GetDirectoryName(p.App)!; string reviewed = Path.Combine(f.Root, "reviewed-long-scope.json"); File.Copy(p.ScopePath, reviewed);
        var markerPath = Path.Combine(install, "cloudscribe-install.json");
        var marker = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(markerPath))!; marker.Remove("GoogleLongAudioScopeSha256");
        File.WriteAllBytes(markerPath, JsonSerializer.SerializeToUtf8Bytes(marker));
        File.Delete(p.ScopePath); File.Delete(p.LedgerPath); File.Delete(Path.Combine(install, "long-audio-provisioning.lock"));
        string transaction = Path.Combine(install, "long-audio-provisioning");
        File.Delete(Path.Combine(transaction, "plan.json")); File.Delete(Path.Combine(transaction, "receipt.json")); Directory.Delete(transaction);
        return (p.App, p.ParentPath, reviewed, p.ScopeSha, p.LedgerPath, p.Request);
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunLongProvisioningAsync(Fixture f, string app, string scope,
        string sha, string phase, string stopAt = "")
    {
        string? root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "tools", "long-audio", "Provisioning.Files.ps1"))) root = Path.GetDirectoryName(root);
        if (root is null) throw new InvalidOperationException("Current checkout's Long Audio sources are missing.");
        string runner = Path.Combine(f.Root, "synthetic-provisioning-runner.ps1");
        File.WriteAllText(runner, """
            param($Repository,$Install,$Profile,$Scope,$Sha,$Phase,$StopAt)
            $ErrorActionPreference='Stop'
            . (Join-Path $Repository 'scripts/Install-CloudScribe.ps1') -CompletionFunctionsOnly
            foreach($name in @('Provisioning.Files.ps1','Provisioning.Scope.ps1','Provisioning.Transaction.ps1')){. (Join-Path $Repository ('tools/long-audio/'+$name))}
            $checkpoint={param($stage) if($StopAt -and $stage -ceq $StopAt){throw 'SyntheticInterruption'}}.GetNewClosure()
            Invoke-LAProvisioning $Install $Profile $Scope $Sha $Phase $checkpoint | ConvertTo-Json -Compress
            """);
        var start = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string value in new[] { "-NoLogo", "-NoProfile", "-File", runner, root, Path.GetDirectoryName(app)!, f.Root, scope, sha, phase, stopAt }) start.ArgumentList.Add(value);
        using var process = Process.Start(start) ?? throw new IOException("Synthetic provisioning child did not start.");
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        return (process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
    }
}
