namespace CloudScribe.Infrastructure.Generation;

public sealed partial class InstalledChirpArticleAdmissionCatalogStore
{
    private static async Task<Receipt> VerifyHistoryAsync(string root, Registry head, string headHash, string? observed, CancellationToken token)
    {
        var current = head; string hash = headHash; Receipt? latest = null;
        HashSet<string> visited = new(StringComparer.Ordinal);
        Dictionary<Guid, (string Plan, string Operation)> transactions = [];
        while (true)
        {
            RequireRegistry(current);
            if (!visited.Add(hash) || visited.Count > 256) throw new InvalidDataException("The admission history is cyclic or exceeds its inspection bound.");
            _ = await ReadPinnedAsync(root, "registries", hash, token).ConfigureAwait(false);
            var receipt = await ReadReceiptAsync(root, current.BindingReceiptSha256, token).ConfigureAwait(false);
            latest ??= receipt;
            if (!transactions.TryAdd(receipt.TransactionId, (receipt.PlanSha256, receipt.Operation)) || !string.Equals(receipt.LegacyPolicySha256, current.LegacyPolicySha256, StringComparison.Ordinal)
                || !string.Equals(latest.ProfileRootSha256, receipt.ProfileRootSha256, StringComparison.Ordinal))
                throw new InvalidDataException("The admission receipt chain changed ownership or profile.");
            await VerifyProofAsync(root, receipt, null, token).ConfigureAwait(false);
            var previous = receipt.PreviousRegistrySha256 is null ? null : ChirpPolicyFiles.Parse<Registry>(
                await ReadPinnedAsync(root, "registries", receipt.PreviousRegistrySha256, token).ConfigureAwait(false));
            if (string.Equals(receipt.Operation, "Rebind", StringComparison.Ordinal))
            {
                using var plan = System.Text.Json.JsonDocument.Parse(await ReadPinnedAsync(root, "plans", receipt.PlanSha256, token).ConfigureAwait(false));
                RequireString(plan.RootElement, "PreviousLegacyPolicyIdentitySha256", previous?.LegacyPolicySha256);
            }
            RequireTransition(current, previous, receipt);
            if (previous is null)
            {
                if (observed is not null && !visited.Contains(observed)) throw new InvalidDataException("Previously observed admission history was removed.");
                await RequireTransactionHistoryAsync(root, transactions, token).ConfigureAwait(false);
                return latest;
            }
            current = previous; hash = receipt.PreviousRegistrySha256!;
        }
    }

    private static void RequireRegistry(Registry registry)
    {
        if (registry.SchemaVersion != 1 || !ChirpPolicyValidation.IsHash(registry.LegacyPolicySha256)
            || !ChirpPolicyValidation.IsHash(registry.BindingReceiptSha256) || registry.Families is not { Count: > 0 and <= 32 }
            || registry.Families.Any(x => x.FamilyId == Guid.Empty || !ChirpPolicyValidation.IsHash(x.GrantSha256) || !ChirpPolicyValidation.IsHash(x.ReceiptSha256))
            || registry.Families.Select(x => x.FamilyId).Distinct().Count() != registry.Families.Count
            || registry.Families.Select(x => x.GrantSha256).Distinct(StringComparer.Ordinal).Count() != registry.Families.Count)
            throw new InvalidDataException("The admission registry is incomplete or ambiguous.");
    }

    private static void RequireTransition(Registry current, Registry? previous, Receipt receipt)
    {
        if (previous is not null) RequireRegistry(previous);
        if (string.Equals(receipt.Operation, "Admit", StringComparison.Ordinal))
        {
            var added = new Entry(receipt.FamilyId!.Value, receipt.GrantSha256!, current.BindingReceiptSha256);
            if (!current.Families.SequenceEqual((previous?.Families ?? []).Append(added))
                || previous is not null && !string.Equals(current.LegacyPolicySha256, previous.LegacyPolicySha256, StringComparison.Ordinal))
                throw new InvalidDataException("An admission removed, reset or changed an earlier family.");
        }
        else if (previous is null || !current.Families.SequenceEqual(previous.Families))
            throw new InvalidDataException("A build-only binding changed admission or retained allowance history.");
    }

    private static async Task<Receipt> ReadReceiptAsync(string root, string hash, CancellationToken token)
    {
        var r = ChirpPolicyFiles.Parse<Receipt>(await ReadPinnedAsync(root, "receipts", hash, token).ConfigureAwait(false));
        if (r.SchemaVersion != 1 || r.TransactionId == Guid.Empty || !string.Equals(r.Phase, "Complete", StringComparison.Ordinal)
            || r.Operation is not ("Admit" or "Rebind") || new[] { r.PlanSha256, r.ProfileRootSha256, r.AppSha256, r.InfrastructureSha256,
                r.InstallMarkerSha256, r.LegacyPolicySha256, r.SourceApprovalSha256, r.NativeProofSha256 }.Any(x => !ChirpPolicyValidation.IsHash(x))
            || r.PreviousRegistrySha256 is not null && !ChirpPolicyValidation.IsHash(r.PreviousRegistrySha256)
            || string.Equals(r.Operation, "Admit", StringComparison.Ordinal) && (r.FamilyId is null || r.FamilyId == Guid.Empty || !ChirpPolicyValidation.IsHash(r.GrantSha256))
            || string.Equals(r.Operation, "Rebind", StringComparison.Ordinal) && (r.FamilyId is not null || r.GrantSha256 is not null || r.PreviousRegistrySha256 is null))
            throw new InvalidDataException("A complete admission transaction receipt is required.");
        return r;
    }
}
