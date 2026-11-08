using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    private static async Task<InstalledAdditiveFixture> InstalledAdditiveAsync(Fixture f)
    {
        var evidence = await AdditiveEvidenceAsync(f, 2).ConfigureAwait(false);
        var result = new InstalledAdditiveFixture(f, evidence);
        result.WriteBuild("inert synthetic application", "inert synthetic infrastructure");
        result.WriteLegacy();
        return result;
    }

    private sealed partial class InstalledAdditiveFixture(Fixture fixture, AdditiveEvidence evidence)
    {
        public AdditiveEvidence Evidence { get; } = evidence;
        public string Install { get; } = Path.Combine(fixture.Root, "synthetic-additive-install");
        public string App => Path.Combine(Install, "app");
        public string Root => Path.Combine(Install, "chirp-article-admissions");
        public string Marker => Path.Combine(Install, "cloudscribe-install.json");
        public string Parent => Path.Combine(Install, "chirp-article-admission-policy.json");
        public string RegistryPath => Path.Combine(Root, "registry.json");
        public ChirpArticleAdmissionPolicy Legacy { get; set; } = evidence.Legacy.Admission.Policy!;
        public InstalledAdditiveRegistry? Head { get; private set; }
        public string? HeadSha { get; private set; }
        public InstalledAdditiveReceipt? LastReceipt { get; private set; }
        public Dictionary<string, object?>? LastPlan { get; private set; }
        public Dictionary<string, object?>? LastProof { get; private set; }
        public InstalledChirpArticleAdmissionCatalogStore Store() => new(App, fixture.Paths, Evidence.Legacy.Clock);
        public ChirpArticleService Service(IChirpArticleAdmissionCatalog store) => new(fixture, store, fixture.Paths, Evidence.Legacy.Clock);

        public void WriteBuild(string app, string infrastructure)
        {
            Directory.CreateDirectory(App);
            File.WriteAllText(Path.Combine(App, "CloudScribe.dll"), app);
            File.WriteAllText(Path.Combine(App, "CloudScribe.Infrastructure.dll"), infrastructure);
            Legacy = Legacy with { AppSha256 = Hash(app), InfrastructureSha256 = Hash(infrastructure) };
        }

        public void WriteLegacy(string? scope = null)
        {
            File.WriteAllBytes(Parent, JsonSerializer.SerializeToUtf8Bytes(Legacy));
            File.WriteAllBytes(Marker, JsonSerializer.SerializeToUtf8Bytes(new
            {
                Product = "CloudScribe Pro",
                Profile = fixture.Root,
                Legacy.AppSha256,
                ChirpArticlePolicySha256 = InstalledBytesHash(File.ReadAllBytes(Parent)),
                GoogleLongAudioScopeSha256 = scope ?? Hash("synthetic retained long-audio scope")
            }));
        }

        public ChirpAdditionalAdmissionFamily Append(ChirpAdditionalAdmissionFamily template, string source = "synthetic first approval",
            Func<ChirpAdditionalAdmissionFamily, ChirpAdditionalAdmissionFamily>? change = null, string? rawFault = null,
            Action<Dictionary<string, object?>>? proofChange = null)
        {
            string approval = Pin("source-approvals", Encoding.UTF8.GetBytes(source), "bin");
            var family = template with
            {
                Policy = template.Policy with
                { AppSha256 = Legacy.AppSha256, InfrastructureSha256 = Legacy.InfrastructureSha256, SourceApprovalSha256 = approval }
            };
            if (change is not null) family = change(family);
            byte[] grant = JsonSerializer.SerializeToUtf8Bytes(family);
            if (rawFault is not null) grant = MutateInstalledGrant(grant, rawFault);
            string grantSha = Pin("grants", grant);
            Guid transaction = Guid.NewGuid();
            var common = CommonProofPins(grantSha, approval);
            var proof = new Dictionary<string, object?>(common, StringComparer.Ordinal)
            {
                ["SchemaVersion"] = 1,
                ["Passed"] = true,
                ["FamilyId"] = family.FamilyId.ToString("D"),
                ["ReviewedBaselineSha256"] = family.ReviewedBaselineSha256,
                ["FamilyPolicySha256"] = ChirpPolicyValidation.Hash(family.Policy),
                ["AllInCeilingMinorUnits"] = family.AllInCeilingMinorUnits,
                ["ProvisionMinorUnits"] = family.ProvisionMinorUnits,
                ["ProviderCalls"] = 0,
                ["LiveWrites"] = 0
            };
            proofChange?.Invoke(proof);
            string proofSha = Pin("native-proofs", JsonSerializer.SerializeToUtf8Bytes(proof));
            var plan = new Dictionary<string, object?>(common, StringComparer.Ordinal)
            {
                ["SchemaVersion"] = 1,
                ["TransactionId"] = transaction.ToString("D"),
                ["InstallRoot"] = Install,
                ["ProfileRoot"] = fixture.Root,
                ["PreviousRegistrySha256"] = HeadSha,
                ["PreviousSidecarTreeSha256"] = HeadSha is null ? null : Hash("synthetic previous sidecar"),
                ["SourceApprovalPath"] = PinPath("source-approvals", approval, "bin"),
                ["GrantPath"] = PinPath("grants", grantSha),
                ["NativeProofPath"] = PinPath("native-proofs", proofSha),
                ["NativeProofSha256"] = proofSha
            };
            string planSha = Pin("plans", JsonSerializer.SerializeToUtf8Bytes(plan));
            var receipt = new InstalledAdditiveReceipt(1, transaction, "Admit", planSha, HeadSha, family.FamilyId, grantSha,
                Legacy.ProfileRootSha256, Legacy.AppSha256, Legacy.InfrastructureSha256, InstalledBytesHash(File.ReadAllBytes(Marker)),
                ChirpPolicyValidation.Hash(Legacy), approval, proofSha, "Complete");
            string receiptSha = Pin("receipts", JsonSerializer.SerializeToUtf8Bytes(receipt));
            Publish(new(1, ChirpPolicyValidation.Hash(Legacy), (Head?.Families ?? []).Append(new(family.FamilyId, grantSha, receiptSha)).ToArray(), receiptSha));
            string directory = Path.Combine(Root, "transactions", transaction.ToString("N")); Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, "state.json"), JsonSerializer.SerializeToUtf8Bytes(new
            { SchemaVersion = 1, TransactionId = transaction, PlanSha256 = planSha, Phase = "Complete" }));
            LastReceipt = receipt; LastPlan = plan; LastProof = proof;
            return family;
        }

        private Dictionary<string, object?> CommonProofPins(string grantSha, string approval) => new(StringComparer.Ordinal)
        {
            ["AppSha256"] = Legacy.AppSha256,
            ["InfrastructureSha256"] = Legacy.InfrastructureSha256,
            ["ProfileRootSha256"] = Legacy.ProfileRootSha256,
            ["InstallMarkerSha256"] = InstalledBytesHash(File.ReadAllBytes(Marker)),
            ["LegacyPolicySha256"] = ChirpPolicyValidation.Hash(Legacy),
            ["LegacyPolicyFileSha256"] = InstalledBytesHash(File.ReadAllBytes(Parent)),
            ["LegacyScopeSha256"] = Hash("synthetic retained long-audio scope"),
            ["LegacyLedgerSha256"] = Hash("synthetic retained ledger"),
            ["ProfileTreeSha256"] = Hash("synthetic reviewed profile"),
            ["RetainedEvidenceTreeSha256"] = Hash("synthetic retained evidence"),
            ["GrantSha256"] = grantSha,
            ["SourceApprovalSha256"] = approval
        };

        public string Pin(string folder, byte[] bytes, string extension = "json")
        {
            string hash = InstalledBytesHash(bytes), path = PinPath(folder, hash, extension);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, bytes); return hash;
        }

        public string PinPath(string folder, string hash, string extension = "json") => Path.Combine(Root, folder, hash + "." + extension);
        public void Publish(InstalledAdditiveRegistry registry)
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(registry);
            HeadSha = Pin("registries", bytes); Head = registry; File.WriteAllBytes(RegistryPath, bytes);
        }
    }

    private static string InstalledBytesHash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private sealed record InstalledAdditiveRegistry(int SchemaVersion, string LegacyPolicySha256, IReadOnlyList<InstalledAdditiveEntry> Families, string BindingReceiptSha256);
    private sealed record InstalledAdditiveEntry(Guid FamilyId, string GrantSha256, string ReceiptSha256);
    private sealed record InstalledAdditiveReceipt(int SchemaVersion, Guid TransactionId, string Operation, string PlanSha256, string? PreviousRegistrySha256,
        Guid? FamilyId, string? GrantSha256, string ProfileRootSha256, string AppSha256, string InfrastructureSha256, string InstallMarkerSha256,
        string LegacyPolicySha256, string SourceApprovalSha256, string NativeProofSha256, string Phase);
}
