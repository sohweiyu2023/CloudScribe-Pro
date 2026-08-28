namespace CloudScribe.Domain.Generation;

public sealed record SpeechProsody(decimal Rate, decimal PitchSemitones, decimal VolumeDb) : SpeechPlanNode
{
    public decimal Rate { get; init; } = Rate is > 0m and <= 4m
        ? Rate
        : throw new ArgumentOutOfRangeException(nameof(Rate), "Rate must be greater than zero and at most 4x.");

    public decimal PitchSemitones { get; init; } = PitchSemitones is >= -24m and <= 24m
        ? PitchSemitones
        : throw new ArgumentOutOfRangeException(nameof(PitchSemitones), "Pitch must be within +/-24 semitones.");

    public decimal VolumeDb { get; init; } = VolumeDb is >= -96m and <= 24m
        ? VolumeDb
        : throw new ArgumentOutOfRangeException(nameof(VolumeDb), "Volume must be between -96 dB and +24 dB.");
}
