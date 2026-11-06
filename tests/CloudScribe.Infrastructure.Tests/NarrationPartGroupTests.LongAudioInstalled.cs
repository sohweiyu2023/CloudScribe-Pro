using System.Text.Json;
using CloudScribe.Application.Documents;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("missing-pin")]
    [InlineData("scope-changed")]
    [InlineData("wrong-ledger-scope")]
    [InlineData("missing-ledger")]
    [InlineData("changed-binary")]
    [InlineData("changed-parent")]
    [InlineData("duplicate-ledger")]
    [InlineData("missing-proof")]
    [InlineData("pending-proof")]
    [InlineData("changed-proof-plan")]
    public async Task LongAudioInstalledContextUsesCanonicalHistoryAndRequiresAllPins(string fault)
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await ChirpEvidenceAsync(f);
        var p = await PrepareLongInstalledAsync(f, e, fault);
        string app = p.App, parentPath = p.ParentPath, scopePath = p.ScopePath, ledger = p.LedgerPath,
            root = p.Root, scopeSha = p.ScopeSha;
        ApplyLongInstalledFault(fault, scopePath, ledger, app, parentPath);
        byte[]? before = File.Exists(ledger) ? File.ReadAllBytes(ledger) : null;
        var context = await new InstalledGoogleLongAudioStore(app, f.Paths, e.Clock).LoadVerifiedAsync(TestContext.Current.CancellationToken);
        if (fault is "valid")
        {
            Assert.NotNull(context); Assert.Equal(ledger, context.LedgerPath);
            Assert.Equal(Path.Combine(root, "operations"), context.OperationsDirectory);
            Assert.False(Directory.Exists(context.OperationsDirectory)); // Discovery creates no runtime state.
            Assert.Equal(scopeSha, context.ScopeSha256);
        }
        else Assert.Null(context);
        if (before is null) Assert.False(File.Exists(ledger));
        else Assert.Equal(before, File.ReadAllBytes(ledger));
    }

    private static async Task<(string App, string ParentPath, string ScopePath, string LedgerPath, string Root, string ScopeSha,
        GoogleLongAudioRequest Request)> PrepareLongInstalledAsync(Fixture f, ChirpEvidence e, string fault = "valid", DocumentSnapshot? longDocument = null)
    {
        string install = Path.Combine(f.Root, "synthetic-long-install"), app = Path.Combine(install, "app"); Directory.CreateDirectory(app);
        await File.WriteAllTextAsync(Path.Combine(app, "CloudScribe.dll"), "inert synthetic application", TestContext.Current.CancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(app, "CloudScribe.Infrastructure.dll"), "inert synthetic infrastructure", TestContext.Current.CancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(app, "appsettings.json"), JsonSerializer.Serialize(new { CloudScribe = new { AppDataDirectoryOverride = f.Root } }), TestContext.Current.CancellationToken).ConfigureAwait(false);
        var parent = e.Admission.Policy! with { AppSha256 = Hash("inert synthetic application"), InfrastructureSha256 = Hash("inert synthetic infrastructure") };
        var p = await LongLedgerFixtureAsync(f, parent, e.Clock).ConfigureAwait(false);
        string parentPath = Path.Combine(install, "chirp-article-admission-policy.json"), scopePath = Path.Combine(install, "google-long-audio-scope.json");
        File.Copy(p.ParentPath, parentPath); File.Copy(p.ScopePath, scopePath);
        var request = p.Request;
        if (longDocument is not null)
        {
            var scope = JsonSerializer.Deserialize<GoogleLongAudioBudgetScope>(File.ReadAllBytes(scopePath))!;
            request = GoogleLongAudioRequest.Create(p.Request.AttemptId, scope.ProjectNumber, "global", scope.CredentialReferenceId,
                p.Request.OutputGcsUri, longDocument.Text, scope.LanguageCode, scope.VoiceName, scope.AudioEncoding);
            var job = new GoogleLongAudioDocumentJob(longDocument.Id, longDocument.CurrentRevisionId!.Value, Hash(longDocument.Text), request.AttemptId,
                request.OutputGcsUri, "text", 4096, 24000, 1);
            scope = scope with { ApprovedRequestCeilingsUsdCents = new Dictionary<string, long>(StringComparer.Ordinal) { [request.BindingSha256] = 1 }, DocumentJobs = [job] };
            File.WriteAllBytes(scopePath, JsonSerializer.SerializeToUtf8Bytes(scope));
        }
        string parentSha = Hash(File.ReadAllText(parentPath)), scopeSha = Hash(File.ReadAllText(scopePath));
        var marker = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Product"] = "CloudScribe Pro", ["AppSha256"] = parent.AppSha256,
            ["ChirpArticlePolicySha256"] = parentSha, ["GoogleLongAudioScopeSha256"] = scopeSha
        };
        if (fault is "missing-pin") marker.Remove("GoogleLongAudioScopeSha256");
        await File.WriteAllTextAsync(Path.Combine(install, "cloudscribe-install.json"), JsonSerializer.Serialize(marker), TestContext.Current.CancellationToken).ConfigureAwait(false);
        string root = Path.Combine(f.Paths.RootDirectory, "data", "google-long-audio"); Directory.CreateDirectory(root);
        string ledger = Path.Combine(root, "budget-ledger.json"); File.WriteAllBytes(ledger, JsonSerializer.SerializeToUtf8Bytes(new GoogleLongAudioBudgetLedger(1, scopeSha, [])));
        WriteSyntheticLongProvisioningProof(install, parent, parentSha, scopeSha, ledger, e.Clock.GetUtcNow());
        return (app, parentPath, scopePath, ledger, root, scopeSha, request);
    }

    private static void ApplyLongInstalledFault(string fault, string scopePath, string ledger, string app, string parentPath)
    {
        switch (fault)
        {
            case "scope-changed": File.AppendAllText(scopePath, " "); break;
            case "wrong-ledger-scope": File.WriteAllText(ledger, JsonSerializer.Serialize(new GoogleLongAudioBudgetLedger(1, new string('c', 64), []))); break;
            case "missing-ledger": File.Delete(ledger); break;
            case "changed-binary": File.AppendAllText(Path.Combine(app, "CloudScribe.dll"), "changed"); break;
            case "changed-parent": File.AppendAllText(parentPath, " "); break;
            case "duplicate-ledger": File.WriteAllText(ledger, "{\"Version\":1,\"Version\":1}"); break;
            case "missing-proof": File.Delete(Path.Combine(Path.GetDirectoryName(app)!, "long-audio-provisioning", "receipt.json")); break;
            case "pending-proof":
                string receipt = Path.Combine(Path.GetDirectoryName(app)!, "long-audio-provisioning", "receipt.json");
                File.WriteAllText(receipt, File.ReadAllText(receipt).Replace("Complete", "MarkerPublished", StringComparison.Ordinal)); break;
            case "changed-proof-plan": File.AppendAllText(Path.Combine(Path.GetDirectoryName(app)!, "long-audio-provisioning", "plan.json"), " "); break;
        }
    }

    private static void WriteSyntheticLongProvisioningProof(string install, ChirpArticleAdmissionPolicy parent, string parentSha,
        string scopeSha, string ledger, DateTimeOffset now)
    {
        string transaction = Path.Combine(install, "long-audio-provisioning"); Directory.CreateDirectory(transaction);
        File.WriteAllBytes(Path.Combine(install, "long-audio-provisioning.lock"), []);
        var plan = new GoogleLongAudioProvisioningPlan(1, Guid.NewGuid(), scopeSha, parentSha, parent.ProfileRootSha256,
            parent.AppSha256, parent.InfrastructureSha256, new string('0', 64), Hash(File.ReadAllText(Path.Combine(install, "cloudscribe-install.json"))),
            Hash(File.ReadAllText(ledger)), now);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(plan); File.WriteAllBytes(Path.Combine(transaction, "plan.json"), bytes);
        File.WriteAllBytes(Path.Combine(transaction, "receipt.json"), JsonSerializer.SerializeToUtf8Bytes(new GoogleLongAudioProvisioningReceipt(1,
            plan.TransactionId, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), "Complete")));
    }
}
