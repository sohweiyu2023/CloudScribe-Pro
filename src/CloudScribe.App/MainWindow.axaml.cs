using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using CloudScribe.App.Design;
using CloudScribe.App.Input;
using CloudScribe.App.ViewModels;

namespace CloudScribe.App;

public sealed partial class MainWindow : Window
{
    private TransientSurface _lastTransientSurface;
    private IPlatformSettings? _platformSettings;
    private IInputElement? _focusBeforeFocusReading;
    private long _lastExternalActivationSequence;
    private bool _externalActivationPending;
    private bool _isClosingOrClosed;

    public MainWindow()
    {
        InitializeComponent();
        SizeChanged += OnWindowSizeChanged;
        Opened += OnWindowOpened;
        if (IsVisualCaptureRequested())
        {
            Opened += OnVisualCaptureOpened;
        }
        Activated += OnWindowActivated;
        Closed += OnWindowClosed;
        KeyDown += OnWindowKeyDown;
    }

    public MainWindow(ShellViewModel viewModel)
        : this()
    {
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.OutlineNavigationRequested += OnOutlineNavigationRequested;
        viewModel.OutputFolderRequested += OnOutputFolderRequested;
        viewModel.UpdateViewport(Width);
        RefreshSystemTheme(viewModel);
        ApplyPalette(viewModel.EffectivePalette);
        ObserveExternalActivation(viewModel.ExternalActivationSequence);
    }

    private ShellViewModel? ViewModel => DataContext as ShellViewModel;

    private void OnWindowSizeChanged(object? sender, SizeChangedEventArgs eventArgs)
    {
        ViewModel?.UpdateViewport(eventArgs.NewSize.Width);
    }

    private void OnWindowOpened(object? sender, EventArgs eventArgs)
    {
        ConstrainInitialBoundsToWorkingArea();
        RefreshPlatformSettingsSubscription();

        if (ViewModel is { } viewModel)
        {
            RefreshSystemTheme(viewModel);
        }

        if (_externalActivationPending)
        {
            _externalActivationPending = false;
            Dispatcher.UIThread.Post(RestoreAndActivate, DispatcherPriority.Input);
        }
    }

    private void ConstrainInitialBoundsToWorkingArea()
    {
        Screen? screen = Screens.ScreenFromWindow(this);
        if (screen is null || !double.IsFinite(screen.Scaling) || screen.Scaling <= 0)
        {
            return;
        }

        const double safeEdgeInset = 24;
        double workingWidth = screen.WorkingArea.Width / screen.Scaling;
        double workingHeight = screen.WorkingArea.Height / screen.Scaling;
        Width = Math.Min(Width, Math.Max(MinWidth, workingWidth - safeEdgeInset));
        Height = Math.Min(Height, Math.Max(MinHeight, workingHeight - safeEdgeInset));
        ViewModel?.UpdateViewport(Width);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel)
        {
            return;
        }

