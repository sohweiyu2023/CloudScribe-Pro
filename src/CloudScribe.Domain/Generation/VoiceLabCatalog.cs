namespace CloudScribe.Domain.Generation;

public sealed record VoiceLabEntry(
    string ProviderStableId,
    string AccountId,
    string VoiceStableId,
    string DisplayName,
    string Language,
    IReadOnlySet<string> Capabilities,
    string PricingProvenanceId,
    string CapabilityProvenanceId,
    DateTimeOffset CapabilityObservedAtUtc,
    DateTimeOffset CapabilityExpiresAtUtc,
    bool AuditionSupported)
{
    public VoiceLabEntry Validate(DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ProviderStableId);
        ArgumentException.ThrowIfNullOrWhiteSpace(AccountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(VoiceStableId);
        ArgumentException.ThrowIfNullOrWhiteSpace(DisplayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(Language);
        ArgumentNullException.ThrowIfNull(Capabilities);
        ArgumentException.ThrowIfNullOrWhiteSpace(PricingProvenanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(CapabilityProvenanceId);
        if (CapabilityObservedAtUtc > nowUtc)
        {
            throw new InvalidOperationException("Voice capability provenance cannot be observed in the future.");
        }
        if (CapabilityExpiresAtUtc <= CapabilityObservedAtUtc)
        {
            throw new InvalidOperationException("Voice capability provenance expiry must follow observation time.");
        }
        return this;
    }

    public bool IsCapabilityStale(DateTimeOffset nowUtc) => nowUtc >= CapabilityExpiresAtUtc;

    public string StableIdentity => string.Join("/", ProviderStableId, AccountId, VoiceStableId);
}

public sealed record VoiceLabQuery(
    string? Text,
    string? ProviderStableId,
    string? Language,
    IReadOnlySet<string> RequiredCapabilities,
    bool RequireAudition,
    bool ExcludeStaleCapabilities = true);

public static class VoiceLabCatalog
{
    public static IReadOnlyList<VoiceLabEntry> Search(
        IEnumerable<VoiceLabEntry> entries,
        VoiceLabQuery query,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(query.RequiredCapabilities);

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
