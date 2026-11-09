using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudScribe.Application.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Architecture.Tests;

public sealed partial class ShellViewModelStateTests
{
    private sealed record CatalogReopenInstallation(InstalledChirpArticleAdmissionCatalogStore Store,
        string MarkerPath, byte[] MarkerBefore, string LedgerPath, byte[] LedgerBefore, string FamilySha256);

    private sealed partial class CatalogReopenFixture
    {
        internal async Task<CatalogReopenInstallation> InstallAdmissionAsync(string temporaryRoot, IReadOnlyList<CatalogReopenArticle> articles)
        {
            // All authority here is deliberately synthetic and confined to the two disposable test roots.
            string install = Path.Combine(temporaryRoot, "installation"), app = Path.Combine(install, "app");
            string inputs = Path.Combine(temporaryRoot, "reviewed");
            PrepareInertApplication(app, inputs);
            var document = await Disk.NewLibrary().CreateAsync("Retained synthetic Chinese article", "Synthetic retained Chinese narration source.", TestContext.Current.CancellationToken).ConfigureAwait(false);
            var proposal = ChirpArticlePlanCompiler.Compile(document, "cmn-CN", "cmn-CN-Chirp3-HD-Alnilam");
            var history = new[] { new ChirpHistoricalRange(document.Id, document.CurrentRevisionId!.Value, 0, proposal.SourceScalars, proposal.SourceSha256, []) };
            var article = new AdmittedChirpArticle(proposal, ChirpPlanLineageMapper.Map(document.Text, proposal.Parts.Select(x => x.Range).ToArray(), history), [], [new(1, 1)]);
            var legacy = new ChirpArticleAdmissionPolicy(3, Hash("inert synthetic application"), Hash("inert synthetic infrastructure"),
                InstalledRecoveryAdmissionStore.ProfileHash(Disk.Paths.RootDirectory), Account, Project, "USD", 2, Clock.GetUtcNow().AddHours(1),
                Hash("Synthetic historical source approval"), new(553, 714, 0, 100, Hash("Synthetic retained baseline"), [], [], [], []), [], [], [article], 1, 1,
                PriorSuccessorAttemptRows: [], WholeArticleGeneration: new(Guid.NewGuid(), [proposal.PlanSha256]));
            ChirpPolicyValidation.RequireValid(legacy, Clock.GetUtcNow());
            byte[] parent = JsonSerializer.SerializeToUtf8Bytes(legacy); string parentSha = Hash(parent);
            File.WriteAllBytes(Path.Combine(install, "chirp-article-admission-policy.json"), parent);
            var request = CreateRetainedRequest(document.Text);
            var job = new GoogleLongAudioDocumentJob(document.Id, document.CurrentRevisionId.Value, proposal.SourceSha256,
                request.AttemptId, request.OutputGcsUri, "text", 4096, 24000, 1);
            var scope = new GoogleLongAudioBudgetScope(1, parentSha, Hash("Synthetic historical source approval"), request.ProjectNumber, request.CredentialReferenceId,
                request.VoiceName, request.LanguageCode, request.AudioEncoding, "synthetic-private", 1000, 200, 30m, "Synthetic pricing fixture",
                new Dictionary<string, long>(StringComparer.Ordinal) { [request.BindingSha256] = 1 }, [job]);
            byte[] scopeBytes = JsonSerializer.SerializeToUtf8Bytes(scope); string scopeSha = Hash(scopeBytes);
            File.WriteAllBytes(Path.Combine(install, "google-long-audio-scope.json"), scopeBytes);
            string markerPath = Path.Combine(install, "cloudscribe-install.json");
            var marker = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Product"] = "CloudScribe Pro",
                ["Profile"] = Disk.Paths.RootDirectory,
                ["AppSha256"] = legacy.AppSha256,
                ["ChirpArticlePolicySha256"] = parentSha,
                ["GoogleLongAudioScopeSha256"] = scopeSha
            };
            byte[] markerBytes = JsonSerializer.SerializeToUtf8Bytes(marker); File.WriteAllBytes(markerPath, markerBytes);
            string data = Path.Combine(Disk.Paths.RootDirectory, "data", "google-long-audio"); Directory.CreateDirectory(data);
            string ledgerPath = Path.Combine(data, "budget-ledger.json");
            byte[] ledgerBytes = JsonSerializer.SerializeToUtf8Bytes(new GoogleLongAudioBudgetLedger(1, scopeSha, [new(request.AttemptId, request.BindingSha256, 1)]));
            File.WriteAllBytes(ledgerPath, ledgerBytes);
            SealRetainedInstallation(install, legacy, marker, parentSha, scopeSha, scopeBytes, ledgerBytes);
            WriteRetainedOperation(data, request);
            var family = CreateAdditionalFamily(legacy, articles, inputs);
            WriteValidatedAdmissionInputs(inputs, legacy, family);
            string script = Path.Combine(temporaryRoot, "synthetic-catalog-reopen-admission.ps1"); File.WriteAllText(script, CatalogReopenAdmissionScript);
            await RunAdmissionScriptAsync(script, install, Disk.Paths.RootDirectory, inputs, Disk.Paths.DatabasePath).ConfigureAwait(false);
            var store = new InstalledChirpArticleAdmissionCatalogStore(app, Disk.Paths, Clock);
            var loaded = await store.LoadCatalogVerifiedAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
            Assert.Equal(ChirpPolicyValidation.Hash(family), ChirpPolicyValidation.Hash(Assert.Single(loaded.Families)));
            Assert.Equal(parent, File.ReadAllBytes(Path.Combine(install, "chirp-article-admission-policy.json")));
            Assert.Equal(scopeBytes, File.ReadAllBytes(Path.Combine(install, "google-long-audio-scope.json")));
            return new(store, markerPath, markerBytes, ledgerPath, ledgerBytes, ChirpPolicyValidation.Hash(family));
        }

