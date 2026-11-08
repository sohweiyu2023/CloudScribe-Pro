using System.Text.Json;

namespace CloudScribe.Infrastructure.Generation;

public static class ChirpArticleAdmissionLookup
{
    public static async Task<ChirpArticleAdmissionPolicy?> FindAsync(IChirpArticleAdmissionStore store,
        Guid? documentId = null, string? policySha256 = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (store is not IChirpArticleAdmissionCatalog catalogStore)
        {
            var policy = await store.LoadVerifiedAsync(token).ConfigureAwait(false);
            return Matches(policy, documentId, policySha256) ? policy : null;
        }
        var catalog = await catalogStore.LoadCatalogVerifiedAsync(token).ConfigureAwait(false);
        if (documentId is null && policySha256 is null) return catalog.LegacyPolicy;
        var policies = catalog.Families.Select(x => x.Policy).Concat(catalog.LegacyPolicy is { } legacy ? [legacy] : []);
        return policies.SingleOrDefault(x => Matches(x, documentId, policySha256));
    }

    internal static ChirpArticleAdmissionCatalog Own(ChirpArticleAdmissionCatalog catalog) =>
        ChirpPolicyFiles.Parse<ChirpArticleAdmissionCatalog>(JsonSerializer.SerializeToUtf8Bytes(catalog));

    private static bool Matches(ChirpArticleAdmissionPolicy? policy, Guid? document, string? hash) => policy is not null
        && (document is null || policy.Articles.Any(x => x.Proposal.DocumentId == document))
        && (hash is null || string.Equals(ChirpPolicyValidation.Hash(policy), hash, StringComparison.Ordinal));
}
