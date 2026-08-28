namespace CloudScribe.App.Design;

/// <summary>
/// Explicit stage-level visibility gates for controls whose backing workflows are not yet implemented.
/// A control must not become visible merely because its visual shell exists; the owning stage must first
/// replace the preview with a functional command and then advance the corresponding gate.
/// </summary>
public sealed record StageFeatureAvailability(
    bool ShowGenerationCommands,
    bool ShowProviderControls,
    bool ShowPlayerControls)
{
    public static StageFeatureAvailability Stage2 { get; } = new(
        ShowGenerationCommands: false,
        ShowProviderControls: false,
        ShowPlayerControls: false);

    public static StageFeatureAvailability Stage4 { get; } = new(
        ShowGenerationCommands: false,
        ShowProviderControls: true,
        ShowPlayerControls: false);
}