        private void WriteValidatedAdmissionInputs(string inputs, ChirpArticleAdmissionPolicy legacy, ChirpAdditionalAdmissionFamily family)
        {
            ChirpAdditionalAdmissionValidation.RequireValid(new ChirpArticleAdmissionCatalog(legacy, [family]), Clock.GetUtcNow());
            File.WriteAllBytes(Path.Combine(inputs, "grant.json"), JsonSerializer.SerializeToUtf8Bytes(family));
            File.WriteAllBytes(Path.Combine(inputs, "identities.json"), JsonSerializer.SerializeToUtf8Bytes(new
            { LegacyPolicySha256 = ChirpPolicyValidation.Hash(legacy), FamilyPolicySha256 = ChirpPolicyValidation.Hash(family.Policy) }));
        }

        private void PrepareInertApplication(string app, string inputs)
        {
            Directory.CreateDirectory(app); Directory.CreateDirectory(inputs);
            File.WriteAllText(Path.Combine(app, "CloudScribe.dll"), "inert synthetic application");
            File.WriteAllText(Path.Combine(app, "CloudScribe.Infrastructure.dll"), "inert synthetic infrastructure");
            File.WriteAllBytes(Path.Combine(app, "appsettings.json"), JsonSerializer.SerializeToUtf8Bytes(new { CloudScribe = new { AppDataDirectoryOverride = Disk.Paths.RootDirectory } }));
        }

        private static GoogleLongAudioRequest CreateRetainedRequest(string text)
        {
            Guid attempt = Guid.NewGuid();
            return GoogleLongAudioRequest.Create(attempt, "123456", "global", "synthetic-retained-key", "gs://synthetic-private/" + attempt.ToString("N") + ".mp3",
                text, "cmn-CN", "cmn-CN-Chirp3-HD-Alnilam", "MP3");
        }

        private void WriteRetainedOperation(string data, GoogleLongAudioRequest request)
        {
            string operations = Path.Combine(data, "operations"); Directory.CreateDirectory(operations);
            File.WriteAllBytes(Path.Combine(operations, request.AttemptId.ToString("N") + ".json"), JsonSerializer.SerializeToUtf8Bytes(new
            {
                request.AttemptId,
                request.BindingSha256,
                State = 3,
                OperationParseFailed = false,
                Observation = new { ReadbackVerified = true, ReadState = 1, Truncated = false }
            }));
            File.WriteAllBytes(Disk.Paths.InstanceLockPath, []);
        }

