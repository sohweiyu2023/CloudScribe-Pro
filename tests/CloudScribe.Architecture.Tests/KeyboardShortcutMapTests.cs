using Avalonia.Input;
using CloudScribe.App.Input;

namespace CloudScribe.Architecture.Tests;

public sealed class KeyboardShortcutMapTests
{
    [Fact]
    public void DefaultMapIsUniqueAndResolvesEveryPublishedGesture()
    {
        KeyboardShortcutMap map = KeyboardShortcutMap.Default;

        Assert.Equal(7, map.Bindings.Count);
        Assert.Equal(7, map.Bindings.Select(static binding => binding.Action).Distinct().Count());
        Assert.Equal(7, map.Bindings.Select(static binding => binding.GestureText).Distinct(StringComparer.Ordinal).Count());
        foreach (KeyboardShortcutBinding binding in map.Bindings)
        {
            Assert.True(map.TryResolve(binding.Key, binding.Modifiers, out ShellShortcutAction resolved));
            Assert.Equal(binding.Action, resolved);
        }

        Assert.Contains(map.Bindings, static binding => string.Equals(binding.GestureText, "Ctrl+/", StringComparison.Ordinal));
        Assert.False(map.TryResolve(Key.N, KeyModifiers.Control | KeyModifiers.Shift | (KeyModifiers)32, out _));
        IList<KeyboardShortcutBinding> readOnlyBindings = Assert.IsAssignableFrom<IList<KeyboardShortcutBinding>>(map.Bindings);
        Assert.Throws<NotSupportedException>(() => readOnlyBindings.Add(map.Bindings[0]));
    }

    [Fact]
    public void ConstructorRejectsNullIncompleteExcessiveAndDuplicateMaps()
    {
        Assert.Throws<ArgumentException>(() => new KeyboardShortcutMap([null!]));
        Assert.Throws<ArgumentException>(() => new KeyboardShortcutMap(
        [
            new(ShellShortcutAction.ToggleFocusReading, "Focus Reading", Key.F11, KeyModifiers.None),
        ]));

        KeyboardShortcutBinding[] defaults = KeyboardShortcutMap.Default.Bindings.ToArray();
        Assert.Throws<ArgumentException>(() => new KeyboardShortcutMap(defaults.Append(defaults[0])));

        KeyboardShortcutBinding[] duplicateAction = defaults.ToArray();
        duplicateAction[1] = new KeyboardShortcutBinding(
            ShellShortcutAction.ToggleFocusReading,
            "Duplicate focus action",
            Key.F10,
            KeyModifiers.Control);
        Assert.Throws<ArgumentException>(() => new KeyboardShortcutMap(duplicateAction));

        KeyboardShortcutBinding[] duplicateGesture = defaults.ToArray();
        duplicateGesture[1] = new KeyboardShortcutBinding(
            ShellShortcutAction.OpenNavigation,
            "Duplicate focus gesture",
            Key.F11,
            KeyModifiers.None);
        Assert.Throws<ArgumentException>(() => new KeyboardShortcutMap(duplicateGesture));
    }

    [Fact]
    public void RemapCreatesANewCollisionFreeMapWithoutMutatingDefaults()
    {
        KeyboardShortcutMap original = KeyboardShortcutMap.Default;
        KeyboardShortcutMap remapped = original.WithOverride(
            ShellShortcutAction.ToggleFocusReading,
            Key.F10,
            KeyModifiers.Control);

        Assert.True(original.TryResolve(Key.F11, KeyModifiers.None, out ShellShortcutAction originalAction));
        Assert.Equal(ShellShortcutAction.ToggleFocusReading, originalAction);
        Assert.False(original.TryResolve(Key.F10, KeyModifiers.Control, out _));
        Assert.True(remapped.TryResolve(Key.F10, KeyModifiers.Control, out ShellShortcutAction remappedAction));
        Assert.Equal(ShellShortcutAction.ToggleFocusReading, remappedAction);
        Assert.False(remapped.TryResolve(Key.F11, KeyModifiers.None, out _));
    }

    [Fact]
    public void RemapRejectsCollisionReservedActionAndInvalidGesture()
    {
        KeyboardShortcutMap map = KeyboardShortcutMap.Default;

        Assert.Throws<InvalidOperationException>(() => map.WithOverride(
            ShellShortcutAction.ToggleFocusReading,
            Key.N,
            KeyModifiers.Control | KeyModifiers.Shift));
        Assert.Throws<InvalidOperationException>(() => map.WithOverride(
            ShellShortcutAction.CloseTransientSurface,
            Key.F12,
            KeyModifiers.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => map.WithOverride(
            ShellShortcutAction.ToggleFocusReading,
            Key.None,
            KeyModifiers.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => map.WithOverride(
            ShellShortcutAction.ToggleFocusReading,
            Key.F10,
            (KeyModifiers)32));
        Assert.Throws<ArgumentException>(() => map.WithOverride(
            ShellShortcutAction.ToggleFocusReading,
            Key.N,
            KeyModifiers.None));
        Assert.Throws<ArgumentException>(() => map.WithOverride(
            ShellShortcutAction.ToggleFocusReading,
            Key.N,
            KeyModifiers.Shift));
    }

    [Fact]
    public void ShortcutGuideAndMatcherUseTheSameDataDrivenMap()
    {
        string repositoryRoot = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(repositoryRoot, "src", "CloudScribe.App", "MainWindow.axaml"));
        string codeBehind = File.ReadAllText(Path.Combine(repositoryRoot, "src", "CloudScribe.App", "MainWindow.axaml.cs"));
        string viewModel = File.ReadAllText(Path.Combine(repositoryRoot, "src", "CloudScribe.App", "ViewModels", "ShellViewModel.cs"));

        Assert.Contains("ItemsSource=\"{Binding ShortcutMap.Bindings}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:DataType=\"input:KeyboardShortcutBinding\"", xaml, StringComparison.Ordinal);
        Assert.Contains("viewModel.ShortcutMap.TryResolve", codeBehind, StringComparison.Ordinal);
        Assert.Contains("TryApplyShortcutOverride", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("ShortcutBindings", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("_shortcutBindings", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"Ctrl+Shift+N\"", xaml, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "CloudScribe.sln")))
        {
            current = current.Parent;
        }

        return current?.FullName
            ?? throw new DirectoryNotFoundException("CloudScribe repository root was not found.");
    }
}
