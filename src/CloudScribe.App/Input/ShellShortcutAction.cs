namespace CloudScribe.App.Input;

/// <summary>
/// Stable identifiers for shell-level keyboard actions. These identifiers are suitable for
/// future persistence; display labels and gestures may evolve independently.
/// </summary>
public enum ShellShortcutAction
{
    ToggleFocusReading = 0,
    OpenNavigation = 1,
    OpenOutline = 2,
    OpenInspector = 3,
    OpenQueue = 4,
    OpenShortcutGuide = 5,
    CloseTransientSurface = 6,
}