        private static ChirpAdditionalAdmissionFamily CreateAdditionalFamily(ChirpArticleAdmissionPolicy legacy,
            IReadOnlyList<CatalogReopenArticle> articles, string inputs)
        {
            const string approval = "SYNTHETIC TWO-VOICE ADMISSION TEST ONLY; NEVER LIVE AUTHORITY";
            File.WriteAllText(Path.Combine(inputs, "source.txt"), approval);
            long total = articles.Sum(x => x.Pending.Sum(p => p.CurrentEstimateMinorUnits));
            var policy = legacy with
            {
                SourceApprovalSha256 = Hash(approval),
                Budget = new(20, 100, 20, 100, Hash("Synthetic independent baseline"), [], [], [], []),
                Articles = articles.Select(x => x.Article).ToArray(),
                MaximumNewAttempts = articles.Sum(x => x.Pending.Count),
                MaximumAdditionalReservationMinorUnits = total,
                WholeArticleGeneration = new(Guid.NewGuid(), articles.Select(x => x.Article.Proposal.PlanSha256).ToArray())
            };
            var pins = articles.SelectMany(x => x.Pending.Select((p, i) => new ChirpAdditionalRequestPin(x.Article.Proposal.PlanSha256, i + 1,
                p.Envelope, p.CurrentEstimateMinorUnits))).ToArray();
            return new(Guid.NewGuid(), policy, 100, 20, policy.Budget.ReviewedEvidenceSha256, pins);
        }

        private static void SealRetainedInstallation(string install, ChirpArticleAdmissionPolicy legacy, Dictionary<string, string> marker,
            string parentSha, string scopeSha, byte[] scope, byte[] ledger)
        {
            string root = Path.Combine(install, "long-audio-provisioning"); Directory.CreateDirectory(root);
            byte[] after = JsonSerializer.SerializeToUtf8Bytes(marker);
            var previous = new Dictionary<string, string>(marker, StringComparer.Ordinal); previous.Remove("GoogleLongAudioScopeSha256");
            byte[] before = JsonSerializer.SerializeToUtf8Bytes(previous);
            var plan = new GoogleLongAudioProvisioningPlan(1, Guid.NewGuid(), scopeSha, parentSha, legacy.ProfileRootSha256,
                legacy.AppSha256, legacy.InfrastructureSha256, Hash(before), Hash(after), Hash(ledger), DateTimeOffset.UtcNow);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(plan);
            File.WriteAllBytes(Path.Combine(root, "plan.json"), bytes);
            File.WriteAllBytes(Path.Combine(root, "receipt.json"), JsonSerializer.SerializeToUtf8Bytes(new GoogleLongAudioProvisioningReceipt(1, plan.TransactionId, Hash(bytes), "Complete")));
            File.WriteAllBytes(Path.Combine(root, "scope.json"), scope); File.WriteAllBytes(Path.Combine(root, "ledger-initial.json"), ledger);
            File.WriteAllBytes(Path.Combine(root, "marker-before.json"), before); File.WriteAllBytes(Path.Combine(root, "marker-after.json"), after);
            File.WriteAllBytes(Path.Combine(install, "long-audio-provisioning.lock"), []);
        }

