using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    private sealed partial class InstalledAdditiveFixture
    {
        public void Rebind(Action<Dictionary<string, object?>>? planChange = null)
        {
            Assert.NotNull(Head); Assert.NotNull(HeadSha);
            var previous = Head;
            string previousParent = InstalledBytesHash(File.ReadAllBytes(Parent));
            string previousMarker = InstalledBytesHash(File.ReadAllBytes(Marker));
            WriteBuild("inert synthetic updated application", "inert synthetic updated infrastructure");
            string scope = Hash("synthetic rebound long-audio scope"); WriteLegacy(scope);
            Guid transaction = Guid.NewGuid();
            string approval = Pin("source-approvals", Encoding.UTF8.GetBytes("synthetic build-only approval"), "bin");
            var plan = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["Version"] = 3,
                ["TransactionId"] = transaction.ToString("D"),
                ["SourceApprovalSha256"] = approval,
                ["PreviousParentSha256"] = previousParent,
                ["NewParentSha256"] = InstalledBytesHash(File.ReadAllBytes(Parent)),
                ["PreviousMarkerSha256"] = previousMarker,
                ["NewScopeSha256"] = scope,
                ["PackageAppTreeSha256"] = Hash("synthetic rebound package"),
                ["ProfileTreeSha256"] = Hash("synthetic preserved profile"),
                ["DeferredEvidenceSha256"] = Hash("synthetic preserved deferred evidence"),
                ["AdditiveRegistrySha256"] = HeadSha,
                ["AdditiveSidecarTreeSha256"] = Hash("synthetic preserved sidecar"),
                ["PreviousLegacyPolicyIdentitySha256"] = previous.LegacyPolicySha256,
                ["NewLegacyPolicyIdentitySha256"] = ChirpPolicyValidation.Hash(Legacy)
            };
            planChange?.Invoke(plan);
            var proof = new Dictionary<string, object?>(StringComparer.Ordinal)
            { ["Version"] = 1, ["Passed"] = true, ["ProviderCalls"] = 0, ["LiveWrites"] = 0 };
            foreach (string name in new[] { "PreviousParentSha256", "NewParentSha256", "PackageAppTreeSha256", "ProfileTreeSha256", "DeferredEvidenceSha256",
                "AdditiveRegistrySha256", "AdditiveSidecarTreeSha256", "PreviousLegacyPolicyIdentitySha256", "NewLegacyPolicyIdentitySha256" }) proof[name] = plan[name];
            string proofSha = Pin("native-proofs", JsonSerializer.SerializeToUtf8Bytes(proof)); plan["NativeProofSha256"] = proofSha;
            string planSha = Pin("plans", JsonSerializer.SerializeToUtf8Bytes(plan));
            var receipt = new InstalledAdditiveReceipt(1, transaction, "Rebind", planSha, HeadSha, null, null,
                Legacy.ProfileRootSha256, Legacy.AppSha256, Legacy.InfrastructureSha256, InstalledBytesHash(File.ReadAllBytes(Marker)),
                ChirpPolicyValidation.Hash(Legacy), approval, proofSha, "Complete");
            string receiptSha = Pin("receipts", JsonSerializer.SerializeToUtf8Bytes(receipt));
            Publish(new(1, ChirpPolicyValidation.Hash(Legacy), previous.Families, receiptSha));
            LastReceipt = receipt; LastPlan = plan; LastProof = proof;
        }
    }

    private static async Task<ChirpAdditionalAdmissionFamily> InstalledSiblingAsync(Fixture f, ChirpAdditionalAdmissionFamily original)
    {
        var first = await AdditionalDocumentAsync(f, "en-US-Neural2-D", 2).ConfigureAwait(false);
        var second = await AdditionalDocumentAsync(f, "en-US-Chirp3-HD-Aoede", 2).ConfigureAwait(false);
        var articles = new[] { first.Article, second.Article };
        var policy = original.Policy with { Articles = articles, WholeArticleGeneration = new(Guid.NewGuid(), articles.Select(x => x.Proposal.PlanSha256).ToArray()) };
        return original with
        {
            FamilyId = Guid.NewGuid(),
            Policy = policy,
            Requests = new[] { first, second }.SelectMany(x => x.Article.Proposal.Parts.Select(p =>
        {
            var intent = FullDocumentIntent(x, p.CoverageOrdinal);
            return new ChirpAdditionalRequestPin(x.Article.Proposal.PlanSha256, p.CoverageOrdinal, intent.Envelope, intent.EstimateMinorUnits);
        })).ToArray()
        };
    }

    private static byte[] MutateInstalledGrant(byte[] bytes, string fault)
    {
        string text = Encoding.UTF8.GetString(bytes);
        if (fault is "duplicate-field") return Encoding.UTF8.GetBytes("{\"FamilyId\":\"00000000-0000-0000-0000-000000000001\"," + text[1..]);
        if (fault is "unknown-field") return Encoding.UTF8.GetBytes("{\"UncheckedAllowance\":1000000," + text[1..]);
        var grant = JsonNode.Parse(bytes)!.AsObject();
        switch (fault)
        {
            case "null-policy": grant["Policy"] = null; break;
            case "null-requests": grant["Requests"] = null; break;
            case "null-request": grant["Requests"]!.AsArray()[0] = null; break;
            case "null-article": grant["Policy"]!["Articles"]!.AsArray()[0] = null; break;
            case "null-family": return Encoding.UTF8.GetBytes("null");
            default: throw new ArgumentException("Unknown synthetic grant fault.", nameof(fault));
        }
        return JsonSerializer.SerializeToUtf8Bytes(grant);
    }
}
