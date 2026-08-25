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
        ValidateValues(AccountId, ProvenanceId, ObservedAtUtc, ExpiresAtUtc, VoiceNames, AudioEncodings, MaximumCompiledPayloadBytes, nowUtc);
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

    private static void ValidateValues(
        string accountId,
        string provenanceId,
        DateTimeOffset observedAtUtc,
        DateTimeOffset expiresAtUtc,
        IReadOnlySet<string> voiceNames,
        IReadOnlySet<string> audioEncodings,
        int maximumCompiledPayloadBytes,
        DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(provenanceId);
        ArgumentNullException.ThrowIfNull(voiceNames);
        ArgumentNullException.ThrowIfNull(audioEncodings);
        if (observedAtUtc > nowUtc) throw new InvalidOperationException("Capability observation cannot be from the future.");
        if (expiresAtUtc <= observedAtUtc) throw new InvalidOperationException("Capability expiry must follow its observation time.");
        if (maximumCompiledPayloadBytes is < 256 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(maximumCompiledPayloadBytes));
    }
}
