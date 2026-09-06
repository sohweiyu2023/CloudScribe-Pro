namespace CloudScribe.Domain.Generation;

public static class VoiceLabCatalog
{
    public static IReadOnlyList<VoiceLabEntry> Search(
        IEnumerable<VoiceLabEntry> entries,
        VoiceLabQuery query,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(query);
        if (query.RequiredCapabilities is null)
        {
            throw new InvalidOperationException("Voice Lab query capability requirements are missing.");
        }

        var validated = entries.Select(entry => entry.Validate(nowUtc)).ToArray();
        var duplicate = validated.GroupBy(entry => entry.StableIdentity, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"Voice Lab catalog contains duplicate stable identity: {duplicate.Key}");
        }

        IEnumerable<VoiceLabEntry> filtered = validated;
        if (query.ExcludeStaleCapabilities)
        {
            filtered = filtered.Where(entry => !entry.IsCapabilityStale(nowUtc));
        }
        if (!string.IsNullOrWhiteSpace(query.ProviderStableId))
        {
            filtered = filtered.Where(entry => string.Equals(entry.ProviderStableId, query.ProviderStableId, StringComparison.Ordinal));
        }
        if (!string.IsNullOrWhiteSpace(query.Language))
        {
            filtered = filtered.Where(entry => string.Equals(entry.Language, query.Language, StringComparison.OrdinalIgnoreCase));
        }
        if (query.RequireAudition)
        {
            filtered = filtered.Where(static entry => entry.AuditionSupported);
        }
        if (query.RequiredCapabilities.Count > 0)
        {
            filtered = filtered.Where(entry => query.RequiredCapabilities.All(required => entry.Capabilities.Contains(required)));
        }
        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            var text = query.Text.Trim();
            filtered = filtered.Where(entry =>
                entry.DisplayName.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                entry.VoiceStableId.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                entry.Language.Contains(text, StringComparison.OrdinalIgnoreCase));
        }

        return filtered
            .OrderBy(static entry => entry.ProviderStableId, StringComparer.Ordinal)
            .ThenBy(static entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static entry => entry.VoiceStableId, StringComparer.Ordinal)
            .ToArray();
    }
}
