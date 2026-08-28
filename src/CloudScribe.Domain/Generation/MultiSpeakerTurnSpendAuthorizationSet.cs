namespace CloudScribe.Domain.Generation;

public sealed record MultiSpeakerTurnSpendAuthorizationSet(IReadOnlyList<MultiSpeakerTurnSpendAuthorization> Items)
{
    public MultiSpeakerTurnSpendAuthorizationSet Validate(MultiSpeakerVoiceMap voiceMap)
    {
        ArgumentNullException.ThrowIfNull(voiceMap);
        return ValidateItems(Items, voiceMap);
    }

    private MultiSpeakerTurnSpendAuthorizationSet ValidateItems(
        IReadOnlyList<MultiSpeakerTurnSpendAuthorization> items,
        MultiSpeakerVoiceMap voiceMap)
    {
        ArgumentNullException.ThrowIfNull(items);
        voiceMap.Validate();
        if (items.Count == 0)
            throw new InvalidOperationException("At least one multi-speaker spend authorization is required.");

        var validated = items.Select(static x => x.Validate()).ToArray();
        if (validated.Select(static x => x.SpeakerRole).Distinct(StringComparer.Ordinal).Count() != validated.Length)
            throw new InvalidOperationException("A speaker role cannot have multiple active spend authorizations.");

        var expected = voiceMap.Bindings.Select(static x => x.SpeakerRole).ToHashSet(StringComparer.Ordinal);
        var actual = validated.Select(static x => x.SpeakerRole).ToHashSet(StringComparer.Ordinal);
        if (!expected.SetEquals(actual))
            throw new InvalidOperationException("Spend authorizations must cover exactly the pinned multi-speaker voice map.");

        foreach (var authorization in validated)
        {
            var binding = voiceMap.Resolve(authorization.SpeakerRole);
            if (!string.Equals(authorization.RouteIdentity, binding.RouteIdentity, StringComparison.Ordinal) ||
                !string.Equals(authorization.PricingProvenanceId, binding.PricingProvenanceId, StringComparison.Ordinal))
                throw new InvalidOperationException("Spend authorization is stale for the current pinned speaker route.");
        }

        return this with { Items = validated };
    }
}
