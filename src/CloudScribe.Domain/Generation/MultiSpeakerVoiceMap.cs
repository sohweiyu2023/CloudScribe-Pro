namespace CloudScribe.Domain.Generation;

public sealed record SpeakerVoiceBinding(
    string SpeakerRole,
    string ProviderStableId,
    string AccountId,
    string VoiceStableId,
    string PricingProvenanceId,
    string CapabilityProvenanceId)
{
    public SpeakerVoiceBinding Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(SpeakerRole);
        ArgumentException.ThrowIfNullOrWhiteSpace(ProviderStableId);
        ArgumentException.ThrowIfNullOrWhiteSpace(AccountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(VoiceStableId);
        ArgumentException.ThrowIfNullOrWhiteSpace(PricingProvenanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(CapabilityProvenanceId);
        return this;
    }

    public string RouteIdentity => string.Join("/", ProviderStableId, AccountId, VoiceStableId);
}

public sealed record MultiSpeakerVoiceMap(IReadOnlyList<SpeakerVoiceBinding> Bindings)
{
    public MultiSpeakerVoiceMap Validate()
    {
        ArgumentNullException.ThrowIfNull(Bindings);
        if (Bindings.Count == 0) throw new InvalidOperationException("At least one speaker voice binding is required.");
        var validated = Bindings.Select(static binding => binding.Validate()).ToArray();
        var duplicateRole = validated.GroupBy(static binding => binding.SpeakerRole, StringComparer.Ordinal)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicateRole is not null) throw new InvalidOperationException($"Speaker role is mapped more than once: {duplicateRole.Key}");
        return this with { Bindings = validated };
    }

    public SpeakerVoiceBinding Resolve(string speakerRole)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(speakerRole);
        Validate();
        return Bindings.SingleOrDefault(binding => string.Equals(binding.SpeakerRole, speakerRole, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException($"No pinned voice route exists for speaker role '{speakerRole}'.");
    }

    public void AssertNoSilentRouteChange(MultiSpeakerVoiceMap previous)
    {
        ArgumentNullException.ThrowIfNull(previous);
        Validate();
        previous.Validate();
        foreach (var prior in previous.Bindings)
        {
            var current = Resolve(prior.SpeakerRole);
            if (!string.Equals(current.RouteIdentity, prior.RouteIdentity, StringComparison.Ordinal) ||
                !string.Equals(current.PricingProvenanceId, prior.PricingProvenanceId, StringComparison.Ordinal) ||
                !string.Equals(current.CapabilityProvenanceId, prior.CapabilityProvenanceId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Pinned provider route changed for speaker role '{prior.SpeakerRole}'.");
            }
        }
    }
}
