using CloudScribe.Domain.Generation;

namespace CloudScribe.Application.Generation;

public sealed class VoiceLabCatalogQueryService
{
    private const int MaxCatalogResults = 500;
    private readonly Func<VoiceLabCatalogQuery, CancellationToken, Task<IReadOnlyList<VoiceLabCatalogSelection>>> _queryAsync;

    public VoiceLabCatalogQueryService(
        Func<VoiceLabCatalogQuery, CancellationToken, Task<IReadOnlyList<VoiceLabCatalogSelection>>> queryAsync)
    {
        _queryAsync = queryAsync ?? throw new ArgumentNullException(nameof(queryAsync));
    }

    public async Task<IReadOnlyList<VoiceLabCatalogSelection>> QueryAsync(
        VoiceLabCatalogQuery query,
        bool accountAuthorized,
        bool projectAuthorized,
        bool privateVoiceAccessAuthorized,
        CancellationToken cancellationToken = default)
    {
        var admitted = VoiceLabCatalogQueryPolicy.RequireAuthorized(
            query,
            accountAuthorized,
            projectAuthorized,
            privateVoiceAccessAuthorized);

        var results = await _queryAsync(admitted, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Voice Lab catalog transport returned no result collection.");

        if (results.Count > MaxCatalogResults)
        {
            throw new InvalidOperationException(
                $"Voice Lab catalog transport returned {results.Count} results; the bounded maximum is {MaxCatalogResults}.");
        }

        var seenVoiceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var selection in results)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VoiceLabCatalogSelectionPolicy.RequireEligible(selection);
            if (!string.Equals(selection.ProviderStableId, admitted.ProviderId, StringComparison.Ordinal) ||
                !string.Equals(selection.AccountStableId, admitted.AccountId, StringComparison.Ordinal) ||
                !string.Equals(selection.ProjectStableId, admitted.ProjectId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Voice Lab catalog transport returned a selection outside the admitted provider/account/project trust boundary.");
            }

            if (!seenVoiceIds.Add(selection.VoiceStableId))
            {
                throw new InvalidOperationException(
                    "Voice Lab catalog transport returned duplicate voice identities; ambiguous catalog results must be reconciled before display or audition.");
            }
        }

        return results;
    }
}
