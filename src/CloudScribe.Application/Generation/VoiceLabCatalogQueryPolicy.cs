using System.Buffers;

namespace CloudScribe.Application.Generation;

public static class VoiceLabCatalogQueryPolicy
{
    private static readonly SearchValues<char> ForbiddenIdentityCharacters = SearchValues.Create("\r\n\0");

    public static VoiceLabCatalogQuery RequireAuthorized(
        VoiceLabCatalogQuery query,
        bool accountAuthorized,
        bool projectAuthorized,
        bool privateVoiceAccessAuthorized)
    {
        ArgumentNullException.ThrowIfNull(query);
        foreach (var value in new[] { query.ProviderId, query.AccountId, query.ProjectId })
        {
            if (string.IsNullOrWhiteSpace(value) ||
                !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
                value.AsSpan().IndexOfAny(ForbiddenIdentityCharacters) >= 0)
            {
                throw new InvalidOperationException("Voice Lab catalog query contains a non-canonical trust identity.");
            }
        }

        if (!accountAuthorized || !projectAuthorized)
            throw new InvalidOperationException("Voice Lab catalog query is not authorized for the current account/project.");
        if (query.IncludePrivateVoices && !privateVoiceAccessAuthorized)
            throw new InvalidOperationException("Private/custom voices require explicit current access authorization.");
        if (query.SearchText is { Length: > 256 })
            throw new InvalidOperationException("Voice Lab search text exceeds the bounded query length.");
        if (query.Locale is { Length: > 32 })
            throw new InvalidOperationException("Voice Lab locale filter exceeds the bounded length.");
        return query;
    }
}
