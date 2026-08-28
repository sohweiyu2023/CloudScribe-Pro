using Avalonia.Input;

namespace CloudScribe.App.Input;

/// <summary>
/// Immutable shell shortcut binding. Gesture text is derived from the validated key and
/// modifiers so the discovery surface cannot drift from the active matcher.
/// </summary>
public sealed record KeyboardShortcutBinding
{
    private const KeyModifiers SupportedModifiers =
        KeyModifiers.Alt | KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Meta;

    public KeyboardShortcutBinding(
        ShellShortcutAction action,
        string label,
        Key key,
        KeyModifiers modifiers,
        bool canRemap = true)
    {
        if (!Enum.IsDefined(action))
        {
            throw new ArgumentOutOfRangeException(nameof(action), action, "Shortcut action is not defined.");
        }

        if (string.IsNullOrWhiteSpace(label))
        {
            throw new ArgumentException("Shortcut label is required.", nameof(label));
        }

        if (key == Key.None)
        {
            throw new ArgumentOutOfRangeException(nameof(key), key, "A shortcut requires a non-empty key.");
        }

        if ((modifiers & ~SupportedModifiers) != KeyModifiers.None)
        {
            throw new ArgumentOutOfRangeException(nameof(modifiers), modifiers, "Shortcut modifiers contain unsupported flags.");
        }

        KeyModifiers commandModifiers = KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta;
        if (canRemap
            && (modifiers & commandModifiers) == KeyModifiers.None
            && !IsFunctionKey(key))
        {
            throw new ArgumentException(
                "A remappable global shortcut must use Control, Alt or Meta unless it is a function key.",
                nameof(modifiers));
        }

        Action = action;
        Label = label.Trim();
        Key = key;
        Modifiers = modifiers;
        CanRemap = canRemap;
    }

    public ShellShortcutAction Action { get; }

    public string Label { get; }

    public Key Key { get; }

    public KeyModifiers Modifiers { get; }

    public bool CanRemap { get; }

    public string GestureText => KeyboardShortcutMap.FormatGesture(Key, Modifiers);

    public KeyboardShortcutBinding WithGesture(Key key, KeyModifiers modifiers)
    {
        if (!CanRemap)
        {
            throw new InvalidOperationException($"The '{Label}' shortcut is reserved and cannot be remapped.");
        }

        return new KeyboardShortcutBinding(Action, Label, key, modifiers, canRemap: true);
    }

    private static bool IsFunctionKey(Key key) =>
        (int)key >= (int)Key.F1 && (int)key <= (int)Key.F24;
}
