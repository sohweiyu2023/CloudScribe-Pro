using CloudScribe.App.Design;

namespace CloudScribe.Architecture.Tests;

public sealed class AdaptiveShellTests
{
    [Theory]
    [InlineData(0, AdaptiveLayoutMode.Narrow)]
    [InlineData(799.99, AdaptiveLayoutMode.Narrow)]
    [InlineData(800, AdaptiveLayoutMode.Compact)]
    [InlineData(1099.99, AdaptiveLayoutMode.Compact)]
    [InlineData(1100, AdaptiveLayoutMode.Standard)]
    [InlineData(1439.99, AdaptiveLayoutMode.Standard)]
    [InlineData(1440, AdaptiveLayoutMode.Full)]
    [InlineData(2400, AdaptiveLayoutMode.Full)]
    public void WidthMapsToSpecifiedLayoutBand(double width, AdaptiveLayoutMode expected)
    {
        Assert.Equal(expected, AdaptiveLayoutState.ForWidth(width).Mode);
    }

    [Fact]
    public void NarrowLayoutKeepsOnePrimaryWorkspace()
    {
        AdaptiveLayoutState state = AdaptiveLayoutState.ForWidth(640);

        Assert.False(state.ShowNavigationRail);
        Assert.False(state.ShowOutlinePanel);
        Assert.False(state.ShowInspectorPanel);
        Assert.True(state.ShowCompactCommandLabels);
        Assert.True(state.EditorMaximumWidth >= 700);
    }

    [Fact]
    public void FullLayoutShowsAllProductionSurfaces()
    {
        AdaptiveLayoutState state = AdaptiveLayoutState.ForWidth(1600);

        Assert.True(state.ShowNavigationRail);
        Assert.True(state.ShowOutlinePanel);
        Assert.True(state.ShowInspectorPanel);
        Assert.False(state.ShowCompactCommandLabels);
        Assert.InRange(state.EditorMaximumWidth, 820, 880);
    }

