using System.Diagnostics;
using System.Text.Json;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Fact]
    public async Task LongAudioBuildPendingMarkerBlocksBothNativeAdmissionReaders()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await ChirpEvidenceAsync(f);
        var p = await PrepareLongInstalledAsync(f, e);
        string path = Path.Combine(Path.GetDirectoryName(p.App)!, "cloudscribe-install.json");
        var marker = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllBytes(path))!;
        marker["LongAudioBuildRebindPending"] = JsonSerializer.SerializeToElement(true);
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(marker));
        Assert.Null(await new InstalledChirpArticleAdmissionStore(p.App, f.Paths, e.Clock).LoadVerifiedAsync(TestContext.Current.CancellationToken));
        var read = await new InstalledGoogleLongAudioStore(p.App, f.Paths, e.Clock).ReadInstallationAsync(TestContext.Current.CancellationToken);
        Assert.Null(read.Context); Assert.Equal(GoogleLongAudioInstallationStatus.PendingProvisioning, read.Status);
    }

    [Theory]
    [InlineData("PreparedBeforeSeal", false)]
    [InlineData("Prepared", false)]
    [InlineData("OldAppArchived", false)]
    [InlineData("AppPublished", false)]
    [InlineData("ParentWritten", false)]
    [InlineData("ScopeWritten", false)]
    [InlineData("LedgerWritten", false)]
    [InlineData("MarkerPublished", false)]
    [InlineData("Complete", false)]
    [InlineData("PreparedBeforeSeal", true)]
    [InlineData("AppPublished", true)]
    [InlineData("LedgerWritten", true)]
    [InlineData("MarkerPublished", true)]
    public async Task LongAudioBuildOnlyRebindResumesOrRollsBackWithoutChangingAuthority(string checkpoint, bool rollback)
    {
        using var f = new Fixture(); using var applicationFiles = new Fixture();
        await f.InitializeAsync(); var e = await ChirpEvidenceAsync(f);
        var document = await f.Library.CreateAsync("Synthetic build-only update", "Exact preserved source.", TestContext.Current.CancellationToken);
        var p = await PrepareLongInstalledAsync(f, e, longDocument: document);
        string oldInstall = Path.GetDirectoryName(p.App)!, install = Path.Combine(applicationFiles.Root, "installation");
        Assert.StartsWith(f.Root + Path.DirectorySeparatorChar, oldInstall, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(applicationFiles.Root + Path.DirectorySeparatorChar, install, StringComparison.OrdinalIgnoreCase);
        Directory.Move(oldInstall, install);
        File.WriteAllBytes(f.Paths.InstanceLockPath, []);
        string markerPath = Path.Combine(install, "cloudscribe-install.json");
        var marker = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllBytes(markerPath))!;
        marker["Profile"] = JsonSerializer.SerializeToElement(f.Root); File.WriteAllBytes(markerPath, JsonSerializer.SerializeToUtf8Bytes(marker));
        File.WriteAllBytes(p.LedgerPath, JsonSerializer.SerializeToUtf8Bytes(new GoogleLongAudioBudgetLedger(1, p.ScopeSha,
            [new(p.Request.AttemptId, p.Request.BindingSha256, 1)])));
        SealSyntheticHistoricalPlan(install, p.LedgerPath, p.ScopeSha, File.ReadAllBytes(Path.Combine(install, "google-long-audio-scope.json")));
        string operations = Path.Combine(p.Root, "operations"); Directory.CreateDirectory(operations);
        File.WriteAllBytes(Path.Combine(operations, p.Request.AttemptId.ToString("N") + ".json"), JsonSerializer.SerializeToUtf8Bytes(new
        {
            p.Request.AttemptId, p.Request.BindingSha256, State = 3, OperationParseFailed = false,
            Observation = new { ReadbackVerified = true, ReadState = 1, Truncated = false }
        }));
        string? repository = AppContext.BaseDirectory;
        while (repository is not null && !File.Exists(Path.Combine(repository, "tools", "long-audio", "BuildRebind.Transaction.ps1"))) repository = Path.GetDirectoryName(repository);
        Assert.NotNull(repository);
        string script = Path.Combine(repository, "tests", "CloudScribe.Infrastructure.Tests", "LongAudioBuildRebind.Synthetic.ps1");
        var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string value in new[] { "-NoLogo", "-NoProfile", "-File", script, repository, install, f.Root, checkpoint, rollback.ToString() }) start.ArgumentList.Add(value);
        using var process = Process.Start(start) ?? throw new IOException("Synthetic build-rebind child did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        string output = await stdout, error = await stderr;
        Assert.True(process.ExitCode == 0, output + error);
        Assert.Contains("SyntheticBuildRebindVerified", output, StringComparison.Ordinal);
        Assert.Single(JsonSerializer.Deserialize<GoogleLongAudioBudgetLedger>(File.ReadAllBytes(p.LedgerPath))!.Claims);
    }
}
