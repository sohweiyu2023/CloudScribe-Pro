using System.Diagnostics;
using System.Text.Json;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Fact]
    public async Task AdditiveInstalledPowerShellAdmissionAndBuildRebindLoadWithCanonicalTypedPolicyIdentities()
    {
        using var f = new Fixture(); using var applicationFiles = new Fixture(); await f.InitializeAsync();
        var e = await AdditiveEvidenceAsync(f, 2);
        var prepared = await PrepareInstalledScriptInstallationAsync(f, applicationFiles, e);
        string install = prepared.Install;
        string inputs = Path.Combine(applicationFiles.Root, "reviewed"); Directory.CreateDirectory(inputs);
        const string approval = "SYNTHETIC ADMISSION APPROVAL ONLY; NEVER LIVE AUTHORITY";
        File.WriteAllText(Path.Combine(inputs, "source.txt"), approval);
        var legacy = JsonSerializer.Deserialize<ChirpArticleAdmissionPolicy>(File.ReadAllBytes(Path.Combine(install, "chirp-article-admission-policy.json")))!;
        var original = Assert.Single(e.Store.Catalog.Families);
        var family = original with
        {
            Policy = original.Policy with
            { AppSha256 = legacy.AppSha256, InfrastructureSha256 = legacy.InfrastructureSha256, SourceApprovalSha256 = Hash(approval) }
        };
        File.WriteAllBytes(Path.Combine(inputs, "grant.json"), JsonSerializer.SerializeToUtf8Bytes(family));
        File.WriteAllBytes(Path.Combine(inputs, "identities.json"), JsonSerializer.SerializeToUtf8Bytes(new
        { LegacyPolicySha256 = ChirpPolicyValidation.Hash(legacy), FamilyPolicySha256 = ChirpPolicyValidation.Hash(family.Policy) }));
        string? repository = AppContext.BaseDirectory;
        while (repository is not null && !File.Exists(Path.Combine(repository, "tools", "article-admission", "Admission.Transaction.ps1"))) repository = Path.GetDirectoryName(repository);
        Assert.NotNull(repository);
        string script = Path.Combine(applicationFiles.Root, "synthetic-installed-catalog.ps1"); File.WriteAllText(script, InstalledCatalogBridgeScript);
        await RunInstalledCatalogBridgeAsync(script, repository, install, f.Root, inputs, "Admit");
        string app = Path.Combine(install, "app"), sidecar = Path.Combine(install, "chirp-article-admissions");
        var reader = new InstalledChirpArticleAdmissionCatalogStore(app, f.Paths, e.Legacy.Clock);
        var admitted = await reader.LoadCatalogVerifiedAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ChirpPolicyValidation.Hash(family), ChirpPolicyValidation.Hash(Assert.Single(admitted.Families)));
        string[] grantsBefore = InstalledTreeHashes(Path.Combine(sidecar, "grants"));
        var rebound = PrepareInstalledScriptRebind(install, inputs, legacy);
        await RunInstalledCatalogBridgeAsync(script, repository, install, f.Root, inputs, "Rebind");
        foreach (var store in new[] { reader, new InstalledChirpArticleAdmissionCatalogStore(app, f.Paths, e.Legacy.Clock) })
        {
            var loaded = await store.LoadCatalogVerifiedAsync(TestContext.Current.CancellationToken);
            Assert.Equal(ChirpPolicyValidation.Hash(rebound), ChirpPolicyValidation.Hash(loaded.LegacyPolicy));
            Assert.Equal(ChirpPolicyValidation.Hash(family), ChirpPolicyValidation.Hash(Assert.Single(loaded.Families)));
        }
        Assert.Equal(grantsBefore, InstalledTreeHashes(Path.Combine(sidecar, "grants")));
        Assert.Single(JsonSerializer.Deserialize<GoogleLongAudioBudgetLedger>(File.ReadAllBytes(prepared.LedgerPath))!.Claims);
        Assert.Empty(await FinancialRowsAsync(f, "chirp_article_attempts"));
    }

    private static async Task<(string Install, string LedgerPath)> PrepareInstalledScriptInstallationAsync(Fixture f, Fixture applicationFiles, AdditiveEvidence e)
    {
        var document = await f.Library.CreateAsync("Synthetic retained long-audio source", "Exact synthetic retained source.", TestContext.Current.CancellationToken).ConfigureAwait(false);
        var old = await PrepareLongInstalledAsync(f, e.Legacy, longDocument: document).ConfigureAwait(false);
        string oldInstall = Path.GetDirectoryName(old.App)!, install = Path.Combine(applicationFiles.Root, "installation");
        Assert.StartsWith(f.Root + Path.DirectorySeparatorChar, oldInstall, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(applicationFiles.Root + Path.DirectorySeparatorChar, install, StringComparison.OrdinalIgnoreCase);
        Directory.Move(oldInstall, install);
        File.WriteAllBytes(f.Paths.InstanceLockPath, []);
        string markerPath = Path.Combine(install, "cloudscribe-install.json");
        var marker = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllBytes(markerPath))!;
        marker["Profile"] = JsonSerializer.SerializeToElement(f.Root); File.WriteAllBytes(markerPath, JsonSerializer.SerializeToUtf8Bytes(marker));
        File.WriteAllBytes(old.LedgerPath, JsonSerializer.SerializeToUtf8Bytes(new GoogleLongAudioBudgetLedger(1, old.ScopeSha,
            [new(old.Request.AttemptId, old.Request.BindingSha256, 1)])));
        SealSyntheticHistoricalPlan(install, old.LedgerPath, old.ScopeSha, File.ReadAllBytes(Path.Combine(install, "google-long-audio-scope.json")));
        string operations = Path.Combine(old.Root, "operations"); Directory.CreateDirectory(operations);
        File.WriteAllBytes(Path.Combine(operations, old.Request.AttemptId.ToString("N") + ".json"), JsonSerializer.SerializeToUtf8Bytes(new
        {
            old.Request.AttemptId,
            old.Request.BindingSha256,
            State = 3,
            OperationParseFailed = false,
            Observation = new { ReadbackVerified = true, ReadState = 1, Truncated = false }
        }));
        return (install, old.LedgerPath);
    }

    private static ChirpArticleAdmissionPolicy PrepareInstalledScriptRebind(string install, string inputs, ChirpArticleAdmissionPolicy legacy)
    {
        string package = Path.Combine(inputs, "package", "app"); Directory.CreateDirectory(package);
        const string app = "synthetic updated bridge application", infrastructure = "synthetic updated bridge infrastructure",
            approval = "SYNTHETIC BUILD APPROVAL ONLY; NEVER LIVE AUTHORITY";
        File.WriteAllText(Path.Combine(package, "CloudScribe.dll"), app); File.WriteAllText(Path.Combine(package, "CloudScribe.Infrastructure.dll"), infrastructure);
        File.WriteAllText(Path.Combine(package, "appsettings.json"), "{}"); File.WriteAllText(Path.Combine(inputs, "build-source.txt"), approval);
        var next = legacy with { AppSha256 = Hash(app), InfrastructureSha256 = Hash(infrastructure), SourceApprovalSha256 = Hash(approval) };
        byte[] parent = JsonSerializer.SerializeToUtf8Bytes(next); File.WriteAllBytes(Path.Combine(inputs, "new-parent.json"), parent);
        var scope = JsonSerializer.Deserialize<GoogleLongAudioBudgetScope>(File.ReadAllBytes(Path.Combine(install, "google-long-audio-scope.json")))!;
        File.WriteAllBytes(Path.Combine(inputs, "new-scope.json"), JsonSerializer.SerializeToUtf8Bytes(scope with
        { ParentPolicySha256 = InstalledBytesHash(parent), SourceApprovalSha256 = Hash(approval) }));
        File.WriteAllText(Path.Combine(inputs, "new-legacy-identity.txt"), ChirpPolicyValidation.Hash(next));
        return next;
    }

    private static async Task RunInstalledCatalogBridgeAsync(string script, string repository, string install, string profile, string inputs, string phase)
    {
        var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string value in new[] { "-NoLogo", "-NoProfile", "-File", script, repository, install, profile, inputs, phase }) start.ArgumentList.Add(value);
        using var process = Process.Start(start) ?? throw new IOException("Synthetic installed-catalog child did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        string output = await stdout.ConfigureAwait(false), error = await stderr.ConfigureAwait(false);
        Assert.True(process.ExitCode == 0, output + error);
        Assert.Contains("SyntheticInstalledCatalogBridgeVerified", output, StringComparison.Ordinal);
    }
}
