namespace CloudScribe.Application.Generation;

public sealed class VoiceLabCatalogQueryService
{
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

        foreach (var selection in results)
        {
            ArgumentNullException.ThrowIfNull(selection);
            if (!string.Equals(selection.ProviderId, admitted.ProviderId, StringComparison.Ordinal) ||
                !string.Equals(selection.AccountId, admitted.AccountId, StringComparison.Ordinal) ||
                !string.Equals(selection.ProjectId, admitted.ProjectId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Voice Lab catalog transport returned a selection outside the admitted provider/account/project trust boundary.");
            }
        }

        return results;
    }
}