    [Fact]
    public void NegativeWidthIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AdaptiveLayoutState.ForWidth(-1));
    }

    [Fact]
    public void NonFiniteWidthIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AdaptiveLayoutState.ForWidth(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => AdaptiveLayoutState.ForWidth(double.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => AdaptiveLayoutState.ForWidth(double.NegativeInfinity));
    }

    [Fact]
    public void FollowSystemUsesDedicatedHighContrastPalette()
    {
        CosmicThemePalette palette = CosmicThemeCatalog.Resolve(
            ThemePreference.FollowSystem,
            systemUsesDark: false,
            systemHighContrast: true);

        Assert.Same(CosmicThemeCatalog.HighContrast, palette);
        Assert.Equal(Avalonia.Media.Colors.Yellow, palette.Primary);
    }

    [Fact]
    public void CompletedRunDialogUsesExactOutputFolderAndShellOpenContract()
    {
        string repositoryRoot = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(repositoryRoot, "src", "CloudScribe.App", "MainWindow.axaml"));
        string mainWindow = File.ReadAllText(Path.Combine(repositoryRoot, "src", "CloudScribe.App", "MainWindow.axaml.cs"));
        string viewModel = File.ReadAllText(Path.Combine(repositoryRoot, "src", "CloudScribe.App", "ViewModels", "ShellViewModel.cs"));

        Assert.Contains("IsCompletionDialogOpen", xaml, StringComparison.Ordinal);
        Assert.Contains("Open output folder", xaml, StringComparison.Ordinal);
        Assert.Contains("CompletionOutputDirectory", xaml, StringComparison.Ordinal);
        Assert.Contains("ShowRunCompletedDialog", viewModel, StringComparison.Ordinal);
        Assert.Contains("Path.GetFullPath(outputDirectory)", viewModel, StringComparison.Ordinal);
        Assert.Contains("OutputFolderRequested", viewModel, StringComparison.Ordinal);
        Assert.Contains("UseShellExecute = true", mainWindow, StringComparison.Ordinal);
        Assert.Contains("FileName = outputDirectory", mainWindow, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ThemePreference.CosmicNight)]
    [InlineData(ThemePreference.CosmicPaper)]
    public void SystemHighContrastOverridesDecorativeThemePreference(ThemePreference preference)
    {
        CosmicThemePalette palette = CosmicThemeCatalog.Resolve(
            preference,
            systemUsesDark: preference == ThemePreference.CosmicNight,
            systemHighContrast: true);

        Assert.Same(CosmicThemeCatalog.HighContrast, palette);
    }

    [Fact]
    public void FollowSystemTracksLightAndDarkPreference()
    {
        Assert.Same(
            CosmicThemeCatalog.Paper,
            CosmicThemeCatalog.Resolve(ThemePreference.FollowSystem, systemUsesDark: false, systemHighContrast: false));
        Assert.Same(
            CosmicThemeCatalog.Night,
            CosmicThemeCatalog.Resolve(ThemePreference.FollowSystem, systemUsesDark: true, systemHighContrast: false));
    }

    [Theory]
    [InlineData(ThemePreference.CosmicNight)]
    [InlineData(ThemePreference.CosmicPaper)]
    [InlineData(ThemePreference.HighContrast)]
    public void ExplicitThemeOverridesSystemTheme(ThemePreference preference)
    {
        CosmicThemePalette palette = CosmicThemeCatalog.Resolve(preference, systemUsesDark: false, systemHighContrast: false);

        CosmicThemePalette expected = preference switch
        {
            ThemePreference.CosmicNight => CosmicThemeCatalog.Night,
            ThemePreference.CosmicPaper => CosmicThemeCatalog.Paper,
            ThemePreference.HighContrast => CosmicThemeCatalog.HighContrast,
            _ => throw new InvalidOperationException(),
        };
        Assert.Same(expected, palette);
    }
    [Fact]
    public void FocusReadingCollapsesChromeRowsAndKeepsExitOutsideWorkspace()
    {
        string repositoryRoot = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(repositoryRoot, "src", "CloudScribe.App", "MainWindow.axaml"));

        Assert.Contains("Height=\"{Binding CommandBarRowHeight}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Height=\"{Binding PlayerRowHeight}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Height=\"{Binding StatusRowHeight}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Exit Focus Reading\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"Exit Focus Reading\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void FocusReadingPreservesKeyboardFocusAndUsesAdaptiveTitleBarGeometry()
    {
        string repositoryRoot = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(repositoryRoot, "src", "CloudScribe.App", "MainWindow.axaml"));
        string codeBehind = File.ReadAllText(Path.Combine(repositoryRoot, "src", "CloudScribe.App", "MainWindow.axaml.cs"));

        Assert.Contains("ExtendClientAreaTitleBarHeightHint=\"-1\"", xaml, StringComparison.Ordinal);
        Assert.Contains("FocusManager?.GetFocusedElement()", codeBehind, StringComparison.Ordinal);
        Assert.Contains("_focusBeforeFocusReading", codeBehind, StringComparison.Ordinal);
        Assert.Contains("HandleFocusReadingFocus", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void FocusDispatchUsesVoidLambdasAndSuppressesStaleWindowCallbacks()
    {
        string repositoryRoot = FindRepositoryRoot();
        string codeBehind = File.ReadAllText(Path.Combine(repositoryRoot, "src", "CloudScribe.App", "MainWindow.axaml.cs"));

        Assert.Contains("private void PostFocus(Control target)", codeBehind, StringComparison.Ordinal);
        Assert.Contains("private bool FocusVisibleSurfaceOrExecute(", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("private static bool FocusVisibleSurfaceOrExecute(", codeBehind, StringComparison.Ordinal);
        Assert.Contains("protected override void OnClosing(WindowClosingEventArgs e)", codeBehind, StringComparison.Ordinal);
        Assert.Contains("base.OnClosing(e);", codeBehind, StringComparison.Ordinal);
        Assert.Contains("if (e.Cancel)", codeBehind, StringComparison.Ordinal);
        Assert.Contains("IPlatformSettings? current = Avalonia.Application.Current?.PlatformSettings;", codeBehind, StringComparison.Ordinal);
        Assert.Contains("IPlatformSettings? settings = _platformSettings ?? Avalonia.Application.Current?.PlatformSettings;", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("IPlatformSettings? current = PlatformSettings;", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("_platformSettings ?? PlatformSettings", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("GetPlatformSettings", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("Closing += OnWindowClosing", codeBehind, StringComparison.Ordinal);
        Assert.Contains("if (!_isClosingOrClosed && target.IsVisible && target.IsEnabled)", codeBehind, StringComparison.Ordinal);
        Assert.Contains("_isClosingOrClosed || !DocumentEditor.IsVisible || !DocumentEditor.IsEnabled", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("Dispatcher.UIThread.Post(DocumentEditor.Focus", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("Dispatcher.UIThread.Post(previousControl.Focus", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("Dispatcher.UIThread.Post(target.Focus", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("Dispatcher.UIThread.Post(focusTarget.Focus", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void StartupAndCapturePathsAreExplicitlyBoundedAndOptIn()
    {
        string repositoryRoot = FindRepositoryRoot();
        string program = File.ReadAllText(Path.Combine(repositoryRoot, "src", "CloudScribe.App", "Program.cs"));
        string options = File.ReadAllText(Path.Combine(repositoryRoot, "src", "CloudScribe.Infrastructure", "Configuration", "CloudScribeOptions.cs"));
        string capture = File.ReadAllText(Path.Combine(repositoryRoot, "src", "CloudScribe.App", "MainWindow.VisualCapture.cs"));
        string linuxCapture = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "capture-stage2-linux.sh"));
        string windowsCapture = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "capture-stage2-windows.ps1"));

        Assert.Contains("StartupTimeoutSeconds", options, StringComparison.Ordinal);
        Assert.Contains("host.StartAsync(startup.Token)", program, StringComparison.Ordinal);
        Assert.Contains("initializer.InitializeAsync(startup.Token)", program, StringComparison.Ordinal);
        Assert.Contains("ApplicationStartupTimedOut", program, StringComparison.Ordinal);
        Assert.Contains("CLOUDSCRIBE_STAGE2_CAPTURE_MODE", capture, StringComparison.Ordinal);
        Assert.Contains("CLOUDSCRIBE_STAGE2_CAPTURE_MODE=1", linuxCapture, StringComparison.Ordinal);
        Assert.Contains("CLOUDSCRIBE_STAGE2_CAPTURE_MODE", windowsCapture, StringComparison.Ordinal);
        Assert.Contains("VisualEvidenceJsonOptions", capture, StringComparison.Ordinal);
        Assert.Contains("GetTask().ConfigureAwait(true)", capture, StringComparison.Ordinal);
        Assert.Contains("bitmap.Save(stream, PngBitmapEncoderOptions.Default)", capture, StringComparison.Ordinal);
        Assert.DoesNotContain("bitmap.Save(stream, (int?)null)", capture, StringComparison.Ordinal);
    }

    [Fact]
    public void IconOnlyShellControlsUseLocalVectorGeometry()
    {
        string repositoryRoot = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(repositoryRoot, "src", "CloudScribe.App", "MainWindow.axaml"));
        string navigationItem = File.ReadAllText(Path.Combine(repositoryRoot, "src", "CloudScribe.App", "Navigation", "NavigationItem.cs"));

        Assert.True(xaml.Split("<PathIcon", StringSplitOptions.None).Length - 1 >= 18);
        Assert.Contains("string IconPath", navigationItem, StringComparison.Ordinal);
        Assert.Contains("GeometryPathConverter", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("{Binding Glyph}", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("string Glyph", navigationItem, StringComparison.Ordinal);
        foreach (string glyph in new[] { "☰", "☷", "◫", "⌨", "↶", "↷", "▶" })
        {
            Assert.DoesNotContain(glyph, xaml, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(ThemePreference.CosmicNight)]
    [InlineData(ThemePreference.CosmicPaper)]
    [InlineData(ThemePreference.HighContrast)]
    public void SemanticTextPairsMeetMinimumContrast(ThemePreference preference)
    {
        CosmicThemePalette palette = CosmicThemeCatalog.Resolve(
            preference,
            systemUsesDark: preference == ThemePreference.CosmicNight,
            systemHighContrast: preference == ThemePreference.HighContrast);

        AssertContrastAtLeast(palette.TextOnDark, palette.Surface, 4.5, "primary text on dark surface");
        AssertContrastAtLeast(palette.TextOnDarkMuted, palette.Surface, 4.5, "muted text on dark surface");
        AssertContrastAtLeast(palette.Ink, palette.Paper, 4.5, "ink on paper");
        AssertContrastAtLeast(palette.InkMuted, palette.Paper, 4.5, "muted ink on paper");
        AssertContrastAtLeast(palette.WarningOnPaper, palette.PaperWarm, 4.5, "warning text on warm paper");
        AssertContrastAtLeast(palette.PrimaryBright, palette.Surface, 4.5, "gold label on surface");
        AssertContrastAtLeast(palette.PrimaryBright, palette.PerimeterSoft, 4.5, "gold label on perimeter surface");
        AssertContrastAtLeast(palette.Success, palette.Perimeter, 4.5, "success text on perimeter");
        AssertContrastAtLeast(palette.PrimaryText, palette.Primary, 4.5, "primary button text");
        AssertContrastAtLeast(palette.PrimaryText, palette.PrimaryFillBright, 4.5, "primary button hover text");
        AssertContrastAtLeast(palette.CyanBright, palette.Surface, 4.5, "cyan active label on surface");
        AssertContrastAtLeast(palette.SecondaryBright, palette.Surface, 4.5, "violet label on surface");
        AssertContrastAtLeast(palette.Success, palette.SuccessSoft, 4.5, "success label on status surface");
        AssertContrastAtLeast(palette.Warning, palette.WarningSoft, 4.5, "warning label on status surface");
        AssertContrastAtLeast(palette.Error, palette.ErrorSoft, 4.5, "error label on status surface");
        AssertContrastAtLeast(palette.Info, palette.InfoSoft, 4.5, "information label on status surface");
    }

    [Fact]
    public void FocusReadingContextInvariantCoversDirectRouteLifecycleAndCapturePaths()
    {
        string repositoryRoot = FindRepositoryRoot();
        string viewModel = File.ReadAllText(Path.Combine(repositoryRoot, "src", "CloudScribe.App", "ViewModels", "ShellViewModel.cs"));

        Assert.Contains("partial void OnIsFocusReadingChanging(bool value)", viewModel, StringComparison.Ordinal);
        Assert.Contains("if (value && !CanUseFocusReading)", viewModel, StringComparison.Ordinal);
        Assert.Contains("ExitFocusReadingOutsideDocumentContext();\n        if (value is null)", viewModel, StringComparison.Ordinal);
        int lifecycleMethod = viewModel.IndexOf("partial void OnLifecycleStateChanged(WorkspaceLifecycleState value)", StringComparison.Ordinal);
        int lifecycleExit = viewModel.IndexOf("ExitFocusReadingOutsideDocumentContext();", lifecycleMethod, StringComparison.Ordinal);
        int lifecycleCleanup = viewModel.IndexOf("if (!IsDocumentWorkspaceVisible)", lifecycleMethod, StringComparison.Ordinal);
        Assert.True(lifecycleMethod >= 0 && lifecycleExit > lifecycleMethod && lifecycleCleanup > lifecycleExit);
        Assert.Contains("if (focusReading && !SupportsFocusReading(route, lifecycleState))", viewModel, StringComparison.Ordinal);
        Assert.Contains("Unsupported visual-evidence route.", viewModel, StringComparison.Ordinal);
        Assert.Contains("Unsupported visual-evidence theme.", viewModel, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0.3.48-stage2-repair-in-progress", "0.3.48 · Stage 2")]
    [InlineData("1.0.0+release", "1.0.0")]
    [InlineData("local", "local")]
    [InlineData(null, "development build")]
    [InlineData("this-build-label-is-deliberately-far-too-long-for-window-chrome", "development build")]
    public void BuildLabelIsAssemblyDerivedAndBoundedForNarrowChrome(string? informationalVersion, string expected)
    {
        Assert.Equal(expected, BuildLabelFormatter.Format(informationalVersion));

        string repositoryRoot = FindRepositoryRoot();
        string formatter = File.ReadAllText(Path.Combine(repositoryRoot, "src", "CloudScribe.App", "Design", "BuildLabelFormatter.cs"));
        Assert.Contains("RegexTimeoutMilliseconds", formatter, StringComparison.Ordinal);
        Assert.Contains("RegexOptions.NonBacktracking", formatter, StringComparison.Ordinal);
    }

    [Fact]
    public void TitleBarUsesNativeCaptionRolesAndModalSurfacesDisablePrimaryContent()
    {
        string repositoryRoot = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(repositoryRoot, "src", "CloudScribe.App", "MainWindow.axaml"));
        string applicationXaml = File.ReadAllText(Path.Combine(repositoryRoot, "src", "CloudScribe.App", "CloudScribeApplication.axaml"));
        string viewModel = File.ReadAllText(Path.Combine(repositoryRoot, "src", "CloudScribe.App", "ViewModels", "ShellViewModel.cs"));

        Assert.Contains("ExtendClientAreaToDecorationsHint=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("WindowDecorations=\"None\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ExtendClientAreaChromeHints", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("SystemDecorations=", xaml, StringComparison.Ordinal);
        Assert.Contains("<Style Selector=\"ListBoxItem\">", xaml, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"HorizontalContentAlignment\" Value=\"Stretch\" />", xaml, StringComparison.Ordinal);
        Assert.DoesNotMatch("<ListBox\\b[^>]*\\bHorizontalContentAlignment\\s*=", xaml);
        Assert.Contains("WindowDecorationProperties.ElementRole=\"MinimizeButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("WindowDecorationProperties.ElementRole=\"MaximizeButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("WindowDecorationProperties.ElementRole=\"CloseButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding BuildLabel}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotMatch("Text=\"(?:v)?[0-9]+\\.[0-9]+\\.[0-9]+", xaml);
        Assert.Contains("AssemblyInformationalVersionAttribute", viewModel, StringComparison.Ordinal);
        Assert.Contains("BuildLabelFormatter.Format", viewModel, StringComparison.Ordinal);
        Assert.Contains("<RowDefinition Height=\"Auto\" />", xaml, StringComparison.Ordinal);
        Assert.True(xaml.Split("IsEnabled=\"{Binding IsPrimaryContentInteractionEnabled}\"", StringSplitOptions.None).Length - 1 >= 3);
        Assert.True(xaml.Split("IsTabStop=\"False\"", StringSplitOptions.None).Length - 1 >= 5);
        Assert.True(xaml.Split("ToolTip.ShowOnDisabled=\"True\"", StringSplitOptions.None).Length - 1 >= 10);
        Assert.Contains("ControlTheme x:Key=\"{x:Type Button}\" TargetType=\"Button\"", applicationXaml, StringComparison.Ordinal);
        Assert.Contains("ControlTheme x:Key=\"{x:Type CheckBox}\" TargetType=\"CheckBox\"", applicationXaml, StringComparison.Ordinal);
        Assert.Contains("Brush.ShellGradient", applicationXaml, StringComparison.Ordinal);
        Assert.Contains("Brush.DocumentGradient", applicationXaml, StringComparison.Ordinal);
        Assert.Contains("^:focus-visible /template/ Border#PART_FocusRing", applicationXaml, StringComparison.Ordinal);
        Assert.Contains("TextBox.editor:pointerover /template/ Border#PART_BorderElement", applicationXaml, StringComparison.Ordinal);
        Assert.Contains("TextBox.editor:focus /template/ Border#PART_BorderElement", applicationXaml, StringComparison.Ordinal);
        Assert.Contains("TextBox.document-title:focus /template/ Border#PART_BorderElement", applicationXaml, StringComparison.Ordinal);
        Assert.Contains("TextBox.editor[IsReadOnly=True]", applicationXaml, StringComparison.Ordinal);
        Assert.Contains("SelectionForegroundBrush", applicationXaml, StringComparison.Ordinal);
        Assert.Contains("PlaceholderForeground", applicationXaml, StringComparison.Ordinal);
        Assert.Contains("Window.reduced-motion Button /template/ ContentPresenter#PART_ContentPresenter", applicationXaml, StringComparison.Ordinal);
        Assert.Contains("Classes.high-contrast=\"{Binding IsHighContrastTheme}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Classes.reduced-motion=\"{Binding ReducedMotionEnabled}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotMatch("Background=\"#[0-9A-Fa-f]{8}\"", xaml);
    }


    [Fact]
    public void Stage2FutureControlsAreExplicitlyHidden()
    {
        StageFeatureAvailability features = StageFeatureAvailability.Stage2;

        Assert.False(features.ShowGenerationCommands);
        Assert.False(features.ShowProviderControls);
        Assert.False(features.ShowPlayerControls);

        string repositoryRoot = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(repositoryRoot, "src", "CloudScribe.App", "MainWindow.axaml"));
        Assert.Contains("x:Name=\"GenerationCommandPreview\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding FeatureAvailability.ShowGenerationCommands}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ProviderControlPreview\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding FeatureAvailability.ShowProviderControls}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PlayerControlPreview\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding FeatureAvailability.ShowPlayerControls}\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void FocusReadingRowsUseAutoSizingWhenVisible()
    {
        string repositoryRoot = FindRepositoryRoot();
        string viewModel = File.ReadAllText(Path.Combine(repositoryRoot, "src", "CloudScribe.App", "ViewModels", "ShellViewModel.cs"));

        Assert.Contains("ShowChromeSurfaces ? GridLength.Auto : new GridLength(0)", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowChromeSurfaces ? new GridLength(68)", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowChromeSurfaces ? new GridLength(86)", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void OutlineIsBoundToRealEditorNavigationInsteadOfDecorativeRanges()
    {
        string repositoryRoot = FindRepositoryRoot();
        string viewModel = File.ReadAllText(Path.Combine(repositoryRoot, "src", "CloudScribe.App", "ViewModels", "ShellViewModel.cs"));
        string codeBehind = File.ReadAllText(Path.Combine(repositoryRoot, "src", "CloudScribe.App", "MainWindow.axaml.cs"));

        Assert.Contains("OutlineNavigationRequested", viewModel, StringComparison.Ordinal);
        Assert.Contains("DocumentText.IndexOf(entry.AnchorText", viewModel, StringComparison.Ordinal);
        Assert.Contains("DocumentEditor.SelectionStart", codeBehind, StringComparison.Ordinal);
        Assert.Contains("DocumentEditor.SelectionEnd", codeBehind, StringComparison.Ordinal);
        Assert.Contains("SelectedOutlineIndex = -1", viewModel, StringComparison.Ordinal);
        Assert.Contains("_lastTransientSurface = TransientSurface.None", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("new(\"Opening\", \"1–3\"", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void VisualEvidenceGateValidatesRenderedContentAndHashes()
    {
        string repositoryRoot = FindRepositoryRoot();
        string linuxScript = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "capture-stage2-linux.sh"));
        string windowsScript = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "capture-stage2-windows.ps1"));
        string validator = File.ReadAllText(Path.Combine(repositoryRoot, "tools", "verify_stage2_visual_evidence.py"));

        Assert.Contains("verify_stage2_visual_evidence.py", linuxScript, StringComparison.Ordinal);
        Assert.Contains("verify_stage2_visual_evidence.py", windowsScript, StringComparison.Ordinal);
        Assert.Contains("duplicate screenshot bytes detected", validator, StringComparison.Ordinal);
        Assert.Contains("blank or non-rendered evidence is not accepted", validator, StringComparison.Ordinal);
        Assert.Contains("PNG CRC mismatch", validator, StringComparison.Ordinal);
        Assert.Contains("duplicate JSON member", validator, StringComparison.Ordinal);
        Assert.Contains("trailing bytes after IEND", validator, StringComparison.Ordinal);
        Assert.Contains("manifest field", validator, StringComparison.Ordinal);
        Assert.Contains("EditorFocused", validator, StringComparison.Ordinal);
        Assert.Contains("EditorVisualAudit", validator, StringComparison.Ordinal);
        Assert.Contains("MIN_TEXT_CONTRAST_RATIO = 4.5", validator, StringComparison.Ordinal);
        Assert.Contains("MIN_CARET_CONTRAST_RATIO = 3.0", validator, StringComparison.Ordinal);
        Assert.Contains("editor {label} contrast", validator, StringComparison.Ordinal);
    }


    [Fact]
    public void Stage2PromotionVerifierRequiresRuntimeEvidenceAndTopologicalBuildOrder()
    {
        string repositoryRoot = FindRepositoryRoot();
        string linux = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "verify-stage2.sh"));
        string windows = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "verify-stage2.ps1"));
        string linuxCapture = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "capture-stage2-linux.sh"));
        string windowsCapture = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "capture-stage2-windows.ps1"));
        string windowsPublish = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "publish-stage2-windows.ps1"));
        string windowsBuildLauncher = File.ReadAllText(Path.Combine(repositoryRoot, "BUILD-CLOUDSCRIBE-WINDOWS.cmd"));
        string windowsBuildGuide = File.ReadAllText(Path.Combine(repositoryRoot, "BUILDING-WINDOWS.txt"));

        Assert.True(
            linux.IndexOf("src/CloudScribe.Domain/CloudScribe.Domain.csproj", StringComparison.Ordinal)
            < linux.IndexOf("src/CloudScribe.App/CloudScribe.App.csproj", linux.IndexOf("build_projects=(", StringComparison.Ordinal), StringComparison.Ordinal));
        Assert.True(
            windows.IndexOf("src/CloudScribe.Domain/CloudScribe.Domain.csproj", windows.IndexOf("$buildProjects", StringComparison.Ordinal), StringComparison.Ordinal)
            < windows.IndexOf("src/CloudScribe.App/CloudScribe.App.csproj", windows.IndexOf("$buildProjects", StringComparison.Ordinal), StringComparison.Ordinal));
        Assert.Contains("runtime or execution evidence cannot be silently skipped", linux, StringComparison.Ordinal);
        Assert.Contains("scripts/capture-stage2-linux.sh", linux, StringComparison.Ordinal);
        Assert.DoesNotContain("if [[ \"$(uname -s)\" == \"Linux\" ]]", linux, StringComparison.Ordinal);
        Assert.Contains("OperatingSystem]::IsWindows", windows, StringComparison.Ordinal);
        Assert.Contains("scripts/capture-stage2-windows.ps1", windows, StringComparison.Ordinal);
        Assert.Contains("run_bounded_process.py", windowsCapture, StringComparison.Ordinal);
        Assert.Contains("--stdout-file", windowsCapture, StringComparison.Ordinal);
        Assert.Contains("--stderr-file", windowsCapture, StringComparison.Ordinal);
        Assert.Contains("--timeout-seconds 60", windowsCapture, StringComparison.Ordinal);
        Assert.Contains("$previousCaptureDirectory", windowsCapture, StringComparison.Ordinal);
        Assert.Contains("$captureDirectoryName", windowsCapture, StringComparison.Ordinal);
        Assert.Contains("run_bounded_process.py", linuxCapture, StringComparison.Ordinal);
        Assert.Contains("dotnet", windowsPublish, StringComparison.Ordinal);
        Assert.Contains("publish", windowsPublish, StringComparison.Ordinal);
        Assert.Contains("CloudScribe.exe", windowsPublish, StringComparison.Ordinal);
        AssertWindowsBuildLauncherContract(windowsBuildLauncher, windowsBuildGuide);
        Assert.Contains("LATEST-CLOUDSCRIBE-EXE.txt", windows, StringComparison.Ordinal);
        Assert.Contains("CLOUDSCRIBE_STAGE2_DELIVERY_ROOT", windows, StringComparison.Ordinal);
        Assert.Contains("verify_stage2_evidence_inventory.py", linux, StringComparison.Ordinal);
        Assert.Contains("run_bounded_process.py", linux, StringComparison.Ordinal);
        Assert.Contains("--max-output-bytes", linux, StringComparison.Ordinal);
        Assert.Contains("verify_stage2_evidence_inventory.py", windows, StringComparison.Ordinal);
        Assert.Contains("Invoke-BoundedCommand", windows, StringComparison.Ordinal);
        Assert.Contains("run_bounded_process.py", windows, StringComparison.Ordinal);
        Assert.Contains("MaximumOutputBytes", windows, StringComparison.Ordinal);
        Assert.Contains("Source manifest changed during Stage 2 verification", linux, StringComparison.Ordinal);
        Assert.Contains("Repository version changed during Stage 2 verification", linux, StringComparison.Ordinal);
        Assert.Contains("Source manifest changed during Stage 2 verification", windows, StringComparison.Ordinal);
        Assert.Contains("Repository version changed during Stage 2 verification", windows, StringComparison.Ordinal);
    }

    [Fact]
    public void SemanticDesignTokensAndVisualEvidenceCoverReducedMotionAndTextScaling()
    {
        string repositoryRoot = FindRepositoryRoot();
        string resources = File.ReadAllText(Path.Combine(repositoryRoot, "src", "CloudScribe.App", "CloudScribeApplication.axaml"));
        string window = File.ReadAllText(Path.Combine(repositoryRoot, "src", "CloudScribe.App", "MainWindow.axaml"));
        string codeBehind = File.ReadAllText(Path.Combine(repositoryRoot, "src", "CloudScribe.App", "MainWindow.axaml.cs"));
        string capture = File.ReadAllText(Path.Combine(repositoryRoot, "src", "CloudScribe.App", "MainWindow.VisualCapture.cs"));
        string validator = File.ReadAllText(Path.Combine(repositoryRoot, "tools", "verify_stage2_visual_evidence.py"));

        foreach (string token in new[]
        {
            "Typography.Body",
            "Spacing.Workspace",
            "Radius.Workspace",
            "Elevation.Workspace",
            "Motion.StandardMilliseconds",
        })
        {
            Assert.Contains(token, resources, StringComparison.Ordinal);
        }
        Assert.DoesNotMatch("FontSize=\"(?:10|30)\"", window);
        Assert.Contains("ApplyTypographyScale", capture, StringComparison.Ordinal);
        Assert.Contains("captureCase.Width / captureCase.TypographyScale", capture, StringComparison.Ordinal);
        Assert.Matches(@"CaptureEditorVisualAudit[\s\S]*ISolidColorBrush", capture);
        Assert.Contains("FocusManager?.Focus(null!, Avalonia.Input.NavigationMethod.Unspecified, Avalonia.Input.KeyModifiers.None)", capture, StringComparison.Ordinal);
        Assert.Matches(@"captureCase\.FocusEditor \|\| captureCase\.FocusReading[\s\S]*DocumentEditor\.IsFocused", capture);
        Assert.Contains("PART_PaperSurface", capture, StringComparison.Ordinal);
        AssertPaperEditorPointerFocusRegression(window, capture, validator);
        Assert.Contains("WindowStartupLocation=\"CenterScreen\"", window, StringComparison.Ordinal);
        Assert.Contains("TextWrapping=\"Wrap\" MaxLines=\"2\"", window, StringComparison.Ordinal);
        Assert.Contains("ConstrainInitialBoundsToWorkingArea", codeBehind, StringComparison.Ordinal);
        Assert.Contains("screen.WorkingArea.Width / screen.Scaling", codeBehind, StringComparison.Ordinal);
        Assert.Contains("10-standard-reduced-motion", capture, StringComparison.Ordinal);
        Assert.Contains("11-full-text-scale-125", capture, StringComparison.Ordinal);
        Assert.Contains("12-full-text-scale-150", capture, StringComparison.Ordinal);
        Assert.Contains("13-full-text-scale-175", capture, StringComparison.Ordinal);
        Assert.Contains("14-full-text-scale-200", capture, StringComparison.Ordinal);
        Assert.Contains("15-narrow-text-scale-200", capture, StringComparison.Ordinal);
        Assert.Contains("16-minimum-window-cosmic-night", capture, StringComparison.Ordinal);
        Assert.Contains("17-minimum-window-text-scale-200", capture, StringComparison.Ordinal);
        Assert.Contains("ReducedMotion", validator, StringComparison.Ordinal);
        Assert.Contains("TypographyScale", validator, StringComparison.Ordinal);
        Assert.Contains("mixed_dpi_verified", validator, StringComparison.Ordinal);
        Assert.Contains("operating_system_text_scale_verified", validator, StringComparison.Ordinal);
        Assert.Contains("capture_surface", validator, StringComparison.Ordinal);
        Assert.Contains("MAX_EVIDENCE_ENTRIES", validator, StringComparison.Ordinal);
        Assert.Contains("MAX_EVIDENCE_AGE", validator, StringComparison.Ordinal);
        Assert.Contains("symbolic-link", validator, StringComparison.Ordinal);
        Assert.Contains("PhysicalDirectoryPolicy.EnsureExistsWithoutLinks", capture, StringComparison.Ordinal);
        Assert.Contains("if (physicalOutputDirectory is not null)", capture, StringComparison.Ordinal);
        Assert.DoesNotContain("Path.Combine(outputDirectory, \"capture-error.txt\")", capture, StringComparison.Ordinal);
    }

    [Fact]
    public void ShutdownAndSecondaryActivationRemainStrictlyBounded()
    {
        string repositoryRoot = FindRepositoryRoot();
        string loggerProvider = File.ReadAllText(Path.Combine(repositoryRoot, "src", "CloudScribe.Infrastructure", "Diagnostics", "BoundedJsonFileLoggerProvider.cs"));
        string singleInstance = File.ReadAllText(Path.Combine(repositoryRoot, "src", "CloudScribe.Infrastructure", "Activation", "SingleInstanceCoordinator.cs"));
        string appPaths = File.ReadAllText(Path.Combine(repositoryRoot, "src", "CloudScribe.Infrastructure", "Configuration", "AppPaths.cs"));
        string physicalDirectoryPolicy = File.ReadAllText(Path.Combine(repositoryRoot, "src", "CloudScribe.Infrastructure", "Files", "PhysicalDirectoryPolicy.cs"));

        Assert.Contains("CancelledShutdownTimeout", loggerProvider, StringComparison.Ordinal);
        Assert.Contains("WaitWithoutThrowing", loggerProvider, StringComparison.Ordinal);
        Assert.Contains("DiagnosticMaximumFileCount", loggerProvider, StringComparison.Ordinal);
        Assert.Contains("EnumerateDiagnosticFilesBounded", loggerProvider, StringComparison.Ordinal);
        Assert.Contains("StatusChanged", loggerProvider, StringComparison.Ordinal);
        Assert.Contains("while (_channel.Reader.TryRead(out _))", loggerProvider, StringComparison.Ordinal);
        Assert.Contains("EnsureCurrentLogIsPhysical", loggerProvider, StringComparison.Ordinal);
        Assert.DoesNotContain("_writerTask.GetAwaiter().GetResult", loggerProvider, StringComparison.Ordinal);
        Assert.Contains("ReadBoundedUtf8LineAsync", singleInstance, StringComparison.Ordinal);
        Assert.Contains("WaitAsync(ListenerShutdownTimeout, timeProvider)", singleInstance, StringComparison.Ordinal);
        Assert.Contains("WaitForListenerSynchronously", singleInstance, StringComparison.Ordinal);
        Assert.DoesNotContain("DisposeAsync().AsTask().GetAwaiter().GetResult", singleInstance, StringComparison.Ordinal);
        Assert.DoesNotContain("arguments.Take(MaximumActivationArguments)", singleInstance, StringComparison.Ordinal);
        Assert.DoesNotContain("payload = \"[]\"", singleInstance, StringComparison.Ordinal);
    }

    [Fact]
    public void ActivationDiagnosticsAndLedgerContractsRemainTruthfulAndBounded()
    {
        string repositoryRoot = FindRepositoryRoot();

        AssertActivationTransportContract(repositoryRoot);
        AssertActivationRoutingContract(repositoryRoot);
        AssertShellActivationContract(repositoryRoot);
        AssertStartupDiagnosticsAndLedgerContract(repositoryRoot);
    }

    private static void AssertActivationTransportContract(string repositoryRoot)
    {
        string coordinator = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "CloudScribe.Infrastructure",
            "Activation",
            "SingleInstanceCoordinator.cs"));

        Assert.Contains("ActivationReadTimeout", coordinator, StringComparison.Ordinal);
        Assert.Contains("WaitAsync(ActivationReadTimeout, timeProvider, cancellationToken)", coordinator, StringComparison.Ordinal);
        Assert.Contains("ActivationDispatchFailed", coordinator, StringComparison.Ordinal);
        Assert.Contains("ValidateActivationArguments(activationArguments)", coordinator, StringComparison.Ordinal);
        Assert.Contains("activationRouter.Route(new ActivationReceivedEventArgs", coordinator, StringComparison.Ordinal);
        Assert.Contains("ActivationSource.PrimaryLaunch", coordinator, StringComparison.Ordinal);
        Assert.Contains("ActivationSource.SecondaryInstance", coordinator, StringComparison.Ordinal);
    }

    private static void AssertActivationRoutingContract(string repositoryRoot)
    {
        string router = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "CloudScribe.Application",
            "Activation",
            "ActivationRouter.cs"));

        Assert.Contains("MaximumPendingActivations", router, StringComparison.Ordinal);
        Assert.Contains("_pending.Enqueue(request)", router, StringComparison.Ordinal);
        Assert.Contains("private readonly System.Threading.Lock _gate = new();", router, StringComparison.Ordinal);
        Assert.DoesNotContain("private readonly object _gate = new();", router, StringComparison.Ordinal);
    }

    private static void AssertShellActivationContract(string repositoryRoot)
    {
        string shell = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "CloudScribe.App",
            "ViewModels",
            "ShellViewModel.cs"));
        string mainWindow = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "CloudScribe.App",
            "MainWindow.axaml.cs"));

        Assert.Contains("Diagnostics · Local logging unavailable", shell, StringComparison.Ordinal);
        Assert.Contains("ExternalActivationSequence", shell, StringComparison.Ordinal);
        Assert.Contains("ActivationSource.SecondaryInstance", shell, StringComparison.Ordinal);
        Assert.Contains("CloseTransientSurfaces();", shell, StringComparison.Ordinal);
        Assert.Contains("Interlocked.Exchange(ref _disposed, 1)", shell, StringComparison.Ordinal);
        Assert.Contains("Volatile.Read(ref _disposed)", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("CloudScribeLog.ActivationReceived(_logger", shell, StringComparison.Ordinal);
        Assert.Contains("ObserveExternalActivation", mainWindow, StringComparison.Ordinal);
        Assert.Contains("RestoreAndActivate", mainWindow, StringComparison.Ordinal);
        Assert.Contains("_isClosingOrClosed", mainWindow, StringComparison.Ordinal);
        Assert.Contains("protected override void OnClosing", mainWindow, StringComparison.Ordinal);
        Assert.Contains("if (e.Cancel)", mainWindow, StringComparison.Ordinal);
        Assert.Contains("WindowState == WindowState.Minimized", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Activate();", mainWindow, StringComparison.Ordinal);
    }

    private static void AssertStartupDiagnosticsAndLedgerContract(string repositoryRoot)
    {
        string program = File.ReadAllText(Path.Combine(repositoryRoot, "src", "CloudScribe.App", "Program.cs"));
        string application = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "CloudScribe.App",
            "CloudScribeApplication.axaml.cs"));
        string logger = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "CloudScribe.Infrastructure",
            "Diagnostics",
            "BoundedJsonFileLoggerProvider.cs"));
        string money = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "CloudScribe.Domain",
            "Observability",
            "ExactMoney.cs"));

        const string readyInvocation = "CloudScribeLog.ApplicationReady(";
        Assert.DoesNotContain(readyInvocation, program, StringComparison.Ordinal);
        Assert.Contains("CloudScribeLog.ApplicationReady(startupLogger);", application, StringComparison.Ordinal);
        Assert.Equal(1, application.Split(readyInvocation, StringSplitOptions.None).Length - 1);
        int frameworkCompleted = application.IndexOf("base.OnFrameworkInitializationCompleted();", StringComparison.Ordinal);
        int applicationReady = application.IndexOf(readyInvocation, StringComparison.Ordinal);
        Assert.True(frameworkCompleted >= 0 && applicationReady > frameworkCompleted);
        Assert.Contains("Diagnostic logging cannot enforce", logger, StringComparison.Ordinal);
        Assert.Contains("exception.Flatten().InnerExceptions.All", logger, StringComparison.Ordinal);
        Assert.Contains("catch (Exception loggingException) when (!IsFatalProcessException(loggingException))", program, StringComparison.Ordinal);
        Assert.Contains("catch (Exception consoleException) when (!IsFatalProcessException(consoleException))", program, StringComparison.Ordinal);
        Assert.Contains("EnsureValid", money, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyAndLoadingStatesDoNotExposeTheEditableSampleDocument()
    {
        string repositoryRoot = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(repositoryRoot, "src", "CloudScribe.App", "MainWindow.axaml"));
        string shell = File.ReadAllText(Path.Combine(repositoryRoot, "src", "CloudScribe.App", "ViewModels", "ShellViewModel.cs"));

        Assert.Contains("x:Name=\"DocumentWorkspace\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding IsDocumentWorkspaceVisible}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"EmptyWorkspace\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding IsEmptyWorkspaceVisible}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"LoadingWorkspace\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding IsLoadingWorkspaceVisible}\"", xaml, StringComparison.Ordinal);
        Assert.Equal(1, xaml.Split("x:Name=\"DocumentEditor\"", StringSplitOptions.None).Length - 1);
        Assert.Contains("public bool IsDocumentWorkspaceVisible => LifecycleState is", shell, StringComparison.Ordinal);
        Assert.Contains("public bool IsEmptyWorkspaceVisible => LifecycleState == WorkspaceLifecycleState.Empty;", shell, StringComparison.Ordinal);
        Assert.Contains("public bool IsLoadingWorkspaceVisible => LifecycleState == WorkspaceLifecycleState.Loading;", shell, StringComparison.Ordinal);
        Assert.Contains("public bool IsLifecycleBannerVisible => LifecycleState is", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void FocusReadingCannotReopenProductionSurfaces()
    {
        string repositoryRoot = FindRepositoryRoot();
        string shell = File.ReadAllText(Path.Combine(repositoryRoot, "src", "CloudScribe.App", "ViewModels", "ShellViewModel.cs"));
        string codeBehind = File.ReadAllText(Path.Combine(repositoryRoot, "src", "CloudScribe.App", "MainWindow.axaml.cs"));

        Assert.Equal(3, shell.Split("if (IsFocusReading)", StringSplitOptions.None).Length - 1);
        Assert.Equal(2, shell.Split("if (!CanOpenDocumentSurfaces)", StringSplitOptions.None).Length - 1);
        Assert.Contains("if (viewModel.IsFocusReading && action is", codeBehind, StringComparison.Ordinal);
        Assert.Contains("ShellShortcutAction.OpenNavigation", codeBehind, StringComparison.Ordinal);
        Assert.Contains("or ShellShortcutAction.OpenOutline", codeBehind, StringComparison.Ordinal);
        Assert.Contains("or ShellShortcutAction.OpenInspector", codeBehind, StringComparison.Ordinal);
        Assert.Contains("or ShellShortcutAction.OpenQueue", codeBehind, StringComparison.Ordinal);
        Assert.Contains("or ShellShortcutAction.OpenShortcutGuide", codeBehind, StringComparison.Ordinal);
        Assert.Contains("if (!viewModel.CanOpenDocumentSurfaces && action is", codeBehind, StringComparison.Ordinal);
        Assert.Contains("ShellShortcutAction.OpenShortcutGuide => Execute(viewModel.ToggleShortcutGuideCommand)", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void RouteAndLifecycleChromeUseTruthfulDocumentContext()
    {
        string repositoryRoot = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(repositoryRoot, "src", "CloudScribe.App", "MainWindow.axaml"));
        string shell = File.ReadAllText(Path.Combine(repositoryRoot, "src", "CloudScribe.App", "ViewModels", "ShellViewModel.cs"));

        Assert.Contains("Text=\"{Binding CommandContextTitle}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("public string CommandContextTitle => IsStudioSelected", shell, StringComparison.Ordinal);
        Assert.Contains(": CurrentPage.Title;", shell, StringComparison.Ordinal);
        Assert.Contains("public bool CanOpenDocumentSurfaces =>", shell, StringComparison.Ordinal);
        Assert.Contains("IsStudioSelected && IsDocumentWorkspaceVisible && !IsFocusReading;", shell, StringComparison.Ordinal);
        Assert.Contains("public bool ShowOutlinePanel => Layout.ShowOutlinePanel && CanOpenDocumentSurfaces;", shell, StringComparison.Ordinal);
        Assert.Contains("public bool ShowInspectorPanel => Layout.ShowInspectorPanel && CanOpenDocumentSurfaces;", shell, StringComparison.Ordinal);
    }


    [Fact]
    public void ShellStateNormalizesNullableSelectionsBeforeReturning()
    {
        string repositoryRoot = FindRepositoryRoot();
        string shell = File.ReadAllText(Path.Combine(repositoryRoot, "src", "CloudScribe.App", "ViewModels", "ShellViewModel.cs"));

        Assert.Contains("SelectedNavigationItem = NavigationItems[0];", shell, StringComparison.Ordinal);
        Assert.Contains("SelectedThemeOption = ThemeOptions[0];", shell, StringComparison.Ordinal);
        Assert.Contains("partial void OnLayoutChanging(AdaptiveLayoutState value)", shell, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "partial void OnLayoutChanged(AdaptiveLayoutState value)\n    {\n        ArgumentNullException.ThrowIfNull(value);",
            shell,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ReopenedWindowDoesNotDuplicatePlatformThemeSubscriptions()
    {
        string repositoryRoot = FindRepositoryRoot();
        string codeBehind = File.ReadAllText(Path.Combine(repositoryRoot, "src", "CloudScribe.App", "MainWindow.axaml.cs"));

        Assert.Contains("RefreshPlatformSettingsSubscription();", codeBehind, StringComparison.Ordinal);
        Assert.Contains("if (ReferenceEquals(_platformSettings, current))", codeBehind, StringComparison.Ordinal);
        Assert.Contains("_platformSettings.ColorValuesChanged -= OnPlatformColorValuesChanged;", codeBehind, StringComparison.Ordinal);
        Assert.Contains("_platformSettings.ColorValuesChanged += OnPlatformColorValuesChanged;", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void DependencyScansAreMachineReadableAndEnforced()
    {
        string repositoryRoot = FindRepositoryRoot();
        string linux = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "verify-stage2.sh"));
        string windows = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "verify-stage2.ps1"));
        string validator = File.ReadAllText(Path.Combine(repositoryRoot, "tools", "verify_dotnet_package_scan.py"));
        string physicalDirectory = File.ReadAllText(Path.Combine(repositoryRoot, "tools", "prepare_physical_directory.py"));
        string directoryProps = File.ReadAllText(Path.Combine(repositoryRoot, "Directory.Build.props"));
        string nugetConfig = File.ReadAllText(Path.Combine(repositoryRoot, "NuGet.config"));
        string linuxAudit = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "invoke-nuget-audit-scan.sh"));
        string windowsAudit = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "invoke-nuget-audit-scan.ps1"));

        Assert.Contains("--format json", linux, StringComparison.Ordinal);
        Assert.Contains("verify_dotnet_package_scan.py", linux, StringComparison.Ordinal);
        Assert.Contains("'--format', 'json'", windows, StringComparison.Ordinal);
        Assert.Contains("verify_dotnet_package_scan.py", windows, StringComparison.Ordinal);
        Assert.Contains("invoke-nuget-audit-scan.sh", linux, StringComparison.Ordinal);
        Assert.Contains("invoke-nuget-audit-scan.ps1", windows, StringComparison.Ordinal);
        Assert.Contains("https://data.nuget.org/v3/index.json", nugetConfig, StringComparison.Ordinal);
        Assert.Contains("<NuGetAudit>true</NuGetAudit>", directoryProps, StringComparison.Ordinal);
        Assert.Contains("<NuGetAuditMode>all</NuGetAuditMode>", directoryProps, StringComparison.Ordinal);
        AssertNuGetAuditCodeSeparation(directoryProps);
        Assert.Contains("CloudScribeNuGetAuditPipeline)' == 'true'", directoryProps, StringComparison.Ordinal);
        Assert.Contains("CloudScribeNuGetAuditPipeline)' != 'true'", directoryProps, StringComparison.Ordinal);
        foreach (string auditWrapper in new[] { linuxAudit, windowsAudit })
        {
            Assert.Contains("--locked-mode", auditWrapper, StringComparison.Ordinal);
            Assert.Matches(@"(?<![A-Za-z0-9-])--force(?![A-Za-z0-9-])", auditWrapper);
            Assert.DoesNotContain("--force-evaluate", auditWrapper, StringComparison.Ordinal);
            Assert.Contains("--no-http-cache", auditWrapper, StringComparison.Ordinal);
            Assert.Contains("CloudScribeNuGetAuditPipeline=true", auditWrapper, StringComparison.Ordinal);
            AssertNuGetAuditRetryBoundary(auditWrapper);
            Assert.Contains("--vulnerable", auditWrapper, StringComparison.Ordinal);
            Assert.DoesNotContain("NuGetAudit=false", auditWrapper, StringComparison.Ordinal);
            Assert.DoesNotContain("--ignore-failed-sources", auditWrapper, StringComparison.Ordinal);
        }
        AssertAuditTargetContainment(linuxAudit, windowsAudit);
        Assert.Contains("deprecationReasons", validator, StringComparison.Ordinal);
        Assert.Contains("vulnerabilities", validator, StringComparison.Ordinal);
        Assert.Contains("MAX_SCAN_FILES", validator, StringComparison.Ordinal);
        Assert.Contains("symbolic-link", validator, StringComparison.Ordinal);
        Assert.Contains("path_is_link_or_reparse", physicalDirectory, StringComparison.Ordinal);
        Assert.Contains("forbidden_roots", physicalDirectory, StringComparison.Ordinal);
        Assert.Contains("--forbid-root", linux, StringComparison.Ordinal);
        Assert.Contains("--forbid-root", windows, StringComparison.Ordinal);
    }

    private static void AssertPaperEditorPointerFocusRegression(string window, string capture, string validator)
    {
        Assert.Contains("x:Key=\"PaperTextBoxTheme\"", window, StringComparison.Ordinal);
        Assert.Contains("TargetType=\"controls:PaperTextBox\"", window, StringComparison.Ordinal);
        Assert.Contains("BasedOn=\"{StaticResource {x:Type TextBox}}\"", window, StringComparison.Ordinal);
        Assert.Contains("ControlTemplate TargetType=\"controls:PaperTextBox\"", window, StringComparison.Ordinal);
        Assert.Contains("Name=\"PART_PaperSurface\"", window, StringComparison.Ordinal);
        Assert.Contains("Theme=\"{StaticResource PaperTextBoxTheme}\"", window, StringComparison.Ordinal);
        Assert.Contains("^:pointerover", window, StringComparison.Ordinal);
        Assert.Contains("^:focus", window, StringComparison.Ordinal);
        Assert.Contains("SetVisualCapturePointerOver", capture, StringComparison.Ordinal);
        Assert.Contains("SelectionForegroundBrush", capture, StringComparison.Ordinal);
        Assert.Contains("PlaceholderForeground", capture, StringComparison.Ordinal);
        Assert.Contains("PART_PaperSurface", capture, StringComparison.Ordinal);
        Assert.DoesNotContain("PART_BorderElement is unavailable during visual capture", capture, StringComparison.Ordinal);
        Assert.Contains("01-full-follow-system-dark-pointer-focus", capture, StringComparison.Ordinal);
        Assert.Contains("07-compact-follow-system-light-pointer-focus", capture, StringComparison.Ordinal);
        Assert.Contains("include_center_brightness=True", validator, StringComparison.Ordinal);
        Assert.Contains("center_bright_fraction", validator, StringComparison.Ordinal);
    }

    private static void AssertWindowsBuildLauncherContract(string windowsBuildLauncher, string windowsBuildGuide)
    {
        Assert.Contains("-ExecutionPolicy Bypass", windowsBuildLauncher, StringComparison.Ordinal);
        Assert.Contains("if errorlevel 1 goto :publish_failed_pop", windowsBuildLauncher, StringComparison.Ordinal);
        Assert.Contains("CloudScribe.exe", windowsBuildLauncher, StringComparison.Ordinal);
        Assert.Contains("RUN-CLOUDSCRIBE.cmd", windowsBuildLauncher, StringComparison.Ordinal);
        Assert.Contains("CLOUDSCRIBE_NO_OPEN", windowsBuildLauncher, StringComparison.Ordinal);
        Assert.Contains("src\\CloudScribe.App\\CloudScribe.App.csproj", windowsBuildGuide, StringComparison.Ordinal);
        Assert.Contains("MachinePolicy or UserPolicy", windowsBuildGuide, StringComparison.Ordinal);
    }

    private static void AssertNuGetAuditRetryBoundary(string auditWrapper)
    {
        Assert.Contains("NU1900", auditWrapper, StringComparison.Ordinal);
        Assert.Contains("NU1301", auditWrapper, StringComparison.Ordinal);
        Assert.DoesNotContain("NU1901|NU1902|NU1903|NU1904", auditWrapper, StringComparison.Ordinal);
    }

    private static void AssertNuGetAuditCodeSeparation(string directoryProps)
    {
        Assert.Contains("<CloudScribeNuGetAuditInfrastructureCodes>NU1900;NU1905</CloudScribeNuGetAuditInfrastructureCodes>", directoryProps, StringComparison.Ordinal);
        Assert.Contains("<CloudScribeNuGetAuditFindingCodes>NU1901;NU1902;NU1903;NU1904</CloudScribeNuGetAuditFindingCodes>", directoryProps, StringComparison.Ordinal);
        Assert.Contains("$(WarningsAsErrors);$(CloudScribeNuGetAuditInfrastructureCodes)", directoryProps, StringComparison.Ordinal);
        Assert.Contains("<WarningsNotAsErrors>$(WarningsNotAsErrors);$(CloudScribeNuGetAuditFindingCodes)</WarningsNotAsErrors>", directoryProps, StringComparison.Ordinal);
    }

    private static void AssertAuditTargetContainment(string linuxAudit, string windowsAudit)
    {
        Assert.Contains("realpath -- \"$project_path\"", linuxAudit, StringComparison.Ordinal);
        Assert.Contains("dotnet restore \"$project_path\"", linuxAudit, StringComparison.Ordinal);
        Assert.Contains("[IO.FileAttributes]::ReparsePoint", windowsAudit, StringComparison.Ordinal);
        Assert.Contains("$cursor = $projectItem.Directory", windowsAudit, StringComparison.Ordinal);
        Assert.Contains("projectItem.Attributes", windowsAudit, StringComparison.Ordinal);
        Assert.Contains("Audit target must resolve beneath the CloudScribe repository", windowsAudit, StringComparison.Ordinal);
        Assert.DoesNotContain("$cursor = $projectItem\n", windowsAudit, StringComparison.Ordinal);
        Assert.Contains("dotnet restore $projectPath", windowsAudit, StringComparison.Ordinal);
    }

    private static void AssertContrastAtLeast(
        Avalonia.Media.Color foreground,
        Avalonia.Media.Color background,
        double minimum,
        string description)
    {
        double contrast = ContrastRatio(foreground, background);
        Assert.True(contrast >= minimum, $"{description} contrast {contrast:F2}:1 is below {minimum:F2}:1");
    }

    private static double ContrastRatio(Avalonia.Media.Color first, Avalonia.Media.Color second)
    {
        double firstLuminance = RelativeLuminance(first);
        double secondLuminance = RelativeLuminance(second);
        double lighter = Math.Max(firstLuminance, secondLuminance);
        double darker = Math.Min(firstLuminance, secondLuminance);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double RelativeLuminance(Avalonia.Media.Color color)
    {
        static double Linearize(byte component)
        {
            double channel = component / 255.0;
            return channel <= 0.04045
                ? channel / 12.92
                : Math.Pow((channel + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Linearize(color.R))
            + (0.7152 * Linearize(color.G))
            + (0.0722 * Linearize(color.B));
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CloudScribe.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("CloudScribe repository root was not found from the test output path.");
    }

}
