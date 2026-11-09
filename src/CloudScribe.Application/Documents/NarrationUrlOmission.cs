namespace CloudScribe.Application.Documents;

/// <summary>Immutable local URL omission policies. Persist and replay the selected version, never upgrade a saved copy.</summary>
public static class NarrationUrlOmission
{
    public const string LegacyVersion = NarrationUrlOmissionV1.CurrentVersion;
    /// <summary>The 1.0.6–1.0.7 bare-domain policy, still replayed exactly for copies and batches that recorded it.</summary>
    public const string BareDomainV2Version = NarrationUrlOmissionV2.CurrentVersion;
    public const string CurrentVersion = NarrationUrlOmissionV3.CurrentVersion;
    public const int MaximumSourceLength = NarrationUrlOmissionV1.MaximumSourceLength;

    public static bool IsSupportedVersion(string? version) => version is LegacyVersion or BareDomainV2Version or CurrentVersion;

    public static NarrationUrlOmissionResult Transform(string sourceText) => Transform(sourceText, CurrentVersion);

    public static NarrationUrlOmissionResult Transform(string sourceText, string? version) => version switch
    {
        LegacyVersion => NarrationUrlOmissionV1.Transform(sourceText),
        BareDomainV2Version => NarrationUrlOmissionV2.Transform(sourceText),
        CurrentVersion => NarrationUrlOmissionV3.Transform(sourceText),
        _ => throw new InvalidOperationException("Unsupported URL skipping version. Saved narration was not changed."),
    };
}