        _isClosingOrClosed = true;
        _externalActivationPending = false;
    }

    private void OnWindowClosed(object? sender, EventArgs eventArgs)
    {
        _isClosingOrClosed = true;
        _externalActivationPending = false;
        DetachRuntimeSubscriptions();
    }

    private void RefreshPlatformSettingsSubscription()
    {
        IPlatformSettings? current = Avalonia.Application.Current?.PlatformSettings;
        if (ReferenceEquals(_platformSettings, current))
        {
            return;
        }

        if (_platformSettings is not null)
        {
            _platformSettings.ColorValuesChanged -= OnPlatformColorValuesChanged;
        }

        _platformSettings = current;
        if (_platformSettings is not null)
        {
            _platformSettings.ColorValuesChanged += OnPlatformColorValuesChanged;
        }
    }

    private void OnWindowActivated(object? sender, EventArgs eventArgs)
    {
        if (ViewModel is { } viewModel)
        {
            RefreshSystemTheme(viewModel);
        }
    }

    private void OnPlatformColorValuesChanged(object? sender, PlatformColorValues values)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_isClosingOrClosed)
            {
                return;
            }

            if (ViewModel is { } viewModel)
            {
                viewModel.UpdateSystemTheme(
                    values.ThemeVariant == PlatformThemeVariant.Dark,
                    values.ContrastPreference == ColorContrastPreference.High);
            }
        }, DispatcherPriority.Input);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (sender is not ShellViewModel viewModel)
        {
            return;
        }

        if (string.Equals(eventArgs.PropertyName, nameof(ShellViewModel.EffectivePalette), StringComparison.Ordinal))
        {
            ApplyPalette(viewModel.EffectivePalette);
        }

        if (string.Equals(eventArgs.PropertyName, nameof(ShellViewModel.IsFocusReading), StringComparison.Ordinal))
        {
            HandleFocusReadingFocus(viewModel.IsFocusReading);
        }

        if (string.Equals(eventArgs.PropertyName, nameof(ShellViewModel.ExternalActivationSequence), StringComparison.Ordinal))
        {
            ObserveExternalActivation(viewModel.ExternalActivationSequence);
        }

        HandleTransientSurfaceFocus(viewModel, eventArgs.PropertyName);
    }


    private void ObserveExternalActivation(long sequence)
    {
        if (_isClosingOrClosed || sequence <= _lastExternalActivationSequence)
        {
            return;
        }

        _lastExternalActivationSequence = sequence;
        if (!IsVisible)
        {
            _externalActivationPending = true;
            return;
        }

        Dispatcher.UIThread.Post(RestoreAndActivate, DispatcherPriority.Input);
    }

    private void RestoreAndActivate()
    {
        if (_isClosingOrClosed)
        {
            return;
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        if (!IsVisible)
        {
            Show();
        }

        Activate();
    }

    private void HandleFocusReadingFocus(bool isFocusReading)
    {
        if (isFocusReading)
        {
            _focusBeforeFocusReading = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
            PostFocus(DocumentEditor);
            return;
        }

        IInputElement? previous = _focusBeforeFocusReading;
        _focusBeforeFocusReading = null;
        if (previous is Control { IsVisible: true, IsEnabled: true } previousControl)
        {
            PostFocus(previousControl);
            return;
        }

        if (DocumentEditor is { IsVisible: true, IsEnabled: true })
        {
            PostFocus(DocumentEditor);
        }
    }

    private void HandleTransientSurfaceFocus(ShellViewModel viewModel, string? propertyName)
    {
        if (string.Equals(propertyName, nameof(ShellViewModel.IsNavigationDrawerOpen), StringComparison.Ordinal) && viewModel.IsNavigationDrawerOpen)
        {
            FocusTransientSurface(TransientSurface.Navigation, NavigationDrawerCloseButton);
        }
        else if (string.Equals(propertyName, nameof(ShellViewModel.IsOutlineDrawerOpen), StringComparison.Ordinal) && viewModel.IsOutlineDrawerOpen)
        {
            FocusTransientSurface(TransientSurface.Outline, OutlineDrawerCloseButton);
        }
        else if (string.Equals(propertyName, nameof(ShellViewModel.IsInspectorDrawerOpen), StringComparison.Ordinal) && viewModel.IsInspectorDrawerOpen)
        {
            FocusTransientSurface(TransientSurface.Inspector, InspectorDrawerCloseButton);
        }
        else if (string.Equals(propertyName, nameof(ShellViewModel.IsQueueDrawerOpen), StringComparison.Ordinal) && viewModel.IsQueueDrawerOpen)
        {
            FocusTransientSurface(TransientSurface.Queue, QueueDrawerCloseButton);
        }
        else if (string.Equals(propertyName, nameof(ShellViewModel.IsShortcutGuideOpen), StringComparison.Ordinal) && viewModel.IsShortcutGuideOpen)
        {
            FocusTransientSurface(TransientSurface.Shortcuts, ShortcutGuideCloseButton);
        }
        else if (string.Equals(propertyName, nameof(ShellViewModel.IsCompletionDialogOpen), StringComparison.Ordinal) && viewModel.IsCompletionDialogOpen)
        {
            FocusTransientSurface(TransientSurface.Completion, CompletionDoneButton);
        }
        else if (IsTransientSurfaceProperty(propertyName) && !AnyTransientSurfaceOpen(viewModel))
        {
            RestoreTransientSurfaceFocus();
        }
    }

    private void FocusTransientSurface(TransientSurface surface, Control target)
    {
        _lastTransientSurface = surface;
        PostFocus(target);
    }

    private void PostFocus(Control target)
    {
        ArgumentNullException.ThrowIfNull(target);
        Dispatcher.UIThread.Post(() =>
        {
            if (!_isClosingOrClosed && target.IsVisible && target.IsEnabled)
            {
                target.Focus();
            }
        }, DispatcherPriority.Input);
    }

    private void RestoreTransientSurfaceFocus()
    {
        Control? target = _lastTransientSurface switch
        {
            TransientSurface.Navigation => NavigationDrawerButton,
            TransientSurface.Outline => OutlineDrawerButton,
            TransientSurface.Inspector => InspectorDrawerButton,
            TransientSurface.Queue => QueueDrawerButton,
            TransientSurface.Shortcuts => ShortcutGuideButton,
            TransientSurface.Completion => DocumentEditor,
            _ => null,
        };

        _lastTransientSurface = TransientSurface.None;
        if (target is { IsVisible: true, IsEnabled: true })
        {
            PostFocus(target);
            return;
        }

        if (DocumentEditor is { IsVisible: true, IsEnabled: true })
        {
            PostFocus(DocumentEditor);
        }
    }

    private static bool IsTransientSurfaceProperty(string? propertyName) => propertyName is
        nameof(ShellViewModel.IsNavigationDrawerOpen)
        or nameof(ShellViewModel.IsOutlineDrawerOpen)
        or nameof(ShellViewModel.IsInspectorDrawerOpen)
        or nameof(ShellViewModel.IsQueueDrawerOpen)
        or nameof(ShellViewModel.IsShortcutGuideOpen)
        or nameof(ShellViewModel.IsCompletionDialogOpen);

    private static bool AnyTransientSurfaceOpen(ShellViewModel viewModel) =>
        viewModel.IsNavigationDrawerOpen
        || viewModel.IsOutlineDrawerOpen
        || viewModel.IsInspectorDrawerOpen
        || viewModel.IsQueueDrawerOpen
        || viewModel.IsShortcutGuideOpen
        || viewModel.IsCompletionDialogOpen;

    private void OnWindowKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (ViewModel is { } viewModel && TryHandleShortcut(viewModel, eventArgs))
        {
            eventArgs.Handled = true;
        }
    }

    private bool TryHandleShortcut(ShellViewModel viewModel, KeyEventArgs eventArgs)
    {
        if (!viewModel.ShortcutMap.TryResolve(eventArgs.Key, eventArgs.KeyModifiers, out ShellShortcutAction action))
        {
            return false;
        }

        if (viewModel.IsFocusReading && action is
            ShellShortcutAction.OpenNavigation
            or ShellShortcutAction.OpenOutline
            or ShellShortcutAction.OpenInspector
            or ShellShortcutAction.OpenQueue
            or ShellShortcutAction.OpenShortcutGuide)
        {
            return true;
        }

        if (action == ShellShortcutAction.ToggleFocusReading
            && !viewModel.IsFocusReading
            && !viewModel.CanUseFocusReading)
        {
            return true;
        }

        if (!viewModel.CanOpenDocumentSurfaces && action is
            ShellShortcutAction.OpenOutline
            or ShellShortcutAction.OpenInspector)
        {
            return true;
        }

        return action switch
        {
            ShellShortcutAction.ToggleFocusReading => Execute(viewModel.ToggleFocusReadingCommand),
            ShellShortcutAction.CloseTransientSurface => HandleEscape(viewModel),
            ShellShortcutAction.OpenNavigation => FocusVisibleSurfaceOrExecute(
                viewModel.ShowNavigationRail,
                NavigationRailList,
                viewModel.ToggleNavigationDrawerCommand),
            ShellShortcutAction.OpenOutline => FocusVisibleSurfaceOrExecute(
                viewModel.ShowOutlinePanel,
                OutlinePanelList,
                viewModel.ToggleOutlineDrawerCommand),
            ShellShortcutAction.OpenInspector => FocusVisibleSurfaceOrExecute(
                viewModel.ShowInspectorPanel,
                InspectorShortcutButton,
                viewModel.ToggleInspectorDrawerCommand),
            ShellShortcutAction.OpenQueue => Execute(viewModel.ToggleQueueDrawerCommand),
            ShellShortcutAction.OpenShortcutGuide => Execute(viewModel.ToggleShortcutGuideCommand),
            _ => false,
        };
    }

    private bool FocusVisibleSurfaceOrExecute(
        bool surfaceVisible,
        Control focusTarget,
        System.Windows.Input.ICommand fallbackCommand)
    {
        if (surfaceVisible && focusTarget is { IsVisible: true, IsEnabled: true })
        {
            PostFocus(focusTarget);
            return true;
        }

        return Execute(fallbackCommand);
    }

    private static bool HandleEscape(ShellViewModel viewModel)
    {
        if (AnyTransientSurfaceOpen(viewModel))
        {
            return Execute(viewModel.CloseTransientSurfacesCommand);
        }

        return viewModel.IsFocusReading && Execute(viewModel.ToggleFocusReadingCommand);
    }

    private static bool Execute(System.Windows.Input.ICommand command)
    {
        if (!command.CanExecute(null))
        {
            return false;
        }

        command.Execute(null);
        return true;
    }

    private void OnOutputFolderRequested(object? sender, OutputFolderRequestedEventArgs eventArgs)
    {
        if (_isClosingOrClosed)
        {
            return;
        }

        try
        {
            string outputDirectory = Path.GetFullPath(eventArgs.OutputDirectory);
            if (!Directory.Exists(outputDirectory))
            {
                if (ViewModel is { } missingViewModel)
                {
                    missingViewModel.StatusMessage = "Output folder is unavailable";
                }

                return;
            }

            using Process? process = Process.Start(new ProcessStartInfo
            {
                FileName = outputDirectory,
                UseShellExecute = true,
                Verb = "open",
            });

            if (process is null && ViewModel is { } unavailableViewModel)
            {
                unavailableViewModel.StatusMessage = "The operating system could not open the output folder";
            }
        }
        catch (Exception exception) when (exception is
            ArgumentException
            or IOException
            or InvalidOperationException
            or NotSupportedException
            or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception)
        {
            if (ViewModel is { } viewModel)
            {
                viewModel.StatusMessage = "The operating system could not open the output folder";
            }
        }
    }

    private void OnOutlineNavigationRequested(object? sender, OutlineNavigationRequestedEventArgs eventArgs)
    {
        _lastTransientSurface = TransientSurface.None;
        Dispatcher.UIThread.Post(() =>
        {
            if (_isClosingOrClosed || !DocumentEditor.IsVisible || !DocumentEditor.IsEnabled)
            {
                return;
            }

            int textLength = DocumentEditor.Text?.Length ?? 0;
            int start = Math.Clamp(eventArgs.StartOffset, 0, textLength);
            int end = Math.Clamp(start + eventArgs.SelectionLength, start, textLength);
            DocumentEditor.SelectionStart = start;
            DocumentEditor.SelectionEnd = end;
            DocumentEditor.Focus();
        }, DispatcherPriority.Input);
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs eventArgs) => WindowState = WindowState.Minimized;

    private void OnMaximizeRestoreClick(object? sender, RoutedEventArgs eventArgs) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object? sender, RoutedEventArgs eventArgs) => Close();

    private void RefreshSystemTheme(ShellViewModel viewModel)
    {
        IPlatformSettings? settings = _platformSettings ?? Avalonia.Application.Current?.PlatformSettings;
        if (settings is null)
        {
            viewModel.UpdateSystemTheme(
                Avalonia.Application.Current?.ActualThemeVariant == ThemeVariant.Dark,
                highContrast: false);
            return;
        }

        PlatformColorValues values = settings.GetColorValues();
        viewModel.UpdateSystemTheme(
            values.ThemeVariant == PlatformThemeVariant.Dark,
            values.ContrastPreference == ColorContrastPreference.High);
    }

    private static void ApplyPalette(CosmicThemePalette palette)
    {
        Avalonia.Application? application = Avalonia.Application.Current;
        if (application is null)
        {
            return;
        }

        ApplySurfaceBrushes(application, palette);
        ApplyInkAndAccentBrushes(application, palette);
        ApplyStatusBrushes(application, palette);
        ApplyGradientColors(application, palette);
        application.RequestedThemeVariant = palette.PreferDarkControls ? ThemeVariant.Dark : ThemeVariant.Light;
    }

    private static void ApplySurfaceBrushes(Avalonia.Application application, CosmicThemePalette palette)
    {
        SetBrush(application, "Brush.Perimeter", palette.Perimeter);
        SetBrush(application, "Brush.PerimeterSoft", palette.PerimeterSoft);
        SetBrush(application, "Brush.Surface", palette.Surface);
        SetBrush(application, "Brush.SurfaceRaised", palette.SurfaceRaised);
        SetBrush(application, "Brush.SurfaceInset", palette.SurfaceInset);
        SetBrush(application, "Brush.SurfaceHover", palette.SurfaceHover);
        SetBrush(application, "Brush.SurfaceHighlight", palette.SurfaceHighlight);
        SetBrush(application, "Brush.Paper", palette.Paper);
        SetBrush(application, "Brush.PaperWarm", palette.PaperWarm);
        SetBrush(application, "Brush.PaperInset", palette.PaperInset);
        SetBrush(application, "Brush.PaperBorder", palette.PaperBorder);
        SetBrush(application, "Brush.Border", palette.Border);
        SetBrush(application, "Brush.BorderSubtle", palette.BorderSubtle);
        SetBrush(application, "Brush.Scrim", palette.Scrim);
        SetBrush(application, "Brush.AmbientViolet", palette.AmbientViolet);
        SetBrush(application, "Brush.AmbientBlue", palette.AmbientBlue);
    }

    private static void ApplyInkAndAccentBrushes(Avalonia.Application application, CosmicThemePalette palette)
    {
        SetBrush(application, "Brush.Ink", palette.Ink);
        SetBrush(application, "Brush.InkMuted", palette.InkMuted);
        SetBrush(application, "Brush.TextOnDark", palette.TextOnDark);
        SetBrush(application, "Brush.TextOnDarkMuted", palette.TextOnDarkMuted);
        SetBrush(application, "Brush.Primary", palette.Primary);
        SetBrush(application, "Brush.PrimaryBright", palette.PrimaryBright);
        SetBrush(application, "Brush.PrimaryFillBright", palette.PrimaryFillBright);
        SetBrush(application, "Brush.PrimaryText", palette.PrimaryText);
        SetBrush(application, "Brush.Secondary", palette.Secondary);
        SetBrush(application, "Brush.SecondaryBright", palette.SecondaryBright);
        SetBrush(application, "Brush.Cyan", palette.Cyan);
        SetBrush(application, "Brush.CyanBright", palette.CyanBright);
        SetBrush(application, "Brush.Focus", palette.Focus);
        SetBrush(application, "Brush.Selection", palette.Selection);
    }

    private static void ApplyStatusBrushes(Avalonia.Application application, CosmicThemePalette palette)
    {
        SetBrush(application, "Brush.Success", palette.Success);
        SetBrush(application, "Brush.SuccessSoft", palette.SuccessSoft);
        SetBrush(application, "Brush.Warning", palette.Warning);
        SetBrush(application, "Brush.WarningSoft", palette.WarningSoft);
        SetBrush(application, "Brush.WarningOnPaper", palette.WarningOnPaper);
        SetBrush(application, "Brush.Error", palette.Error);
        SetBrush(application, "Brush.ErrorSoft", palette.ErrorSoft);
        SetBrush(application, "Brush.Info", palette.Info);
        SetBrush(application, "Brush.InfoSoft", palette.InfoSoft);
    }

    private static void ApplyGradientColors(Avalonia.Application application, CosmicThemePalette palette)
    {
        SetColor(application, "Color.Perimeter", palette.Perimeter);
        SetColor(application, "Color.PerimeterSoft", palette.PerimeterSoft);
        SetColor(application, "Color.Surface", palette.Surface);
        SetColor(application, "Color.SurfaceHighlight", palette.SurfaceHighlight);
        SetColor(application, "Color.Paper", palette.Paper);
        SetColor(application, "Color.PaperWarm", palette.PaperWarm);
        SetColor(application, "Color.Primary", palette.Primary);
        SetColor(application, "Color.PrimaryBright", palette.PrimaryBright);
        SetColor(application, "Color.PrimaryFillBright", palette.PrimaryFillBright);
        SetColor(application, "Color.Cyan", palette.Cyan);
        SetColor(application, "Color.CyanBright", palette.CyanBright);
        SetColor(application, "Color.AmbientViolet", palette.AmbientViolet);
        SetColor(application, "Color.AmbientBlue", palette.AmbientBlue);
    }

    private static void SetBrush(Avalonia.Application application, string key, Color color) =>
        application.Resources[key] = new SolidColorBrush(color);

    private static void SetColor(Avalonia.Application application, string key, Color color) =>
        application.Resources[key] = color;

    private void DetachRuntimeSubscriptions()
    {
        if (_platformSettings is not null)
        {
            _platformSettings.ColorValuesChanged -= OnPlatformColorValuesChanged;
            _platformSettings = null;
        }

        if (DataContext is ShellViewModel viewModel)
        {
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            viewModel.OutlineNavigationRequested -= OnOutlineNavigationRequested;
            viewModel.OutputFolderRequested -= OnOutputFolderRequested;
        }
    }

    public void DisposeDataContext()
    {
        DetachRuntimeSubscriptions();
        if (DataContext is ShellViewModel viewModel)
        {
            viewModel.Dispose();
        }
    }

    private enum TransientSurface
    {
        None = 0,
        Navigation = 1,
        Outline = 2,
        Inspector = 3,
        Queue = 4,
        Shortcuts = 5,
        Completion = 6,
    }
}
