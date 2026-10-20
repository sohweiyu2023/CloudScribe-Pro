using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace CloudScribe.App.Composition;

/// <summary>A docked, keyboard-operable guide. Geometry follows the live visual tree, never screen coordinates.</summary>
internal sealed class AnchoredWalkthrough : IDisposable
{
    private readonly Window _window;
    private readonly Control _original;
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, PreparedHost> Hosts = new();
    private readonly Grid _root;
    private readonly Grid _workspace;
    private readonly Border _card;
    private readonly TextBlock _heading = Text(string.Empty, 20);
    private readonly TextBlock _instruction = Text(string.Empty);
    private readonly TextBlock _location = Text(string.Empty);
    private readonly TextBlock _progress = Text(string.Empty);
    private readonly Button _back = Button("Back");
    private readonly Button _next = Button("Next");
    private readonly Button _link = Button("Open official instructions in browser");
    private readonly TargetOutline _outline = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly TaskCompletionSource<int> _completion = new();
    private readonly IReadOnlyList<WalkthroughStep> _steps;
    private readonly WalkthroughScreen _screen;
    private readonly IInputElement? _previousFocus;
    private readonly Dictionary<Control, bool> _compactVisibility = [];
    private Control? _target;
    private bool _disposed;
    private int _index;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "MA0051:Method is too long", Justification = "One guide surface layout and lifetime wiring.")]
    public AnchoredWalkthrough(Window window, IReadOnlyList<WalkthroughStep> steps, int index)
    {
        _window = window;
        _steps = steps;
        _index = index;
        _screen = steps[index].Screen;
        PreparedHost host = Hosts.GetValue(window, static owner => new PreparedHost(owner));
        _original = host.Original;
        _root = host.Root;
        _workspace = host.Workspace;
        _previousFocus = window.FocusManager?.GetFocusedElement();
        if (window is MainWindow)
        {
            foreach (Control chrome in _original.GetVisualDescendants().OfType<Control>().Where(c =>
                string.Equals(c.Name, "PlayerSurface", StringComparison.Ordinal) ||
                string.Equals(AutomationProperties.GetName(c), "Toggle Focus Reading", StringComparison.Ordinal)))
                _compactVisibility[chrome] = chrome.IsVisible;
        }
        _workspace.Children.Add(_outline);
        StackPanel text = new() { Spacing = 12, Margin = new Thickness(16) };
        text.Children.Add(_progress);
        text.Children.Add(_heading);
        text.Children.Add(_instruction);
        text.Children.Add(_location);
        text.Children.Add(_link);
        if (_screen is WalkthroughScreen.Studio or WalkthroughScreen.Return)
        {
            Button setup = Button("Include Google setup guide");
            setup.Click += (_, _) => _completion.TrySetResult(-2);
            text.Children.Add(setup);
        }
        Grid cardGrid = new() { RowDefinitions = new RowDefinitions("*,Auto") };
        cardGrid.Children.Add(new ScrollViewer { Content = text });
        WrapPanel actions = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(10) };
        Button skip = Button("Skip guide");
        Button locate = Button("Show control");
        foreach (Button button in new[] { _back, _next, skip, locate })
        {
            button.Margin = new Thickness(3);
            actions.Children.Add(button);
        }
        Grid.SetRow(actions, 1);
        cardGrid.Children.Add(actions);
        _card = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#10243A")),
            BorderBrush = Brushes.White,
            BorderThickness = new Thickness(2),
            Child = cardGrid,
        };
        _root.Children.Add(_card);
        _back.Click += (_, _) => Move(_index - 1);
        _next.Click += (_, _) => Move(_index + 1);
        skip.Click += (_, _) => _completion.TrySetResult(-1);
        locate.Click += (_, _) => Locate(focus: true);
        _link.Click += (_, _) => OpenLink();
        window.SizeChanged += OnSizeChanged;
        window.Closed += OnClosed;
        window.AddHandler(InputElement.KeyDownEvent, OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _timer.Tick += OnTick;
        _timer.Start();
        Resize();
        ShowStep();
    }

    public Task<int> Completion => _completion.Task;

    public void NavigateToCreation()
    {
        if (_disposed || _completion.Task.IsCompleted) return;
        for (int index = 0; index < _steps.Count; index++)
        {
            if (_steps[index].Screen is not (WalkthroughScreen.Connection or WalkthroughScreen.Create)) continue;
            Move(index);
            return;
        }
    }

    // Prepare the permanent host before the first accessibility observation. Replaying Help
    // never reparents the editor or invalidates its native automation peers.
    public static void Prepare(Window window) => _ = Hosts.GetValue(window, static owner => new PreparedHost(owner));

    private void Move(int index)
    {
        if (index < 0) return;
        if (index >= _steps.Count || _steps[index].Screen != _screen)
        {
            _completion.TrySetResult(index >= _steps.Count ? -1 : index);
            return;
        }
        _index = index;
        ShowStep();
    }

    private void ShowStep()
    {
        WalkthroughStep step = _steps[_index];
        _progress.Text = $"GUIDED WALKTHROUGH · Step {_index + 1} of {_steps.Count}";
        _heading.Text = step.Heading;
        AutomationProperties.SetLiveSetting(_heading, AutomationLiveSetting.Polite);
        _instruction.Text = step.Instruction;
        _link.IsVisible = step.Link is not null;
        _back.IsEnabled = _index > 0;
        _next.Content = _index == _steps.Count - 1 ? "Finish guide" : "Next";
        AutomationProperties.SetName(_next, _index == _steps.Count - 1 ? "Finish guide" : "Next guide step");
        Dispatcher.UIThread.Post(() => Locate(focus: false), DispatcherPriority.Loaded);
        Dispatcher.UIThread.Post(() => { if (!_disposed) _next.Focus(); }, DispatcherPriority.Loaded);
    }

    private void Locate(bool focus)
    {
        if (_window is MainWindow main && main.DataContext is ViewModels.ShellViewModel model)
        {
            model.SelectedNavigationItem = model.NavigationItems[0];
            model.CloseTransientSurfacesCommand.Execute(null);
        }
        _target = FindTarget();
        if (_target is null) { UpdateOutline(); return; }
        foreach (Expander expander in _target.GetVisualAncestors().OfType<Expander>()) expander.IsExpanded = true;
        if (_target is Expander section) section.IsExpanded = true;
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;
            if (_target is { } control) control.BringIntoView(new Rect(control.Bounds.Size).Inflate(8));
            if (focus && _target is { IsEnabled: true }) _target.Focus();
            UpdateOutline();
        }, DispatcherPriority.Loaded);
    }

    private Control? FindTarget()
    {
        foreach (string name in _steps[_index].Target.Split('|'))
        {
            Control? result = _original.GetVisualDescendants().OfType<Control>().FirstOrDefault(c =>
                (string.Equals(c.Name, name, StringComparison.Ordinal) || string.Equals(AutomationProperties.GetName(c), name, StringComparison.Ordinal))
                && c.IsVisible && c.GetVisualAncestors().OfType<Control>().All(p => p.IsVisible));
            if (result is not null) return result;
        }
        return null;
    }

    private void UpdateOutline()
    {
        _target = FindTarget();
        Rect? visible = null;
        if (_target is { } target && target.TranslatePoint(default, _workspace) is { } point)
        {
            Rect rect = new(point, target.Bounds.Size);
            rect = rect.Intersect(new Rect(_workspace.Bounds.Size));
            foreach (Visual ancestor in target.GetVisualAncestors().Where(v => v.ClipToBounds))
            {
                if (ancestor.TranslatePoint(default, _workspace) is { } origin)
                    rect = rect.Intersect(new Rect(origin, ancestor.Bounds.Size));
            }
            if (rect.Width > 2 && rect.Height > 2) visible = rect;
        }
        _outline.Target = visible;
        _outline.InvalidateVisual();
        _location.Text = visible is null
            ? "Control is outside this view or unavailable. Use Show control to locate it, or continue with Next."
            : $"Outlined control: {AutomationProperties.GetName(_target!) ?? _target!.Name}. " +
              (_target!.IsEnabled ? "You can use it while the guide is open." : "It is currently disabled; complete the earlier prerequisites first.");
    }

    private void Resize()
    {
        bool wide = _window.ClientSize.Width >= 1000;
        _root.ColumnDefinitions = new ColumnDefinitions(wide ? "*,330" : "*");
        _root.RowDefinitions = new RowDefinitions(wide ? "*" : "*,230");
        Grid.SetColumn(_card, wide ? 1 : 0);
        Grid.SetRow(_card, wide ? 0 : 1);
        foreach (var pair in _compactVisibility) pair.Key.SetCurrentValue(Visual.IsVisibleProperty, wide && pair.Value);
        if (_window is MainWindow main && main.DataContext is ViewModels.ShellViewModel model)
        {
            double available = _window.ClientSize.Width - (wide ? 330 : 0);
            model.UpdateViewport(available);
            main.UpdateCommandLayout(available);
        }
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs args)
    {
        Resize();
        Dispatcher.UIThread.Post(() => { if (!_disposed) Locate(focus: false); }, DispatcherPriority.Loaded);
    }
    private void OnTick(object? sender, EventArgs args) => UpdateOutline();
    private void OnClosed(object? sender, EventArgs args) => _completion.TrySetResult(-1);
    private void OnKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Escape) return;
        args.Handled = true;
        _completion.TrySetResult(-1);
    }

    private void OpenLink()
    {
        try
        {
            if (_steps[_index].Link is { } url)
                _ = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        { _location.Text = "Open this official address in your browser: " + _steps[_index].Link; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
        _window.SizeChanged -= OnSizeChanged;
        _window.Closed -= OnClosed;
        _window.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
        _workspace.Children.Remove(_outline);
        _root.Children.Remove(_card);
        _root.ColumnDefinitions = new ColumnDefinitions("*");
        _root.RowDefinitions = new RowDefinitions("*");
        foreach (var pair in _compactVisibility) pair.Key.SetCurrentValue(Visual.IsVisibleProperty, pair.Value);
        if (_window is MainWindow main && main.DataContext is ViewModels.ShellViewModel model)
        {
            model.UpdateViewport(_window.ClientSize.Width);
            main.UpdateCommandLayout(_window.ClientSize.Width);
        }
        if (_previousFocus is Control { IsVisible: true, IsEnabled: true } control) control.Focus();
    }

    private static TextBlock Text(string value, double size = 14) => new()
    { Text = value, FontSize = size, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap };
    private static Button Button(string label)
    {
        Button result = new() { Content = label, MinHeight = 36 };
        AutomationProperties.SetName(result, label);
        return result;
    }

    private sealed class TargetOutline : Control
    {
        public TargetOutline() { IsHitTestVisible = false; }
        public Rect? Target { get; set; }
        public override void Render(DrawingContext context)
        {
            if (Target is not { } rect) return;
            context.DrawRectangle(null, new Pen(Brushes.Black, 7), rect.Deflate(2));
            context.DrawRectangle(null, new Pen(Brushes.Yellow, 3), rect.Deflate(2));
        }
    }

    private sealed class PreparedHost
    {
        public PreparedHost(Window window)
        {
            Original = (Control)window.Content!;
            window.Content = null;
            Workspace.Children.Add(Original);
            Root.Children.Add(Workspace);
            window.Content = Root;
        }
        public Control Original { get; }
        public Grid Root { get; } = new();
        public Grid Workspace { get; } = new() { ClipToBounds = true };
    }
}
