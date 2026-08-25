namespace CloudScribe.Infrastructure.Generation;

public sealed record GoogleCapabilitySnapshot(
    string AccountId,
    string ProvenanceId,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    IReadOnlySet<string> VoiceNames,
    IReadOnlySet<string> AudioEncodings,
    int MaximumCompiledPayloadBytes)
{
    public GoogleCapabilitySnapshot Validate(DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(AccountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ProvenanceId);
        ArgumentNullException.ThrowIfNull(VoiceNames);
        ArgumentNullException.ThrowIfNull(AudioEncodings);
        if (ObservedAtUtc > nowUtc) throw new InvalidOperationException("Capability observation cannot be from the future.");
        if (ExpiresAtUtc <= ObservedAtUtc) throw new InvalidOperationException("Capability expiry must follow its observation time.");
        if (MaximumCompiledPayloadBytes is < 256 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(MaximumCompiledPayloadBytes));
        return this;
    }

    public bool IsStale(DateTimeOffset nowUtc) => nowUtc >= ExpiresAtUtc;

    public void RequireSupported(string voiceName, string audioEncoding, int compiledPayloadBytes, DateTimeOffset nowUtc)
    {
        Validate(nowUtc);
        if (IsStale(nowUtc)) throw new InvalidOperationException("Google capability snapshot is stale and must be refreshed before billable submission.");
        if (!VoiceNames.Contains(voiceName)) throw new InvalidOperationException("Selected Google voice is not present in the current capability snapshot.");
        if (!AudioEncodings.Contains(audioEncoding)) throw new InvalidOperationException("Selected Google audio encoding is not present in the current capability snapshot.");
        if (compiledPayloadBytes > MaximumCompiledPayloadBytes) throw new InvalidOperationException("Compiled Google payload exceeds the current capability snapshot limit.");
    }
}
