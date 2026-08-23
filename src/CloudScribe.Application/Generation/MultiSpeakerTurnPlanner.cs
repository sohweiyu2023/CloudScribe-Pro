using CloudScribe.Domain.Generation;

namespace CloudScribe.Application.Generation;

public sealed record MultiSpeakerTurn(
    int TurnIndex,
    string SpeakerId,
    string Text);

public sealed record PlannedSpeakerTurn(
    int TurnIndex,
    string SpeakerId,
    string Text,
    SpeakerRoute Route);

public static class MultiSpeakerTurnPlanner
{
    public static IReadOnlyList<PlannedSpeakerTurn> Plan(
        IReadOnlyList<MultiSpeakerTurn> turns,
        IReadOnlyCollection<SpeakerRoute> routes,
        IReadOnlyDictionary<string, bool> providerHealth,
        bool explicitFallbackAllowed)
    {
        ArgumentNullException.ThrowIfNull(turns);
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(providerHealth);

        var planned = new List<PlannedSpeakerTurn>(turns.Count);
        var seenIndexes = new HashSet<int>();
        foreach (var turn in turns.OrderBy(static turn => turn.TurnIndex))
        {
            if (turn.TurnIndex < 0 || !seenIndexes.Add(turn.TurnIndex))
                throw new InvalidOperationException("Multi-speaker turn indexes must be unique non-negative values.");
            ArgumentException.ThrowIfNullOrWhiteSpace(turn.SpeakerId);
            if (string.IsNullOrWhiteSpace(turn.Text))
                throw new InvalidOperationException("A multi-speaker turn cannot contain empty speech text.");

            var primary = routes.SingleOrDefault(route =>
                !route.IsFallback && string.Equals(route.SpeakerId, turn.SpeakerId, StringComparison.Ordinal));
            if (primary is null)
                throw new InvalidOperationException("Every speaker turn requires an explicit primary route.");
            if (!providerHealth.TryGetValue(primary.ProviderStableId, out var healthy))
                throw new InvalidOperationException("Provider health must be explicitly known before routing a speaker turn.");

            var selected = MultiSpeakerRoutingPolicy.Select(turn.SpeakerId, routes, healthy, explicitFallbackAllowed);
            if (string.IsNullOrWhiteSpace(selected.ProviderStableId) || string.IsNullOrWhiteSpace(selected.VoiceStableId))
                throw new InvalidOperationException("Selected speaker route must bind an explicit provider and voice.");
            planned.Add(new PlannedSpeakerTurn(turn.TurnIndex, turn.SpeakerId, turn.Text, selected));
        }

        return planned;
    }
}
