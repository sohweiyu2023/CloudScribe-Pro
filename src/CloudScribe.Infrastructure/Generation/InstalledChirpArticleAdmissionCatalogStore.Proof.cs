using System.Text.Json;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class InstalledChirpArticleAdmissionCatalogStore
{
    private static async Task<ChirpAdditionalAdmissionFamily> ReadFamilyAsync(string root, Entry entry, Receipt binding, CancellationToken token)
    {
        var grant = ChirpPolicyFiles.Parse<ChirpAdditionalAdmissionFamily>(await ReadPinnedAsync(root, "grants", entry.GrantSha256, token).ConfigureAwait(false));
        var receipt = await ReadReceiptAsync(root, entry.ReceiptSha256, token).ConfigureAwait(false);
        if (grant.FamilyId != entry.FamilyId || receipt.FamilyId != entry.FamilyId || !string.Equals(receipt.Operation, "Admit", StringComparison.Ordinal)
            || !string.Equals(receipt.GrantSha256, entry.GrantSha256, StringComparison.Ordinal)
            || !string.Equals(grant.Policy.ProfileRootSha256, binding.ProfileRootSha256, StringComparison.Ordinal)
            || !string.Equals(grant.Policy.AppSha256, receipt.AppSha256, StringComparison.Ordinal)
            || !string.Equals(grant.Policy.InfrastructureSha256, receipt.InfrastructureSha256, StringComparison.Ordinal)
            || !string.Equals(grant.Policy.SourceApprovalSha256, receipt.SourceApprovalSha256, StringComparison.Ordinal))
            throw new InvalidDataException("An immutable family differs from its completed installation receipt.");
        await VerifyProofAsync(root, receipt, grant, token).ConfigureAwait(false);
        return grant; // Original policy bytes/identity survive build-only rebinds.
    }

    private static async Task VerifyProofAsync(string root, Receipt receipt, ChirpAdditionalAdmissionFamily? family, CancellationToken token)
    {
        using var plan = JsonDocument.Parse(await ReadPinnedAsync(root, "plans", receipt.PlanSha256, token).ConfigureAwait(false));
        using var proof = JsonDocument.Parse(await ReadPinnedAsync(root, "native-proofs", receipt.NativeProofSha256, token).ConfigureAwait(false));
        _ = await ReadPinnedAsync(root, "source-approvals", receipt.SourceApprovalSha256, token, "bin").ConfigureAwait(false);
        var p = plan.RootElement; var n = proof.RootElement;
        ChirpPolicyFiles.RequireUnique(p); ChirpPolicyFiles.RequireUnique(n);
        RequireString(p, "TransactionId", receipt.TransactionId.ToString("D"));
        RequireString(p, "SourceApprovalSha256", receipt.SourceApprovalSha256);
        RequireString(p, "NativeProofSha256", receipt.NativeProofSha256);
        if (!n.GetProperty("Passed").GetBoolean() || n.GetProperty("ProviderCalls").GetInt64() != 0 || n.GetProperty("LiveWrites").GetInt64() != 0)
            throw new InvalidDataException("A passing zero-provider native proof is required.");
        if (string.Equals(receipt.Operation, "Rebind", StringComparison.Ordinal))
        {
            RequireRebindProof(p, n, receipt); return;
        }
        if (p.GetProperty("SchemaVersion").GetInt32() != 1 || n.GetProperty("SchemaVersion").GetInt32() != 1)
            throw new InvalidDataException("Unsupported independent admission proof.");
        RequireString(p, "PreviousRegistrySha256", receipt.PreviousRegistrySha256);
        foreach (var field in new[] { ("ProfileRootSha256", receipt.ProfileRootSha256), ("AppSha256", receipt.AppSha256),
            ("InfrastructureSha256", receipt.InfrastructureSha256), ("InstallMarkerSha256", receipt.InstallMarkerSha256),
            ("LegacyPolicySha256", receipt.LegacyPolicySha256), ("GrantSha256", receipt.GrantSha256), ("SourceApprovalSha256", receipt.SourceApprovalSha256) })
        { RequireString(p, field.Item1, field.Item2); RequireString(n, field.Item1, field.Item2); }
        foreach (string field in new[] { "LegacyPolicyFileSha256", "LegacyScopeSha256", "LegacyLedgerSha256", "ProfileTreeSha256", "RetainedEvidenceTreeSha256" })
        {
            string value = p.GetProperty(field).GetString()!;
            if (!ChirpPolicyValidation.IsHash(value)) throw new InvalidDataException("The reviewed retained baseline is incomplete.");
            RequireString(n, field, value);
        }
        RequireString(n, "FamilyId", receipt.FamilyId!.Value.ToString("D"));
        if (family is null) return;
        RequireString(n, "ReviewedBaselineSha256", family.ReviewedBaselineSha256);
        RequireString(n, "FamilyPolicySha256", ChirpPolicyValidation.Hash(family.Policy));
        if (n.GetProperty("AllInCeilingMinorUnits").GetInt64() != family.AllInCeilingMinorUnits
            || n.GetProperty("ProvisionMinorUnits").GetInt64() != family.ProvisionMinorUnits)
            throw new InvalidDataException("The native proof changed the independent family allowance.");
    }

    private static void RequireRebindProof(JsonElement plan, JsonElement native, Receipt receipt)
    {
        if (plan.GetProperty("Version").GetInt32() != 3 || native.GetProperty("Version").GetInt32() != 1)
            throw new InvalidDataException("Only an explicitly reviewed additive-aware build transition can rebind a catalog.");
        RequireString(plan, "AdditiveRegistrySha256", receipt.PreviousRegistrySha256);
        RequireString(plan, "NewLegacyPolicyIdentitySha256", receipt.LegacyPolicySha256);
        foreach (string field in new[] { "PreviousParentSha256", "NewParentSha256", "PackageAppTreeSha256", "ProfileTreeSha256", "DeferredEvidenceSha256",
            "AdditiveRegistrySha256", "AdditiveSidecarTreeSha256", "PreviousLegacyPolicyIdentitySha256", "NewLegacyPolicyIdentitySha256" })
        {
            string value = plan.GetProperty(field).GetString()!;
            if (!ChirpPolicyValidation.IsHash(value)) throw new InvalidDataException("A build transition proof pin is invalid.");
            RequireString(native, field, value);
        }
    }

    private static void RequireString(JsonElement value, string property, string? expected)
    {
        if (!string.Equals(value.GetProperty(property).GetString(), expected, StringComparison.Ordinal))
            throw new InvalidDataException("An independent admission proof pin changed.");
    }
}
