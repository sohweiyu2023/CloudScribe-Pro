namespace CloudScribe.App.Composition;

public sealed record StudioPreferences
{
    public int Version { get; init; } = 1;
    public VoiceSetup? LastUsed { get; init; }
    public VoiceSetup[] Setups { get; init; } = [];
    public CloudScribe.App.Design.ThemePreference Theme { get; init; }
    public bool ReducedMotion { get; init; }
}
