using System.Diagnostics;
using System.Text.Json;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Theory]
    [InlineData("")]
    [InlineData("PredecessorArchived")]
    [InlineData("Prepared")]
    [InlineData("ScopeWritten")]
    [InlineData("LedgerWritten")]
    [InlineData("MarkerWritten")]
    [InlineData("Complete")]
    [InlineData("reject-uncertain")]
    [InlineData("reject-prior-approval")]
    [InlineData("reject-history-omitted")]
    [InlineData("reject-attempt-replay")]
    [InlineData("reject-cap-change")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051:Method is too long", Justification = "One synthetic fixture pins the complete interrupted successor transaction and retained financial evidence.")]
    public async Task LongAudioFormatSuccessorPreservesFailedClaimAndResumesInterruptedInstallation(string stopAt)
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await ChirpEvidenceAsync(f);
        var p = await PrepareLongProvisioningAsync(f, e);
        string install = Path.GetDirectoryName(p.App)!;
        string markerPath = Path.Combine(install, "cloudscribe-install.json");
        var marker = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllBytes(markerPath))!;
        marker["Profile"] = JsonSerializer.SerializeToElement(f.Root); File.WriteAllBytes(markerPath, JsonSerializer.SerializeToUtf8Bytes(marker));
        var first = await RunLongProvisioningAsync(f, p.App, p.ScopePath, p.ScopeSha, "Install"); Assert.True(first.ExitCode == 0, first.Error);
        var oldScope = JsonSerializer.Deserialize<GoogleLongAudioBudgetScope>(File.ReadAllBytes(p.ScopePath))!;
        var claim = new GoogleLongAudioBudgetClaim(p.Request.AttemptId, p.Request.BindingSha256, 1);
        File.WriteAllBytes(p.LedgerPath, JsonSerializer.SerializeToUtf8Bytes(new GoogleLongAudioBudgetLedger(1, p.ScopeSha, [claim])));
        string operations = Path.Combine(Path.GetDirectoryName(p.LedgerPath)!, "operations"); Directory.CreateDirectory(operations);
        string priorPath = Path.Combine(operations, p.Request.AttemptId.ToString("N") + ".json");
        File.WriteAllBytes(priorPath, JsonSerializer.SerializeToUtf8Bytes(new
        {
            p.Request.AttemptId,
            p.Request.BindingSha256,
            State = string.Equals(stopAt, "reject-uncertain", StringComparison.Ordinal) ? 0 : 3,
            OperationName = (string?)null,
            HttpStatus = 400,
            OperationParseFailed = false,
            Observation = new { ReadbackVerified = true, Truncated = false, DetailsOmitted = false }
        }));
        var job = Assert.Single(oldScope.DocumentJobs!);
        string text = File.ReadAllText(Path.Combine(f.Root, "documents", job.DocumentId.ToString("N"), "content", job.RevisionId.ToString("N") + ".utf8"));
        Guid id = Guid.NewGuid(); string uri = "gs://" + oldScope.OutputBucket + "/comparison/" + id.ToString("N") + ".wav";
        var nextRequest = GoogleLongAudioRequest.Create(id, oldScope.ProjectNumber, "global", oldScope.CredentialReferenceId, uri, text,
            oldScope.LanguageCode, oldScope.VoiceName, "LINEAR16");
        var next = oldScope with
        {
            SourceApprovalSha256 = new string('e', 64),
            AudioEncoding = "LINEAR16",
            ApprovedRequestCeilingsUsdCents = new Dictionary<string, long>(StringComparer.Ordinal) { [p.Request.BindingSha256] = 1, [nextRequest.BindingSha256] = 1 },
            DocumentJobs = [job with { AttemptId = id, OutputGcsUri = uri }]
        };
        next = stopAt switch
        {
            "reject-prior-approval" => next with { SourceApprovalSha256 = oldScope.SourceApprovalSha256 },
            "reject-history-omitted" => next with { ApprovedRequestCeilingsUsdCents = new Dictionary<string, long>(StringComparer.Ordinal) { [nextRequest.BindingSha256] = 1 } },
            "reject-attempt-replay" => next with { DocumentJobs = [next.DocumentJobs![0] with { AttemptId = claim.AttemptId }] },
            "reject-cap-change" => next with { TotalGoogleCapUsdCents = oldScope.TotalGoogleCapUsdCents + 1 },
            _ => next,
        };
        string scopePath = Path.Combine(f.Root, "reviewed-linear16-scope.json"); File.WriteAllBytes(scopePath, JsonSerializer.SerializeToUtf8Bytes(next));
        string HashFile(string path) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
        var plan = new
        {
            Version = 1,
            next.SourceApprovalSha256,
            PreviousScopeSha256 = p.ScopeSha,
            PreviousLedgerSha256 = HashFile(p.LedgerPath),
            PreviousMarkerSha256 = HashFile(markerPath),
            PreviousProvisioningPlanSha256 = HashFile(Path.Combine(install, "long-audio-provisioning", "plan.json")),
            PreviousProvisioningReceiptSha256 = HashFile(Path.Combine(install, "long-audio-provisioning", "receipt.json")),
            PreviousOperationSha256 = HashFile(priorPath),
            PreviousAttemptId = p.Request.AttemptId,
            NewScopePath = scopePath,
            NewScopeSha256 = HashFile(scopePath)
        };
        string planPath = Path.Combine(f.Root, "reviewed-format-transition.json"); File.WriteAllBytes(planPath, JsonSerializer.SerializeToUtf8Bytes(plan));
        byte[] parent = File.ReadAllBytes(p.ParentPath), prior = File.ReadAllBytes(priorPath);
        byte[] markerBefore = File.ReadAllBytes(markerPath), ledgerBefore = File.ReadAllBytes(p.LedgerPath);
        var validation = await RunLongFormatSuccessorAsync(f, install, planPath, HashFile(planPath), "", true, false);
        if (stopAt.StartsWith("reject-", StringComparison.Ordinal))
        {
            Assert.NotEqual(0, validation.ExitCode); Assert.Equal(markerBefore, File.ReadAllBytes(markerPath));
            Assert.Equal(ledgerBefore, File.ReadAllBytes(p.LedgerPath)); Assert.Equal(prior, File.ReadAllBytes(priorPath));
            return;
        }
        Assert.True(validation.ExitCode == 0, validation.Error);
        Assert.Equal(markerBefore, File.ReadAllBytes(markerPath)); Assert.Equal(ledgerBefore, File.ReadAllBytes(p.LedgerPath));
        var run = await RunLongFormatSuccessorAsync(f, install, planPath, HashFile(planPath), stopAt, false, false);
        if (stopAt.Length > 0)
        {
            Assert.NotEqual(0, run.ExitCode); Assert.Contains("SyntheticInterruption", run.Error, StringComparison.Ordinal);
            if (!string.Equals(stopAt, "Complete", StringComparison.Ordinal)) Assert.Null(await new InstalledGoogleLongAudioStore(p.App, f.Paths, e.Clock).LoadVerifiedAsync(TestContext.Current.CancellationToken));
            run = await RunLongFormatSuccessorAsync(f, install, planPath, HashFile(planPath), "", false, true);
        }
        Assert.True(run.ExitCode == 0, run.Error);
        var context = await new InstalledGoogleLongAudioStore(p.App, f.Paths, e.Clock).LoadVerifiedAsync(TestContext.Current.CancellationToken); Assert.NotNull(context);
        var ledger = JsonSerializer.Deserialize<GoogleLongAudioBudgetLedger>(File.ReadAllBytes(p.LedgerPath))!;
        Assert.Equal(claim, Assert.Single(ledger.Claims)); Assert.Equal(plan.NewScopeSha256, ledger.ScopeSha256);
        Assert.Equal(parent, File.ReadAllBytes(p.ParentPath)); Assert.Equal(prior, File.ReadAllBytes(priorPath));
        var admission = new GoogleLongAudioLedgerAdmission(p.ParentPath, next.ParentPolicySha256, context.ScopePath, context.ScopeSha256,
            p.LedgerPath, (_, _, _) => throw new InvalidOperationException("No provider readiness in offline test"), e.Clock);
        var review = await admission.ReviewAsync(nextRequest, TestContext.Current.CancellationToken);
        Assert.Equal(1, review.AdditionalCommitmentUsdCents); Assert.False(review.IsAlreadyCommitted);
        // A changed prior financial row cannot be accepted by the exact transition or silently reset.
        File.AppendAllText(p.LedgerPath, " ");
        var changed = await RunLongFormatSuccessorAsync(f, install, planPath, HashFile(planPath), "", false, true);
        Assert.NotEqual(0, changed.ExitCode);
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunLongFormatSuccessorAsync(Fixture f,
        string install, string plan, string sha, string stopAt, bool validate, bool resume, string scriptName = "Update-LongAudioScope.ps1")
    {
        string? root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "scripts", "Update-LongAudioScope.ps1"))) root = Path.GetDirectoryName(root);
        if (root is null) throw new InvalidOperationException("Successor source missing.");
        string wrapper = Path.Combine(f.Root, "synthetic-format-successor.ps1");
        File.WriteAllText(wrapper, """
            param($Repository,$Install,$Profile,$Plan,$Sha,$StopAt,$Validate,$Resume,$ScriptName)
            $hook={param($stage)if($StopAt -and $stage -ceq $StopAt){throw 'SyntheticInterruption'}}.GetNewClosure()
            & (Join-Path $Repository ('scripts/'+$ScriptName)) -InstallRoot $Install -ProfileRoot $Profile -ReviewedPlanPath $Plan -ReviewedPlanSha256 $Sha -ValidateOnly:($Validate -ceq 'true') -Resume:($Resume -ceq 'true') -AfterCheckpoint $hook
            """);
        var start = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string item in new[] { "-NoLogo", "-NoProfile", "-File", wrapper, root, install, f.Root, plan, sha, stopAt, validate ? "true" : "false", resume ? "true" : "false", scriptName }) start.ArgumentList.Add(item);
        using var process = Process.Start(start) ?? throw new IOException("Synthetic successor process unavailable.");
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken); var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        return (process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
    }
}
