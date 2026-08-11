using System.Collections.ObjectModel;
using Avalonia.Input;

namespace CloudScribe.App.Input;

/// <summary>
/// Immutable, collision-free shortcut map. Stage 2 uses the default map in memory; Stage 3 can
/// persist validated overrides without changing keyboard matching or discovery UI contracts.
/// </summary>
public sealed class KeyboardShortcutMap
{
    private const KeyModifiers SupportedModifiers =
        KeyModifiers.Alt | KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Meta;

    private readonly ReadOnlyCollection<KeyboardShortcutBinding> _bindings;
    private readonly Dictionary<(Key Key, KeyModifiers Modifiers), ShellShortcutAction> _actionsByGesture;

    public KeyboardShortcutMap(IEnumerable<KeyboardShortcutBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ShellShortcutAction[] definedActions = Enum.GetValues<ShellShortcutAction>();
        List<KeyboardShortcutBinding> snapshot = SnapshotBindings(
            bindings,
            definedActions.Length,
            nameof(bindings));
        ValidateActionCoverage(snapshot, definedActions, nameof(bindings));
        ValidateGestureUniqueness(snapshot, nameof(bindings));

        _bindings = snapshot.AsReadOnly();
        _actionsByGesture = snapshot.ToDictionary(
            static binding => (binding.Key, binding.Modifiers),
            static binding => binding.Action);
    }

    private static List<KeyboardShortcutBinding> SnapshotBindings(
        IEnumerable<KeyboardShortcutBinding> bindings,
        int maximumCount,
        string parameterName)
    {
        List<KeyboardShortcutBinding> snapshot = new(maximumCount);
        foreach (KeyboardShortcutBinding? binding in bindings)
        {
            if (binding is null)
            {
                throw new ArgumentException("Shortcut bindings cannot contain null entries.", parameterName);
            }

            if (snapshot.Count >= maximumCount)
            {
                throw new ArgumentException(
                    $"A shortcut map cannot contain more than {maximumCount} defined actions.",
                    parameterName);
            }

            snapshot.Add(binding);
        }

        if (snapshot.Count != maximumCount)
        {
            throw new ArgumentException(
                $"A shortcut map must contain exactly one binding for each of the {maximumCount} defined actions.",
                parameterName);
        }

        return snapshot;
    }

    private static void ValidateActionCoverage(
        IReadOnlyCollection<KeyboardShortcutBinding> snapshot,
        IReadOnlyCollection<ShellShortcutAction> definedActions,
        string parameterName)
    {
        ShellShortcutAction[] duplicateActions = snapshot
            .GroupBy(static binding => binding.Action)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .ToArray();
        if (duplicateActions.Length > 0)
        {
            throw new ArgumentException(
                $"Shortcut actions must be unique: {string.Join(", ", duplicateActions)}.",
                parameterName);
        }

        ShellShortcutAction[] missingActions = definedActions
            .Except(snapshot.Select(static binding => binding.Action))
            .ToArray();
        if (missingActions.Length > 0)
        {
            throw new ArgumentException(
                $"Shortcut bindings are missing defined actions: {string.Join(", ", missingActions)}.",
                parameterName);
        }
    }

    private static void ValidateGestureUniqueness(
        IEnumerable<KeyboardShortcutBinding> snapshot,
        string parameterName)
    {
        string[] duplicateGestures = snapshot
            .GroupBy(static binding => (binding.Key, binding.Modifiers))
            .Where(static group => group.Count() > 1)
            .Select(static group => FormatGesture(group.Key.Key, group.Key.Item2))
            .ToArray();
        if (duplicateGestures.Length > 0)
        {
            throw new ArgumentException(
                $"Shortcut gestures must be unique: {string.Join(", ", duplicateGestures)}.",
                parameterName);
        }
    }

    public static KeyboardShortcutMap Default { get; } = new(
        new KeyboardShortcutBinding[]
        {
            new(ShellShortcutAction.ToggleFocusReading, "Focus Reading", Key.F11, KeyModifiers.None),
            new(ShellShortcutAction.OpenNavigation, "Navigation", Key.N, KeyModifiers.Control | KeyModifiers.Shift),
            new(ShellShortcutAction.OpenOutline, "Document outline", Key.O, KeyModifiers.Control | KeyModifiers.Shift),
            new(ShellShortcutAction.OpenInspector, "Voice inspector", Key.I, KeyModifiers.Control | KeyModifiers.Shift),
            new(ShellShortcutAction.OpenQueue, "Queue", Key.Q, KeyModifiers.Control | KeyModifiers.Shift),
            new(ShellShortcutAction.OpenShortcutGuide, "Keyboard shortcut guide", Key.OemQuestion, KeyModifiers.Control),
            new(ShellShortcutAction.CloseTransientSurface, "Close surface / exit Focus Reading", Key.Escape, KeyModifiers.None, canRemap: false),
        });

    public IReadOnlyList<KeyboardShortcutBinding> Bindings => _bindings;

    public bool TryResolve(Key key, KeyModifiers modifiers, out ShellShortcutAction action)
    {
        if ((modifiers & ~SupportedModifiers) != KeyModifiers.None)
        {
            action = default;
            return false;
        }

        return _actionsByGesture.TryGetValue((key, modifiers), out action);
    }

    public KeyboardShortcutMap WithOverride(
        ShellShortcutAction action,
        Key key,
        KeyModifiers modifiers)
    {
        int index = _bindings
            .Select(static (binding, bindingIndex) => (binding, bindingIndex))
            .Where(pair => pair.binding.Action == action)
            .Select(pair => pair.bindingIndex)
            .DefaultIfEmpty(-1)
            .Single();
        if (index < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(action), action, "Shortcut action is not present in this map.");
        }

        KeyboardShortcutBinding replacement = _bindings[index].WithGesture(key, modifiers);
        KeyboardShortcutBinding? conflict = _bindings.FirstOrDefault(binding =>
            binding.Action != action
            && binding.Key == replacement.Key
            && binding.Modifiers == replacement.Modifiers);
        if (conflict is not null)
        {
            throw new InvalidOperationException(
                $"{replacement.GestureText} is already assigned to '{conflict.Label}'.");
        }

        KeyboardShortcutBinding[] updated = _bindings.ToArray();
        updated[index] = replacement;
        return new KeyboardShortcutMap(updated);
    }

    public static string FormatGesture(Key key, KeyModifiers modifiers)
    {
        if (key == Key.None)
        {
            throw new ArgumentOutOfRangeException(nameof(key), key, "A shortcut requires a non-empty key.");
        }

        if ((modifiers & ~SupportedModifiers) != KeyModifiers.None)
        {
            throw new ArgumentOutOfRangeException(nameof(modifiers), modifiers, "Shortcut modifiers contain unsupported flags.");
        }

        List<string> parts = [];
        if (modifiers.HasFlag(KeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (modifiers.HasFlag(KeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (modifiers.HasFlag(KeyModifiers.Meta))
        {
            parts.Add("Meta");
        }

        parts.Add(key switch
        {
            Key.OemQuestion => "/",
            Key.Escape => "Escape",
            _ => key.ToString(),
        });
        return string.Join("+", parts);
    }
}
