using Microsoft.Data.Sqlite;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class ChirpArticleService
{
    // Called only with the same transaction that owns reservation/claim publication. Validate every
    // sibling before excluding it. The legacy digest and historical resume mappings remain unchanged.
    private static async Task<CurrentFinancial> ReadFinancialUnionAsync(SqliteConnection connection, SqliteTransaction transaction,
        ChirpArticleAdmissionPolicy selected, ChirpArticleAdmissionCatalog catalog, RecoveryFinancialSnapshot all,
        IReadOnlyList<object?[]> roots, IReadOnlyList<object?[]> successors, IReadOnlyList<object?[]> splits, CancellationToken token)
    {
        var owned = roots.Concat(successors).Concat(splits).ToArray();
        RequireUniqueOwnership(all, owned);
        string selectedHash = ChirpPolicyValidation.Hash(selected);
        HashSet<string> familyIds = new(StringComparer.Ordinal), familyQueueIdentities = new(StringComparer.Ordinal);
        CurrentFinancial? result = null;
        long retained = 0;
        foreach (var family in catalog.Families)
        {
            string hash = ChirpPolicyValidation.Hash(family.Policy);
            var rows = roots.Where(x => Equals(x[3], hash)).ToArray();
            if (successors.Concat(splits).Any(x => Equals(x[3], hash)))
                throw new InvalidOperationException("Fresh additional families cannot own successor or recovery attempts.");
            foreach (var row in rows)
            {
                ValidateAttempt(family.Policy, row, all.QueueStates, false);
                RequireAdditionalRequest(family, (string)row[1]!, checked((int)(long)row[2]!),
                    ParseStored<GoogleGenerationSubmissionEnvelope>((string)row[9]!) with { ChirpAuthorizationId = null }, (long)row[12]!);
                if (!familyIds.Add((string)row[0]!)) throw new InvalidOperationException("A reservation belongs to multiple additional families.");
                familyQueueIdentities.Add(RecoveryFinancialSnapshot.Identity([row[5], GoogleGenerationProvider.SynthesizeOperationStableId, row[7]], 3));
            }
            var keys = rows.Select(x => (string)x[7]!).ToHashSet(StringComparer.Ordinal);
            var snapshot = new RecoveryFinancialSnapshot(
                all.Queue.Where(x => Equals(x[0], family.Policy.AccountId) && Equals(x[1], GoogleGenerationProvider.SynthesizeOperationStableId) && keys.Contains((string)x[2]!)).ToArray(),
                [], [], [], all.QueueStates.Where(x => string.Equals(x.Account, family.Policy.AccountId, StringComparison.Ordinal)
                    && string.Equals(x.Operation, GoogleGenerationProvider.SynthesizeOperationStableId, StringComparison.Ordinal) && keys.Contains(x.Key)).ToArray(), []);
            var financial = await EvaluateFinancialAsync(connection, transaction, family.Policy, snapshot, rows, [], [], token, family).ConfigureAwait(false);
            if (string.Equals(family.Policy.AccountId, selected.AccountId, StringComparison.Ordinal)
                && string.Equals(family.Policy.ProjectId, selected.ProjectId, StringComparison.Ordinal)) retained = checked(retained + financial.ProjectCommitted);
            if (string.Equals(hash, selectedHash, StringComparison.Ordinal)) result = financial;
        }
        var legacySnapshot = all with
        {
            Queue = all.Queue.Where(x => !familyQueueIdentities.Contains(RecoveryFinancialSnapshot.Identity(x, 3))).ToArray(),
            QueueStates = all.QueueStates.Where(x => !familyQueueIdentities.Contains(RecoveryFinancialSnapshot.Identity([x.Account, x.Operation, x.Key], 3))).ToArray()
        };
        var legacyRoots = roots.Where(x => !familyIds.Contains((string)x[0]!)).ToArray();
        // An explicit historical reserved-resume policy still validates its original view. It cannot
        // acquire additive authority because additive families resolve only by their exact policy hash.
        var legacyPolicy = result is null ? selected : catalog.LegacyPolicy;
        if (legacyPolicy is null)
        {
            if (legacyRoots.Length != 0 || successors.Count != 0 || splits.Count != 0 || legacySnapshot.Queue.Count != 0
                || all.Spend.Count != 0 || all.Recovery.Count != 0 || all.Scopes.Count != 0 || all.Transitions.Count != 0
                || (await ChirpArticleSql.ReadAsync(connection, transaction, "chirp_article_legacy_associations", token).ConfigureAwait(false)).Count != 0)
                throw new InvalidOperationException("Historical financial evidence has no independently admitted owner.");
        }
        else
        {
            var historical = await EvaluateFinancialAsync(connection, transaction, legacyPolicy, legacySnapshot, legacyRoots, successors, splits, token).ConfigureAwait(false);
            if (string.Equals(legacyPolicy.AccountId, selected.AccountId, StringComparison.Ordinal)
                && string.Equals(legacyPolicy.ProjectId, selected.ProjectId, StringComparison.Ordinal)) retained = checked(retained + historical.ProjectCommitted);
            result ??= historical;
        }
        return result is null ? throw new InvalidOperationException("No exact policy owns the requested financial view.")
            : result with { AggregateCommitted = retained };
    }

    private static void RequireUniqueOwnership(RecoveryFinancialSnapshot all, object?[][] owned)
    {
        if (owned.Any(x => x.Length < 18 || x[0] is not string || x[3] is not string || x[7] is not string)
            || owned.Select(x => (string)x[0]!).Distinct(StringComparer.Ordinal).Count() != owned.Length
            || owned.Select(x => (string)x[7]!).Distinct(StringComparer.Ordinal).Count() != owned.Length
            || all.Queue.Select(x => RecoveryFinancialSnapshot.Identity(x, 3)).Distinct(StringComparer.Ordinal).Count() != all.Queue.Count
            || all.QueueStates.Select(x => (x.Account, x.Operation, x.Key)).Distinct().Count() != all.QueueStates.Count)
            throw new InvalidOperationException("The financial union contains duplicate or malformed ownership.");
    }

    private static void RequireAdditionalRequest(ChirpAdditionalAdmissionFamily? family, string plan, int ordinal,
        GoogleGenerationSubmissionEnvelope envelope, long estimate)
    {
        if (family is null) return;
        var pin = family.Requests.SingleOrDefault(x => string.Equals(x.PlanSha256, plan, StringComparison.Ordinal) && x.CoverageOrdinal == ordinal);
        if (pin is null || pin.Envelope != envelope || pin.EstimateMinorUnits != estimate)
            throw new InvalidOperationException("Additional family credential, capability, pricing, revision, payload or estimate changed.");
    }
}
