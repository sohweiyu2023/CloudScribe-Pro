namespace CloudScribe.Infrastructure.Generation;

public sealed partial class InstalledChirpArticleAdmissionCatalogStore
{
    private static async Task RequireUnpublishedAsync(string root, string? observed, CancellationToken token)
    {
        if (observed is not null) throw new InvalidDataException("A previously admitted registry disappeared.");
        if (!Directory.Exists(root)) return;
        string transactions = Path.Combine(root, "transactions");
        ChirpPolicyFiles.RejectLinks(transactions);
        if (!Directory.Exists(transactions)) throw new InvalidDataException("Admission sidecar history is incomplete.");
        var directories = Directory.GetDirectories(transactions);
        if (directories.Length == 0) throw new InvalidDataException("Admission publication history is absent.");
        foreach (string directory in directories)
        {
            var state = await ReadTransactionStateAsync(directory, token).ConfigureAwait(false);
            if (!string.Equals(state.Phase, "RolledBack", StringComparison.Ordinal))
                throw new InvalidDataException("A published or pending admission cannot fall back to legacy-only authority.");
        }
    }

    private static async Task RequireTransactionHistoryAsync(string root, Dictionary<Guid, (string Plan, string Operation)> expected, CancellationToken token)
    {
        string folder = Path.Combine(root, "transactions");
        ChirpPolicyFiles.RejectLinks(folder);
        HashSet<Guid> found = [];
        foreach (string directory in Directory.GetDirectories(folder))
        {
            var state = await ReadTransactionStateAsync(directory, token).ConfigureAwait(false);
            if (!found.Add(state.TransactionId)) throw new InvalidDataException("Admission transaction ownership is duplicated.");
            if (expected.TryGetValue(state.TransactionId, out var entry))
            {
                if (!string.Equals(state.PlanSha256, entry.Plan, StringComparison.Ordinal) || state.Phase is not ("ReceiptSealed" or "RegistryPublished" or "Complete"))
                    throw new InvalidDataException("An admitted receipt lacks its exact durable publication history.");
            }
            else if (!string.Equals(state.Phase, "RolledBack", StringComparison.Ordinal))
                throw new InvalidDataException("The selected registry omits a retained or pending admission transaction.");
        }
        if (expected.Any(x => string.Equals(x.Value.Operation, "Admit", StringComparison.Ordinal) && !found.Contains(x.Key)))
            throw new InvalidDataException("An admitted family's publication history disappeared.");
    }

    private static async Task<TransactionState> ReadTransactionStateAsync(string directory, CancellationToken token)
    {
        var state = ChirpPolicyFiles.Parse<TransactionState>(await ChirpPolicyFiles.ReadAsync(Path.Combine(directory, "state.json"), 16384, token).ConfigureAwait(false));
        if (state.SchemaVersion != 1 || state.TransactionId == Guid.Empty || !ChirpPolicyValidation.IsHash(state.PlanSha256)
            || !string.Equals(Path.GetFileName(directory), state.TransactionId.ToString("N"), StringComparison.Ordinal)
            || state.Phase is not ("Owned" or "PlanSealed" or "InputsSealed" or "ReceiptSealed" or "RegistryPublished" or "Complete" or "RolledBack"))
            throw new InvalidDataException("An admission transaction state is malformed.");
        return state;
    }

    private sealed record TransactionState(int SchemaVersion, Guid TransactionId, string PlanSha256, string Phase);
}
