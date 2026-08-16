using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using CloudScribe.App.Design;
using CloudScribe.App.Input;
using CloudScribe.App.Navigation;
using CloudScribe.Application.Activation;
using CloudScribe.Application.Diagnostics;
using CloudScribe.Application.Logging;
using CloudScribe.Application.Observability;
using CloudScribe.Application.Telemetry;
using CloudScribe.Domain.Observability;
using CloudScribe.Providers.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace CloudScribe.App.ViewModels;

public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    private readonly IActivityTimelineStore _timeline;
    private readonly ISupportBundleService _supportBundles;
    private readonly IDiagnosticLogStatus _diagnosticLogStatus;
    private readonly IProviderFactoryRegistry _providerRegistry;
    private readonly IActivationRouter _activationRouter;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ShellViewModel> _logger;
    private readonly Dictionary<AppRoute, RoutePageViewModel> _pages;
    private AdaptiveLayoutMode? _lastLoggedLayoutMode;
    private int _disposed;

    public ShellViewModel(
        IActivityTimelineStore timeline,
        ISupportBundleService supportBundles,
        IDiagnosticLogStatus diagnosticLogStatus,
        IProviderFactoryRegistry providerRegistry,
        IActivationRouter activationRouter,
        TimeProvider timeProvider,
        ILogger<ShellViewModel> logger)
    {
        _timeline = timeline;
        _supportBundles = supportBundles;
        _diagnosticLogStatus = diagnosticLogStatus;
        _providerRegistry = providerRegistry;
        _activationRouter = activationRouter;
        _timeProvider = timeProvider;
        _logger = logger;

        NavigationItems =
        [
            new(AppRoute.Studio, CosmicIconCatalog.Studio, "Studio", "Open Studio"),
            new(AppRoute.Library, CosmicIconCatalog.Library, "Library", "Open document library"),
            new(AppRoute.Queue, CosmicIconCatalog.Queue, "Queue", "Open generation queue"),
            new(AppRoute.Audio, CosmicIconCatalog.Audio, "Audio", "Open audio library"),
            new(AppRoute.Pricing, CosmicIconCatalog.Pricing, "Pricing", "Open pricing catalog"),
            new(AppRoute.Settings, CosmicIconCatalog.Settings, "Settings", "Open settings"),
            new(AppRoute.Diagnostics, CosmicIconCatalog.Diagnostics, "Diagnostics", "Open diagnostics"),
        ];

        ThemeOptions =
        [
            new(ThemePreference.FollowSystem, "Follow system"),
            new(ThemePreference.CosmicNight, "Cosmic Night"),
            new(ThemePreference.CosmicPaper, "Cosmic Paper"),
            new(ThemePreference.HighContrast, "High contrast"),
        ];

        ShortcutMap = KeyboardShortcutMap.Default;

        OutlineEntries =
        [
            new("Overview", "Paragraph 1", "CloudScribe Pro is a local-first"),
            new("Adaptive workspace", "Paragraph 2", "This Stage 2 workspace demonstrates"),
            new("Calm reading surface", "Paragraph 3", "The paper-centered reading surface"),
            new("Generation gates", "Paragraph 4", "Generation is not enabled yet"),
            new("Focus and shortcuts", "Paragraph 5", "Focus Reading removes surrounding"),
        ];

        _pages = CreatePages();
        _selectedNavigationItem = NavigationItems[0];
        _currentPage = _pages[AppRoute.Studio];
        _selectedThemeOption = ThemeOptions[0];
        _activationRouter.ActivationReceived += OnActivationReceived;
        _diagnosticLogStatus.StatusChanged += OnDiagnosticLogStatusChanged;
        if (!_diagnosticLogStatus.IsAvailable)
        {
            ApplyDiagnosticLogUnavailableState();
        }
    }

    public ObservableCollection<NavigationItem> NavigationItems { get; }

    public ObservableCollection<ThemeOption> ThemeOptions { get; }

    public ObservableCollection<OutlineEntryViewModel> OutlineEntries { get; }

    public KeyboardShortcutMap ShortcutMap { get; private set; }

    public StageFeatureAvailability FeatureAvailability { get; } = StageFeatureAvailability.Stage4;

    public string BuildLabel { get; } = ResolveBuildLabel();

    public event EventHandler<OutlineNavigationRequestedEventArgs>? OutlineNavigationRequested;

    public event EventHandler<OutputFolderRequestedEventArgs>? OutputFolderRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStudioSelected))]
    [NotifyPropertyChangedFor(nameof(IsRouteStateSelected))]
    [NotifyPropertyChangedFor(nameof(CanOpenDocumentSurfaces))]
    [NotifyPropertyChangedFor(nameof(CanUseFocusReading))]
    [NotifyPropertyChangedFor(nameof(CommandContextTitle))]
    [NotifyPropertyChangedFor(nameof(ShowOutlinePanel))]
    [NotifyPropertyChangedFor(nameof(ShowInspectorPanel))]
    [NotifyPropertyChangedFor(nameof(ShowOutlineButton))]
    [NotifyPropertyChangedFor(nameof(ShowInspectorButton))]
    private NavigationItem? _selectedNavigationItem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CommandContextTitle))]
    private RoutePageViewModel _currentPage;

    [ObservableProperty]
    private string _statusMessage = "Offline mode · No provider credentials loaded";

    [ObservableProperty]
    private long _externalActivationSequence;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNarrowLayout))]
    [NotifyPropertyChangedFor(nameof(IsCompactLayout))]
    [NotifyPropertyChangedFor(nameof(IsStandardLayout))]
    [NotifyPropertyChangedFor(nameof(IsFullLayout))]
    [NotifyPropertyChangedFor(nameof(ShowNavigationRail))]
    [NotifyPropertyChangedFor(nameof(ShowOutlinePanel))]
    [NotifyPropertyChangedFor(nameof(ShowInspectorPanel))]
    [NotifyPropertyChangedFor(nameof(ShowCompactCommandLabels))]
    [NotifyPropertyChangedFor(nameof(EditorMaximumWidth))]
    [NotifyPropertyChangedFor(nameof(ShowMobileNavigationButton))]
    [NotifyPropertyChangedFor(nameof(ShowOutlineButton))]
    [NotifyPropertyChangedFor(nameof(ShowInspectorButton))]
    private AdaptiveLayoutState _layout = AdaptiveLayoutState.ForWidth(1180);

    [ObservableProperty]
    private ThemeOption? _selectedThemeOption;

    [ObservableProperty]
    private bool _systemUsesDark = true;

    [ObservableProperty]
    private bool _systemHighContrast;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowChromeSurfaces))]
    [NotifyPropertyChangedFor(nameof(CommandBarRowHeight))]
    [NotifyPropertyChangedFor(nameof(PlayerRowHeight))]
    [NotifyPropertyChangedFor(nameof(StatusRowHeight))]
    [NotifyPropertyChangedFor(nameof(CanOpenDocumentSurfaces))]
    private bool _isFocusReading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyTransientSurfaceOpen))]
    [NotifyPropertyChangedFor(nameof(IsPrimaryContentInteractionEnabled))]
    private bool _isNavigationDrawerOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyTransientSurfaceOpen))]
    [NotifyPropertyChangedFor(nameof(IsPrimaryContentInteractionEnabled))]
    private bool _isOutlineDrawerOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyTransientSurfaceOpen))]
    [NotifyPropertyChangedFor(nameof(IsPrimaryContentInteractionEnabled))]
    private bool _isInspectorDrawerOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyTransientSurfaceOpen))]
    [NotifyPropertyChangedFor(nameof(IsPrimaryContentInteractionEnabled))]
    private bool _isQueueDrawerOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyTransientSurfaceOpen))]
    [NotifyPropertyChangedFor(nameof(IsPrimaryContentInteractionEnabled))]
    private bool _isShortcutGuideOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyTransientSurfaceOpen))]
    [NotifyPropertyChangedFor(nameof(IsPrimaryContentInteractionEnabled))]
    [NotifyPropertyChangedFor(nameof(CanOpenCompletionOutputFolder))]
    private bool _isCompletionDialogOpen;

    [ObservableProperty]
    private string _completionTitle = "Run complete";

    [ObservableProperty]
    private string _completionMessage = "The output files are ready.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOpenCompletionOutputFolder))]
    private string _completionOutputDirectory = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOpenCompletionOutputFolder))]
    private bool _completionOutputDirectoryExists;

    [ObservableProperty]
    private bool _reducedMotionEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CommandContextTitle))]
    private string _documentTitle = "The Long-Form Studio Primer";

    [ObservableProperty]
    private string _documentText = string.Join(Environment.NewLine,
    [
        "CloudScribe Pro is a local-first reading and production studio for long-form text-to-speech work.",
        string.Empty,
        "This Stage 2 workspace demonstrates the adaptive Cosmic Studio shell. The editor remains a single control while side surfaces collapse into drawers, so its caret, selection, scroll position and unsaved text are not recreated when the window changes size.",
        string.Empty,
        "The paper-centered reading surface is intentionally calm. Powerful controls stay at the edges: the document outline on the left, the capability-aware voice inspector on the right, and a persistent player and queue strip below.",
        string.Empty,
        "Generation is not enabled yet. Estimate and Generate stay hidden until the pricing, provider, approval and durable job gates are implemented in later stages. No network client or provider credential is required to explore this shell.",
        string.Empty,
        "Focus Reading removes surrounding production controls without replacing the editor. Press F11 to enter or leave Focus Reading, Ctrl+Shift+O for the outline, Ctrl+Shift+I for the inspector, Ctrl+Shift+Q for the queue, and Ctrl+/ for the shortcut guide.",
    ]);

    [ObservableProperty]
    private int _selectedOutlineIndex = -1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDocumentWorkspaceVisible))]
    [NotifyPropertyChangedFor(nameof(IsEmptyWorkspaceVisible))]
    [NotifyPropertyChangedFor(nameof(IsLoadingWorkspaceVisible))]
    [NotifyPropertyChangedFor(nameof(IsLifecycleBannerVisible))]
    [NotifyPropertyChangedFor(nameof(CanOpenDocumentSurfaces))]
    [NotifyPropertyChangedFor(nameof(CanUseFocusReading))]
    [NotifyPropertyChangedFor(nameof(CommandContextTitle))]
    [NotifyPropertyChangedFor(nameof(ShowOutlinePanel))]
    [NotifyPropertyChangedFor(nameof(ShowInspectorPanel))]
    [NotifyPropertyChangedFor(nameof(ShowOutlineButton))]
    [NotifyPropertyChangedFor(nameof(ShowInspectorButton))]
    [NotifyPropertyChangedFor(nameof(LifecycleLabel))]
    [NotifyPropertyChangedFor(nameof(LifecycleTitle))]
    [NotifyPropertyChangedFor(nameof(LifecycleDescription))]
    private WorkspaceLifecycleState _lifecycleState = WorkspaceLifecycleState.Offline;

    public bool IsStudioSelected => SelectedNavigationItem?.Route == AppRoute.Studio;

    public bool IsRouteStateSelected => !IsStudioSelected;

    public bool IsNarrowLayout => Layout.IsNarrow;

    public bool IsCompactLayout => Layout.IsCompact;

    public bool IsStandardLayout => Layout.IsStandard;

    public bool IsFullLayout => Layout.IsFull;

    public bool ShowNavigationRail => Layout.ShowNavigationRail && ShowChromeSurfaces;

    public bool ShowOutlinePanel => Layout.ShowOutlinePanel && CanOpenDocumentSurfaces;

    public bool ShowInspectorPanel => Layout.ShowInspectorPanel && CanOpenDocumentSurfaces;

    public bool ShowCompactCommandLabels => Layout.ShowCompactCommandLabels;

    public bool ShowMobileNavigationButton => !Layout.ShowNavigationRail && ShowChromeSurfaces;

    public bool ShowOutlineButton => !Layout.ShowOutlinePanel && CanOpenDocumentSurfaces;

    public bool ShowInspectorButton => !Layout.ShowInspectorPanel && CanOpenDocumentSurfaces;

    public double EditorMaximumWidth => Layout.EditorMaximumWidth;

    public bool ShowChromeSurfaces => !IsFocusReading;

    public bool CanOpenDocumentSurfaces =>
        IsStudioSelected && IsDocumentWorkspaceVisible && !IsFocusReading;

    public bool CanUseFocusReading =>
        SelectedNavigationItem is { Route: var route }
        && SupportsFocusReading(route, LifecycleState);

    public string CommandContextTitle => IsStudioSelected
        ? LifecycleState switch
        {
            WorkspaceLifecycleState.Empty => "No document selected",
            WorkspaceLifecycleState.Loading => "Preparing local workspace",
            _ => DocumentTitle,
        }
        : CurrentPage.Title;

    public GridLength CommandBarRowHeight => ShowChromeSurfaces ? GridLength.Auto : new GridLength(0);

    public GridLength PlayerRowHeight => ShowChromeSurfaces ? GridLength.Auto : new GridLength(0);

    public GridLength StatusRowHeight => ShowChromeSurfaces ? GridLength.Auto : new GridLength(0);

    public bool IsAnyTransientSurfaceOpen =>
        IsNavigationDrawerOpen
        || IsOutlineDrawerOpen
        || IsInspectorDrawerOpen
        || IsQueueDrawerOpen
        || IsShortcutGuideOpen
        || IsCompletionDialogOpen;

    public bool IsPrimaryContentInteractionEnabled => !IsAnyTransientSurfaceOpen;

    public bool CanOpenCompletionOutputFolder =>
        IsCompletionDialogOpen
        && CompletionOutputDirectoryExists
        && !string.IsNullOrWhiteSpace(CompletionOutputDirectory);

    public bool IsDocumentWorkspaceVisible => LifecycleState is
        WorkspaceLifecycleState.Ready
        or WorkspaceLifecycleState.Offline
        or WorkspaceLifecycleState.Error;

    public bool IsEmptyWorkspaceVisible => LifecycleState == WorkspaceLifecycleState.Empty;

    public bool IsLoadingWorkspaceVisible => LifecycleState == WorkspaceLifecycleState.Loading;

    public bool IsLifecycleBannerVisible => LifecycleState is
        WorkspaceLifecycleState.Offline
        or WorkspaceLifecycleState.Error;

    public string LifecycleLabel => LifecycleState.ToString().ToUpperInvariant();

    public string LifecycleTitle => LifecycleState switch
    {
        WorkspaceLifecycleState.Empty => "Start with a new local document",
        WorkspaceLifecycleState.Loading => "Preparing the workspace",
        WorkspaceLifecycleState.Offline => "Offline editing is available",
        WorkspaceLifecycleState.Error => "The preview could not be prepared",
        _ => "Workspace ready",
    };

    public string LifecycleDescription => LifecycleState switch
    {
        WorkspaceLifecycleState.Empty => "No document is selected. Durable document creation arrives in Stage 3.",
        WorkspaceLifecycleState.Loading => "Local state is being prepared. Provider access is not required.",
        WorkspaceLifecycleState.Offline => "Cloud features are unavailable or not configured; local reading and editing continue.",
        WorkspaceLifecycleState.Error => "Your temporary text remains in the editor. Retry and recovery actions become durable in Stage 3.",
        _ => string.Empty,
    };

    public string DocumentMetricLabel => $"Local preview · {DocumentText.Length:N0} characters";

    public CosmicThemePalette EffectivePalette => CosmicThemeCatalog.Resolve(
        SelectedThemeOption?.Preference ?? ThemePreference.FollowSystem,
        SystemUsesDark,
        SystemHighContrast);

    public bool DecorativeEffectsEnabled => EffectivePalette.DecorativeEffectsEnabled;

    public bool IsHighContrastTheme => !EffectivePalette.DecorativeEffectsEnabled;

    partial void OnSelectedNavigationItemChanged(NavigationItem? value)
    {
        ExitFocusReadingOutsideDocumentContext();
        if (value is null)
        {
            SelectedNavigationItem = NavigationItems[0];
            return;
        }

        CurrentPage = _pages[value.Route];
        CloudScribeLog.ShellRouteChanged(_logger, value.Route.ToString());
        StatusMessage = value.Route == AppRoute.Diagnostics
            ? _diagnosticLogStatus.IsAvailable
                ? "Diagnostics are local, bounded and redacted"
                : "Diagnostics · Local logging unavailable"
            : "Offline mode · No provider credentials loaded";
        CloseTransientSurfaces();
    }

    partial void OnSelectedOutlineIndexChanged(int value)
    {
        if (value < 0 || value >= OutlineEntries.Count)
        {
            return;
        }

        OutlineEntryViewModel entry = OutlineEntries[value];
        int offset = DocumentText.IndexOf(entry.AnchorText, StringComparison.Ordinal);
        if (offset < 0)
        {
            StatusMessage = $"Outline destination changed · {entry.Label}";
            SelectedOutlineIndex = -1;
            return;
        }

        OutlineNavigationRequested?.Invoke(
            this,
            new OutlineNavigationRequestedEventArgs(offset, entry.AnchorText.Length));
        StatusMessage = $"Outline · {entry.Label}";
        IsOutlineDrawerOpen = false;
        SelectedOutlineIndex = -1;
    }

    partial void OnLifecycleStateChanged(WorkspaceLifecycleState value)
    {
        CloudScribeLog.WorkspaceLifecycleChanged(_logger, value.ToString());
        ExitFocusReadingOutsideDocumentContext();
        if (!IsDocumentWorkspaceVisible)
        {
            IsOutlineDrawerOpen = false;
            IsInspectorDrawerOpen = false;
        }
    }

    partial void OnSelectedThemeOptionChanged(ThemeOption? value)
    {
        if (value is null)
        {
            SelectedThemeOption = ThemeOptions[0];
            return;
        }

        NotifyEffectivePaletteChanged();
        CloudScribeLog.ThemeChanged(_logger, value.Preference.ToString());
        StatusMessage = $"Theme · {value.Label}";
    }

    partial void OnSystemUsesDarkChanged(bool value) => NotifyEffectivePaletteChanged();

    partial void OnSystemHighContrastChanged(bool value) => NotifyEffectivePaletteChanged();

    partial void OnDocumentTextChanged(string value) => OnPropertyChanged(nameof(DocumentMetricLabel));

    private void NotifyEffectivePaletteChanged()
    {
        OnPropertyChanged(nameof(EffectivePalette));
        OnPropertyChanged(nameof(DecorativeEffectsEnabled));
        OnPropertyChanged(nameof(IsHighContrastTheme));
    }

    partial void OnIsFocusReadingChanging(bool value)
    {
        if (value && !CanUseFocusReading)
        {
            throw new InvalidOperationException(
                "Focus Reading requires a visible Studio document context.");
        }
    }

    partial void OnIsFocusReadingChanged(bool value)
    {
        CloudScribeLog.FocusReadingChanged(_logger, value);
        NotifyLayoutVisibilityChanged();
        CloseTransientSurfaces();
        StatusMessage = value
            ? "Focus Reading · Press F11 or Escape to return"
            : "Offline mode · No provider credentials loaded";
    }

    partial void OnLayoutChanging(AdaptiveLayoutState value) =>
        ArgumentNullException.ThrowIfNull(value);

    partial void OnLayoutChanged(AdaptiveLayoutState value)
    {
        if (_lastLoggedLayoutMode != value.Mode)
        {
            _lastLoggedLayoutMode = value.Mode;
            CloudScribeLog.AdaptiveLayoutChanged(_logger, value.Mode.ToString());
        }

        if (value.ShowNavigationRail)
        {
            IsNavigationDrawerOpen = false;
        }

        if (value.ShowOutlinePanel)
        {
            IsOutlineDrawerOpen = false;
        }

        if (value.ShowInspectorPanel)
        {
            IsInspectorDrawerOpen = false;
        }
    }

    public void UpdateViewport(double width)
    {
        AdaptiveLayoutState next = AdaptiveLayoutState.ForWidth(width);
        if (next != Layout)
        {
            Layout = next;
            StatusMessage = $"Adaptive layout · {next.Mode}";
        }
    }

    public void UpdateSystemTheme(bool usesDark, bool highContrast)
    {
        SystemUsesDark = usesDark;
        SystemHighContrast = highContrast;
    }

    public bool TryApplyShortcutOverride(
        ShellShortcutAction action,
        Key key,
        KeyModifiers modifiers,
        out string? error)
    {
        try
        {
            KeyboardShortcutMap updated = ShortcutMap.WithOverride(action, key, modifiers);
            ShortcutMap = updated;
            OnPropertyChanged(nameof(ShortcutMap));

            StatusMessage = $"Keyboard shortcut updated · {updated.Bindings.Single(binding => binding.Action == action).GestureText}";
            error = null;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            error = exception.Message;
            return false;
        }
    }

    [RelayCommand]
    private void ToggleFocusReading()
    {
        if (!IsFocusReading && !CanUseFocusReading)
        {
            return;
        }

        IsFocusReading = !IsFocusReading;
    }

    [RelayCommand]
    private void ToggleNavigationDrawer()
    {
        if (IsFocusReading)
        {
            return;
        }

        bool open = !IsNavigationDrawerOpen;
        CloseTransientSurfaces();
        IsNavigationDrawerOpen = open;
    }

    [RelayCommand]
    private void ToggleOutlineDrawer()
    {
        if (!CanOpenDocumentSurfaces)
        {
            return;
        }

        bool open = !IsOutlineDrawerOpen;
        CloseTransientSurfaces();
        IsOutlineDrawerOpen = open;
    }

    [RelayCommand]
    private void ToggleInspectorDrawer()
    {
        if (!CanOpenDocumentSurfaces)
        {
            return;
        }

        bool open = !IsInspectorDrawerOpen;
        CloseTransientSurfaces();
        IsInspectorDrawerOpen = open;
    }

    [RelayCommand]
    private void ToggleQueueDrawer()
    {
        if (IsFocusReading)
        {
            return;
        }

        bool open = !IsQueueDrawerOpen;
        CloseTransientSurfaces();
        IsQueueDrawerOpen = open;
    }

    [RelayCommand]
    private void ToggleShortcutGuide()
    {
        if (IsFocusReading)
        {
            return;
        }

        bool open = !IsShortcutGuideOpen;
        CloseTransientSurfaces();
        IsShortcutGuideOpen = open;
    }

    [RelayCommand]
    private void CloseTransientSurfaces()
    {
        IsNavigationDrawerOpen = false;
        IsOutlineDrawerOpen = false;
        IsInspectorDrawerOpen = false;
        IsQueueDrawerOpen = false;
        IsShortcutGuideOpen = false;
        IsCompletionDialogOpen = false;
    }

    public void ShowRunCompletedDialog(
        string operationName,
        string outputDirectory,
        int outputFileCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentOutOfRangeException.ThrowIfNegative(outputFileCount);

        string fullOutputDirectory;
        bool outputDirectoryExists;
        try
        {
            fullOutputDirectory = Path.GetFullPath(outputDirectory);
            outputDirectoryExists = Directory.Exists(fullOutputDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            fullOutputDirectory = outputDirectory.Trim();
            outputDirectoryExists = false;
        }

        CloseTransientSurfaces();
        CompletionTitle = $"{operationName.Trim()} complete";
        CompletionMessage = outputFileCount switch
        {
            0 => "The run completed. No output files were reported.",
            1 => "1 output file is ready.",
            _ => $"{outputFileCount:N0} output files are ready.",
        };
        CompletionOutputDirectory = fullOutputDirectory;
        CompletionOutputDirectoryExists = outputDirectoryExists;
        IsCompletionDialogOpen = true;
        StatusMessage = CompletionOutputDirectoryExists
            ? $"{operationName.Trim()} complete · Output ready"
            : $"{operationName.Trim()} complete · Output folder unavailable";
    }

    [RelayCommand]
    private void OpenCompletionOutputFolder()
    {
        if (!CanOpenCompletionOutputFolder)
        {
            StatusMessage = "Output folder is unavailable";
            return;
        }

        OutputFolderRequested?.Invoke(
            this,
            new OutputFolderRequestedEventArgs(CompletionOutputDirectory));
    }

    [RelayCommand]
    private void SelectTheme(ThemePreference preference)
    {
        ThemeOption? match = ThemeOptions.FirstOrDefault(option => option.Preference == preference);
        if (match is not null)
        {
            SelectedThemeOption = match;
        }
    }

    internal void ConfigureVisualEvidence(
        AppRoute route,
        ThemePreference theme,
        WorkspaceLifecycleState lifecycleState,
        bool focusReading,
        bool reducedMotionEnabled)
    {
        if (focusReading && !SupportsFocusReading(route, lifecycleState))
        {
            throw new ArgumentException(
                "Focus Reading visual evidence requires a visible Studio document context.",
                nameof(focusReading));
        }

        NavigationItem item = NavigationItems.FirstOrDefault(candidate => candidate.Route == route)
            ?? throw new ArgumentOutOfRangeException(nameof(route), route, "Unsupported visual-evidence route.");
        ThemeOption option = ThemeOptions.FirstOrDefault(candidate => candidate.Preference == theme)
            ?? throw new ArgumentOutOfRangeException(nameof(theme), theme, "Unsupported visual-evidence theme.");
        SelectedNavigationItem = item;
        SelectedThemeOption = option;
        LifecycleState = lifecycleState;
        IsFocusReading = focusReading;
        ReducedMotionEnabled = reducedMotionEnabled;
        CloseTransientSurfaces();
        if (reducedMotionEnabled)
        {
            StatusMessage = "Reduced motion enabled · non-essential transitions suppressed";
        }
    }

    private static bool SupportsFocusReading(
        AppRoute route,
        WorkspaceLifecycleState lifecycleState) =>
        route == AppRoute.Studio
        && lifecycleState is
            WorkspaceLifecycleState.Ready
            or WorkspaceLifecycleState.Offline
            or WorkspaceLifecycleState.Error;

    private void ExitFocusReadingOutsideDocumentContext()
    {
        if (IsFocusReading && !CanUseFocusReading)
        {
            IsFocusReading = false;
        }
    }

    private Dictionary<AppRoute, RoutePageViewModel> CreatePages() => new()
    {
        [AppRoute.Studio] = CreateStudioPage(),
        [AppRoute.Library] = CreateLibraryPage(),
        [AppRoute.Queue] = CreateQueuePage(),
        [AppRoute.Audio] = CreateAudioPage(),
        [AppRoute.Pricing] = CreatePricingPage(),
        [AppRoute.Settings] = CreateSettingsPage(),
        [AppRoute.Diagnostics] = CreateDiagnosticsPage(),
    };

    private RoutePageViewModel CreateStudioPage() => new(
        "Studio",
        "LOCAL-FIRST WORKSPACE",
        "Read, edit and prepare long-form work without a network connection or provider account.",
        "Cosmic Studio shell active",
        "The adaptive editor and Stage 4 provider/pricing foundations are available. Generation remains gated until the durable Stage 5 engine exists.",
        "READY")
    {
        Detail = "Provider factories registered at startup: " + _providerRegistry.AvailableProviders.Count,
    };

    private static RoutePageViewModel CreateLibraryPage() => new(
        "Library",
        "DOCUMENTS",
        "Your durable document library will live locally on this device.",
        "No documents yet",
        "Document creation and import arrive in Stage 3. This route is intentionally read-only in Stage 2.",
        "EMPTY");

    private static RoutePageViewModel CreateQueuePage() => new(
        "Queue",
        "GENERATION",
        "Review durable generation work and recovery state.",
        "No generation jobs",
        "Billable submission is unavailable until estimates, authorization and the generation engine are complete.",
        "EMPTY");

    private static RoutePageViewModel CreateAudioPage() => new(
        "Audio",
        "LOCAL MEDIA",
        "Browse verified exports and resume playback from this device.",
        "No audio yet",
        "The audio engine and player are introduced in Stage 5. Playback controls remain hidden until verified local audio exists.",
        "EMPTY");

    private static RoutePageViewModel CreatePricingPage() => new(
        "Pricing",
        "CATALOG TRUST",
        "Inspect provider pricing and its source, age and uncertainty before spending.",
        "Exact catalog contract not admitted",
        "Strict parsing, cost states, account/capability contracts and catalog dry-run orchestration are active. Approval remains blocked until the exact v2.22 schema/seed bytes are admitted.",
        "BLOCKED")
    {
        Detail = "No hard-coded provider prices · Exact schema 1.1.5/seed bytes required · Signature metadata alone is never trusted",
    };

    private static RoutePageViewModel CreateSettingsPage() => new(
        "Settings",
        "LOCAL CONFIGURATION",
        "Configure CloudScribe without placing secrets in source or ordinary settings files.",
        "Safe defaults active",
        "Provider credentials use the operating-system vault boundary; ordinary settings never contain secret values.",
        "READY")
    {
        Detail = "Local application storage uses the configured per-user application-data location and relies on operating-system account and volume protection.",
    };

    private RoutePageViewModel CreateDiagnosticsPage() => new(
        "Diagnostics",
        "LOCAL OBSERVABILITY",
        "Inspect bounded local diagnostics and preview exactly what a support bundle would contain.",
        _diagnosticLogStatus.IsAvailable ? "Privacy-first diagnostics" : "Diagnostics unavailable",
        _diagnosticLogStatus.IsAvailable
            ? "Documents, audio, databases, credentials and provider payloads are excluded from support bundles by default."
            : "Local diagnostic logging could not start. Editing remains available, and no document or audio content was exposed.",
        _diagnosticLogStatus.IsAvailable ? "INFO" : "DEGRADED")
    {
        Detail = _diagnosticLogStatus.IsAvailable
            ? $"Active log: {_diagnosticLogStatus.CurrentLogPath}"
            : $"Attempted log directory: {_diagnosticLogStatus.LogDirectory}",
        HasPrimaryAction = true,
        PrimaryActionLabel = "Refresh bundle preview",
        PrimaryActionCommand = new AsyncRelayCommand(RefreshSupportBundlePreviewAsync),
    };

    private void OnDiagnosticLogStatusChanged(object? sender, EventArgs eventArgs)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                ApplyDiagnosticLogUnavailableState();
            }
        }, DispatcherPriority.Input);
    }

    private void ApplyDiagnosticLogUnavailableState()
    {
        RoutePageViewModel page = _pages[AppRoute.Diagnostics];
        page.StateTitle = "Diagnostics unavailable";
        page.StateDescription = "Local diagnostic logging could not continue. Editing remains available, and no document or audio content was exposed.";
        page.StateKind = "DEGRADED";
        page.Detail = $"Logging unavailable · {_diagnosticLogStatus.DroppedRecordCount:N0} records dropped · Attempted directory: {_diagnosticLogStatus.LogDirectory}";
        if (SelectedNavigationItem?.Route == AppRoute.Diagnostics)
        {
            StatusMessage = "Diagnostics · Local logging unavailable";
        }
    }

    private async Task RefreshSupportBundlePreviewAsync()
    {
        using Activity? activity = CloudScribeTelemetry.ActivitySource.StartActivity("diagnostics.preview", ActivityKind.Internal);
        RoutePageViewModel page = _pages[AppRoute.Diagnostics];
        try
        {
            SupportBundlePreview preview = await _supportBundles.PreviewAsync().ConfigureAwait(true);
            CloudScribeTelemetry.SupportBundlePreviews.Add(1);
            CloudScribeLog.SupportBundlePreviewCompleted(_logger, preview.Files.Count, preview.TotalSizeBytes);
            page.Detail = _diagnosticLogStatus.IsAvailable
                ? $"{preview.Files.Count} eligible files · {preview.TotalSizeBytes:N0} bytes · Documents: no · Audio: no · Secrets: no · Active log: {_diagnosticLogStatus.CurrentLogPath}"
                : $"Logging unavailable · {_diagnosticLogStatus.DroppedRecordCount:N0} records dropped · Bundle contains only currently available bounded files · Attempted directory: {_diagnosticLogStatus.LogDirectory}";

            string correlationId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
            await _timeline.AppendAsync(ActivityTimelineEntry.Create(
                _timeProvider,
                ActivitySeverity.Information,
                "DIAGNOSTICS_PREVIEWED",
                "Support bundle contents previewed.",
                correlationId)).ConfigureAwait(true);
            StatusMessage = "Diagnostics · Support bundle preview refreshed";
        }
        catch (Exception exception) when (!IsFatalUiException(exception))
        {
            page.Detail = "Preview unavailable · Local documents, audio and credentials remain excluded and unchanged.";
            StatusMessage = "Diagnostics · Support bundle preview failed";
            CloudScribeLog.SupportBundlePreviewFailed(_logger, exception);
        }
    }

    private static bool IsFatalUiException(Exception exception) => exception is
        OutOfMemoryException
        or StackOverflowException
        or AccessViolationException;

    private void OnActivationReceived(object? sender, ActivationReceivedEventArgs request)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            CloseTransientSurfaces();
            SelectedNavigationItem = NavigationItems[0];
            if (request.Source == ActivationSource.SecondaryInstance)
            {
                ExternalActivationSequence++;
                StatusMessage = request.Arguments.Count == 0
                    ? "CloudScribe Pro was activated by another instance"
                    : $"CloudScribe Pro received {request.Arguments.Count} activation argument(s) from another instance";
                return;
            }

            StatusMessage = $"CloudScribe Pro opened with {request.Arguments.Count} startup argument(s)";
        });
    }

    private void NotifyLayoutVisibilityChanged()
    {
        OnPropertyChanged(nameof(ShowChromeSurfaces));
        OnPropertyChanged(nameof(CommandBarRowHeight));
        OnPropertyChanged(nameof(PlayerRowHeight));
        OnPropertyChanged(nameof(StatusRowHeight));
        OnPropertyChanged(nameof(ShowNavigationRail));
        OnPropertyChanged(nameof(ShowOutlinePanel));
        OnPropertyChanged(nameof(ShowInspectorPanel));
        OnPropertyChanged(nameof(ShowMobileNavigationButton));
        OnPropertyChanged(nameof(ShowOutlineButton));
        OnPropertyChanged(nameof(ShowInspectorButton));
    }

    private static string ResolveBuildLabel()
    {
        string? informationalVersion = typeof(ShellViewModel).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return BuildLabelFormatter.Format(informationalVersion);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _activationRouter.ActivationReceived -= OnActivationReceived;
        _diagnosticLogStatus.StatusChanged -= OnDiagnosticLogStatusChanged;
        GC.SuppressFinalize(this);
    }
}