        private static async Task RunAdmissionScriptAsync(string script, string install, string profile, string inputs, string databasePath)
        {
            // Closing the synthetic application releases pooled SQLite handles before the installer snapshot.
            using var pooled = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            { DataSource = databasePath, ForeignKeys = true }.ConnectionString);
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(pooled);
            string? repository = AppContext.BaseDirectory;
            while (repository is not null && !File.Exists(Path.Combine(repository, "tools", "article-admission", "Admission.Transaction.ps1"))) repository = Path.GetDirectoryName(repository);
            Assert.NotNull(repository);
            var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (string value in new[] { "-NoLogo", "-NoProfile", "-File", script, repository, install, profile, inputs }) start.ArgumentList.Add(value);
            using var process = Process.Start(start) ?? throw new IOException("Synthetic admission child did not start.");
            var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
            string combined = await output.ConfigureAwait(false) + await error.ConfigureAwait(false);
            Assert.True(process.ExitCode == 0, combined);
            Assert.Contains("SyntheticCatalogReopenAdmissionVerified", combined, StringComparison.Ordinal);
        }

        private static string Hash(string value) => Hash(Encoding.UTF8.GetBytes(value));
        private static string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
    }

    private const string CatalogReopenAdmissionScript = """
        param($Repository,$Install,$Profile,$Inputs)
        $ErrorActionPreference='Stop'
        . (Join-Path $Repository 'scripts/Install-CloudScribe.ps1') -CompletionFunctionsOnly
        foreach($name in @('Provisioning.Files.ps1','Provisioning.Scope.ps1','Provisioning.Transaction.ps1','BuildRebind.Validation.ps1','BuildRebind.Transaction.ps1')){
            . (Join-Path $Repository ('tools/long-audio/'+$name))
        }
        foreach($name in @('Admission.Validation.ps1','Admission.Transaction.ps1','BuildRebind.ps1')){
            . (Join-Path $Repository ('tools/article-admission/'+$name))
        }
        $parentPath=Join-Path $Install 'chirp-article-admission-policy.json'
        $scopePath=Join-Path $Install 'google-long-audio-scope.json'
        $ledgerPath=Join-Path $Profile 'data/google-long-audio/budget-ledger.json'
        $markerPath=Join-Path $Install 'cloudscribe-install.json'
        $parent=ConvertFrom-LAJson (Read-LABytes $parentPath)
        $identities=ConvertFrom-LAJson (Read-LABytes (Join-Path $Inputs 'identities.json'))
        $grantPath=Join-Path $Inputs 'grant.json';$family=ConvertFrom-LAJson (Read-LABytes $grantPath)
        $sourcePath=Join-Path $Inputs 'source.txt'
        $plan=[ordered]@{SchemaVersion=1;TransactionId=[Guid]::NewGuid().ToString('D');InstallRoot=$Install;ProfileRoot=$Profile;
            ProfileRootSha256=$parent.ProfileRootSha256;AppSha256=$parent.AppSha256;InfrastructureSha256=$parent.InfrastructureSha256;
            SourceApprovalPath=$sourcePath;SourceApprovalSha256=(Get-LAHash (Read-LABytes $sourcePath));
            InstallMarkerSha256=(Get-LAHash (Read-LABytes $markerPath));LegacyPolicySha256=$identities.LegacyPolicySha256;
            LegacyPolicyFileSha256=(Get-LAHash (Read-LABytes $parentPath));LegacyScopeSha256=(Get-LAHash (Read-LABytes $scopePath));
            LegacyLedgerSha256=(Get-LAHash (Read-LABytes $ledgerPath));ProfileTreeSha256=(Get-LABuildTreeHash $Profile);
            RetainedEvidenceTreeSha256=(Get-LABuildTreeHash (Join-Path $Profile 'data/google-long-audio'));
            PreviousRegistrySha256=$null;PreviousSidecarTreeSha256=$null;GrantPath=$grantPath;GrantSha256=(Get-LAHash (Read-LABytes $grantPath));
            NativeProofPath=(Join-Path $Inputs 'synthetic-native-proof.json');NativeProofSha256=('0'*64)}
        $native=[ordered]@{SchemaVersion=1;Passed=$true}
        foreach($key in @('AppSha256','InfrastructureSha256','ProfileRootSha256','InstallMarkerSha256','LegacyPolicySha256','LegacyPolicyFileSha256',
            'LegacyScopeSha256','LegacyLedgerSha256','ProfileTreeSha256','RetainedEvidenceTreeSha256','GrantSha256','SourceApprovalSha256')){$native[$key]=$plan[$key]}
        $native.FamilyId=$family.FamilyId;$native.ReviewedBaselineSha256=$family.ReviewedBaselineSha256
        $native.FamilyPolicySha256=$identities.FamilyPolicySha256;$native.AllInCeilingMinorUnits=$family.AllInCeilingMinorUnits
        $native.ProvisionMinorUnits=$family.ProvisionMinorUnits;$native.ProviderCalls=0;$native.LiveWrites=0
        [IO.File]::WriteAllBytes($plan.NativeProofPath,(ConvertTo-LABytes $native));$plan.NativeProofSha256=Get-LAHash (Read-LABytes $plan.NativeProofPath)
        $planPath=Join-Path $Inputs 'admit-plan.json';[IO.File]::WriteAllBytes($planPath,(ConvertTo-LABytes $plan))
        $result=Invoke-AAAdmission $Install $Profile $planPath (Get-LAHash (Read-LABytes $planPath)) 'Install'
        if(-not $result.Admitted -or $result.ProviderCalls -ne 0){throw 'SyntheticAdmissionDidNotComplete'}
        Write-Output 'SyntheticCatalogReopenAdmissionVerified'
        """;
}
