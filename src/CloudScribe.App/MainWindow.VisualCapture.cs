using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CloudScribe.App.Design;
using CloudScribe.App.Navigation;
using CloudScribe.App.ViewModels;
using CloudScribe.Infrastructure.Files;

namespace CloudScribe.App;

public sealed partial class MainWindow
{
    private const double CaptureBitmapDpi = 96.0;
    private const string CaptureSurface = "Avalonia.RenderTargetBitmap";
    private const string TypographyScaleMethod = "semantic-resource-multiplier";
    private static readonly JsonSerializerOptions VisualEvidenceJsonOptions = new()
    {
        WriteIndented = true,
    };

    private static bool IsVisualCaptureRequested()
    {
        string? mode = Environment.GetEnvironmentVariable("CLOUDSCRIBE_STAGE2_CAPTURE_MODE");
        string? outputDirectory = Environment.GetEnvironmentVariable("CLOUDSCRIBE_STAGE2_CAPTURE_DIR");
        string? sourceManifestSha256 = Environment.GetEnvironmentVariable("CLOUDSCRIBE_SOURCE_MANIFEST_SHA256");
        return string.Equals(mode, "1", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(outputDirectory)
            && sourceManifestSha256 is { Length: 64 }
            && sourceManifestSha256.All(static character => Uri.IsHexDigit(character));
    }

    private async void OnVisualCaptureOpened(object? sender, EventArgs eventArgs)
    {
        string? outputDirectory = Environment.GetEnvironmentVariable("CLOUDSCRIBE_STAGE2_CAPTURE_DIR");
        if (string.IsNullOrWhiteSpace(outputDirectory) || ViewModel is not { } viewModel)
        {
            return;
        }

        Opened -= OnVisualCaptureOpened;
        string? physicalOutputDirectory = null;
        try
        {
            physicalOutputDirectory = PhysicalDirectoryPolicy.EnsureExistsWithoutLinks(
                outputDirectory,
                "Stage 2 visual evidence cannot be written through a symbolic-link or reparse-point directory.");
            await CaptureStage2VisualMatrixAsync(viewModel, physicalOutputDirectory).ConfigureAwait(true);
        }
        catch (Exception exception) when (!IsFatalVisualCaptureException(exception))
        {
            string message = exception.Message
                .Replace('\r', ' ')
                .Replace('\n', ' ');
            if (message.Length > 1000)
            {
                message = message[..1000];
            }

            if (physicalOutputDirectory is not null)
            {
                try
                {
                    await WriteNewTextFileAsync(
                        Path.Combine(physicalOutputDirectory, "capture-error.txt"),
                        $"{exception.GetType().Name}: {message}").ConfigureAwait(true);
                }
                catch (Exception reportException) when (!IsFatalVisualCaptureException(reportException))
                {
                    // The bounded capture process will fail because its evidence manifest is absent.
                    // Never let secondary error-report I/O escape an async-void UI event handler.
                }
            }
        }
        finally
        {
            Close();
        }
    }

    private async Task CaptureStage2VisualMatrixAsync(ShellViewModel viewModel, string outputDirectory)
    {
        List<VisualCaptureResult> results = [];
        foreach (VisualCaptureCase captureCase in VisualCaptureCase.Matrix)
        {
            await ConfigureCaptureCaseAsync(viewModel, captureCase).ConfigureAwait(true);
            string fileName = captureCase.Name + ".png";
            string path = Path.Combine(outputDirectory, fileName);
            EditorVisualAudit editorVisualAudit = CaptureEditorVisualAudit(viewModel.EffectivePalette);
            PixelSize capturedSize = CaptureWindow(path, captureCase.Width, captureCase.Height);
            results.Add(new(
                captureCase.Name,
                fileName,
                capturedSize.Width,
                capturedSize.Height,
                captureCase.Theme.ToString(),
                captureCase.LifecycleState.ToString(),
                captureCase.Route.ToString(),
                captureCase.FocusReading,
                captureCase.OpenNavigationDrawer,
                captureCase.FocusEditor || captureCase.FocusReading,
                captureCase.PointerOverEditor,
                captureCase.SystemUsesDark ?? viewModel.SystemUsesDark,
                captureCase.ReducedMotion,
                captureCase.TypographyScale,
                editorVisualAudit,
                ComputeSha256(path)));
        }

        string sourceManifestSha256 = RequireSourceManifestSha256();
        string repositoryVersion = typeof(MainWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? throw new InvalidOperationException("Application informational version is unavailable during visual capture.");
        string runtimePlatform = GetRuntimePlatform();
        string manifest = JsonSerializer.Serialize(new
        {
            schema = "cloudscribe-stage2-visual-evidence-1.2",
            generated_at_utc = TimeProvider.System.GetUtcNow(),
            repository_version = repositoryVersion,
            source_manifest_sha256 = sourceManifestSha256,
            runtime_platform = runtimePlatform,
            runtime_framework = RuntimeInformation.FrameworkDescription,
            runtime_evidence = true,
            concept_art = false,
            capture_surface = CaptureSurface,
            capture_bitmap_dpi_x = CaptureBitmapDpi,
            capture_bitmap_dpi_y = CaptureBitmapDpi,
            typography_scale_method = TypographyScaleMethod,
            operating_system_text_scale_verified = false,
            mixed_dpi_verified = false,
            windows_accessibility_verified = false,
            cases = results,
        }, VisualEvidenceJsonOptions);
        await WriteNewTextFileAsync(
            Path.Combine(outputDirectory, "visual-evidence-manifest.json"),
            manifest).ConfigureAwait(true);
    }

    private static string GetRuntimePlatform() =>
        OperatingSystem.IsWindows()
            ? "Windows"
            : OperatingSystem.IsLinux()
                ? "Linux"
                : OperatingSystem.IsMacOS()
                    ? "macOS"
                    : "Unknown";

    private static bool IsFatalVisualCaptureException(Exception exception) => exception is
        OutOfMemoryException
        or StackOverflowException
        or AccessViolationException;

    private static async Task WriteNewTextFileAsync(string path, string content)
    {
        FileStream stream = new(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await using (stream.ConfigureAwait(false))
        using (StreamWriter writer = new(stream, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 4096, leaveOpen: true))
        {
            await writer.WriteAsync(content).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
    }

    private async Task ConfigureCaptureCaseAsync(ShellViewModel viewModel, VisualCaptureCase captureCase)
    {
        WindowState = WindowState.Normal;
        Width = captureCase.Width;
        Height = captureCase.Height;
        ApplyTypographyScale(captureCase.TypographyScale);
        viewModel.UpdateViewport(captureCase.Width / captureCase.TypographyScale);
        if (captureCase.SystemUsesDark is bool systemUsesDark)
        {
            viewModel.UpdateSystemTheme(systemUsesDark, highContrast: false);
        }
        viewModel.ConfigureVisualEvidence(
            captureCase.Route,
            captureCase.Theme,
            captureCase.LifecycleState,
            captureCase.FocusReading,
            captureCase.ReducedMotion);
        if (captureCase.TypographyScale > 1.0)
        {
            viewModel.StatusMessage = $"Typography scale preview · {captureCase.TypographyScale:P0}";
        }

        if (captureCase.OpenNavigationDrawer)
        {
            viewModel.ToggleNavigationDrawerCommand.Execute(null);
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            InvalidateMeasure();
            InvalidateArrange();
            InvalidateVisual();
            DocumentEditor.SetVisualCapturePointerOver(captureCase.PointerOverEditor);
            if (captureCase.FocusEditor)
            {
                DocumentEditor.Focus();
                DocumentEditor.SelectionStart = 0;
                DocumentEditor.SelectionEnd = Math.Min(48, DocumentEditor.Text?.Length ?? 0);
            }
            else if (!captureCase.FocusReading)
            {
                FocusManager?.Focus(null!, Avalonia.Input.NavigationMethod.Unspecified, Avalonia.Input.KeyModifiers.None);
            }
        }, DispatcherPriority.Render).GetTask().ConfigureAwait(true);
        await Task.Delay(TimeSpan.FromMilliseconds(viewModel.ReducedMotionEnabled ? 80 : 220)).ConfigureAwait(true);
    }




    private EditorVisualAudit CaptureEditorVisualAudit(CosmicThemePalette palette)
    {
        Border? borderElement = DocumentEditor
            .GetVisualDescendants()
            .OfType<Border>()
            .FirstOrDefault(static border => string.Equals(border.Name, "PART_PaperSurface", StringComparison.Ordinal));
        if (borderElement is null)
        {
            throw new InvalidOperationException("The document editor PART_PaperSurface is unavailable during visual capture.");
        }

        Color surface = ResolveSurfaceColor(borderElement.Background, palette.Paper, "editor surface background");
        return new(
            DocumentEditor.IsFocused,
            ToRgbHex(RequireOpaqueSolidColor(DocumentEditor.Foreground, "editor foreground")),
            ToRgbHex(surface),
            ToRgbHex(RequireOpaqueSolidColor(DocumentEditor.CaretBrush, "editor caret")),
            ToRgbHex(RequireOpaqueSolidColor(DocumentEditor.SelectionBrush, "editor selection background")),
            ToRgbHex(RequireOpaqueSolidColor(DocumentEditor.SelectionForegroundBrush, "editor selection foreground")),
            ToRgbHex(RequireOpaqueSolidColor(DocumentEditor.PlaceholderForeground, "editor placeholder foreground")));
    }

    private static Color RequireOpaqueSolidColor(IBrush? brush, string label)
    {
        if (brush is not ISolidColorBrush solidBrush || solidBrush.Color.A != byte.MaxValue)
        {
            throw new InvalidOperationException($"Stage 2 visual evidence requires an opaque solid {label} brush.");
        }

        return solidBrush.Color;
    }

    private static Color ResolveSurfaceColor(IBrush? brush, Color fallback, string label)
    {
        if (brush is not ISolidColorBrush solidBrush)
        {
            throw new InvalidOperationException($"Stage 2 visual evidence requires a solid {label} brush.");
        }

        Color foreground = solidBrush.Color;
        if (foreground.A == byte.MaxValue)
        {
            return foreground;
        }

        if (fallback.A != byte.MaxValue)
        {
            throw new InvalidOperationException($"Stage 2 visual evidence requires an opaque fallback for {label}.");
        }

        double alpha = foreground.A / 255.0;
        return Color.FromArgb(
            byte.MaxValue,
            BlendChannel(foreground.R, fallback.R, alpha),
            BlendChannel(foreground.G, fallback.G, alpha),
            BlendChannel(foreground.B, fallback.B, alpha));
    }

    private static byte BlendChannel(byte foreground, byte background, double alpha) =>
        (byte)Math.Round((foreground * alpha) + (background * (1.0 - alpha)), MidpointRounding.AwayFromZero);

    private static string ToRgbHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static string RequireSourceManifestSha256()
    {
        string? value = Environment.GetEnvironmentVariable("CLOUDSCRIBE_SOURCE_MANIFEST_SHA256");
        if (value is null || value.Length != 64 || value.Any(static character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidOperationException(
                "CLOUDSCRIBE_SOURCE_MANIFEST_SHA256 must contain the 64-character SHA-256 of the current SHA256SUMS.txt.");
        }

        return value.ToLowerInvariant();
    }

    private static readonly Dictionary<string, double> TypographyBaseValues =
        new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["Typography.Caption"] = 10,
            ["Typography.Meta"] = 11,
            ["Typography.Small"] = 12,
            ["Typography.Body"] = 14,
            ["Typography.Navigation"] = 16,
            ["Typography.BodyLarge"] = 17,
            ["Typography.Large"] = 18,
            ["Typography.SectionTitle"] = 20,
            ["Typography.Title"] = 22,
            ["Typography.DialogTitle"] = 24,
            ["Typography.Display"] = 30,
            ["Typography.Editor"] = 18,
        };

    private static void ApplyTypographyScale(double scale)
    {
        if (scale is < 1.0 or > 2.0)
        {
            throw new ArgumentOutOfRangeException(nameof(scale), scale, "Visual evidence typography scale must be between 100% and 200%.");
        }

        Avalonia.Application application = Avalonia.Application.Current
            ?? throw new InvalidOperationException("Avalonia application resources are unavailable during visual capture.");
        foreach ((string key, double baseValue) in TypographyBaseValues)
        {
            application.Resources[key] = baseValue * scale;
        }
    }

    private PixelSize CaptureWindow(string path, double width, double height)
    {
        PixelSize size = new(
            Math.Max(1, (int)Math.Ceiling(width)),
            Math.Max(1, (int)Math.Ceiling(height)));
        if (Content is not Control captureRoot)
        {
            throw new InvalidOperationException("The Stage 2 visual capture root is unavailable.");
        }

        double previousWidth = captureRoot.Width;
        double previousHeight = captureRoot.Height;
        try
        {
            Size targetSize = new(size.Width, size.Height);
            captureRoot.Width = targetSize.Width;
            captureRoot.Height = targetSize.Height;
            captureRoot.InvalidateMeasure();
            captureRoot.InvalidateArrange();
            captureRoot.Measure(targetSize);
            captureRoot.Arrange(new Rect(0, 0, targetSize.Width, targetSize.Height));
            if ((int)Math.Ceiling(captureRoot.Bounds.Width) != size.Width ||
                (int)Math.Ceiling(captureRoot.Bounds.Height) != size.Height)
            {
                throw new InvalidOperationException(
                    $"Stage 2 visual capture root arranged to {captureRoot.Bounds.Width}x{captureRoot.Bounds.Height}; expected {size.Width}x{size.Height}.");
            }

            using RenderTargetBitmap bitmap = new(size, new Vector(CaptureBitmapDpi, CaptureBitmapDpi));
            bitmap.Render(captureRoot);
            using FileStream stream = new(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough);
            bitmap.Save(stream, PngBitmapEncoderOptions.Default);
            stream.Flush(flushToDisk: true);
            return size;
        }
        finally
        {
            captureRoot.Width = previousWidth;
            captureRoot.Height = previousHeight;
            captureRoot.InvalidateMeasure();
            captureRoot.InvalidateArrange();
        }
    }

    private static string ComputeSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private sealed record VisualCaptureResult(
        string Name,
        string File,
        int Width,
        int Height,
        string Theme,
        string LifecycleState,
        string Route,
        bool FocusReading,
        bool NavigationDrawer,
        bool EditorFocused,
        bool PointerOverEditor,
        bool SystemUsesDark,
        bool ReducedMotion,
        double TypographyScale,
        EditorVisualAudit EditorVisualAudit,
        string Sha256);

    private sealed record EditorVisualAudit(
        bool Focused,
        string Foreground,
        string SurfaceBackground,
        string Caret,
        string SelectionBackground,
        string SelectionForeground,
        string PlaceholderForeground);

    private sealed record VisualCaptureCase(
        string Name,
        double Width,
        double Height,
        ThemePreference Theme,
        WorkspaceLifecycleState LifecycleState,
        AppRoute Route,
        bool FocusReading = false,
        bool OpenNavigationDrawer = false,
        bool FocusEditor = false,
        bool PointerOverEditor = false,
        bool? SystemUsesDark = null,
        bool ReducedMotion = false,
        double TypographyScale = 1.0)
    {
        public static IReadOnlyList<VisualCaptureCase> Matrix { get; } =
        [
            new("01-full-follow-system-dark-pointer-focus", 1600, 1000, ThemePreference.FollowSystem, WorkspaceLifecycleState.Offline, AppRoute.Studio, FocusEditor: true, PointerOverEditor: true, SystemUsesDark: true),
            new("02-standard-cosmic-paper-ready", 1280, 900, ThemePreference.CosmicPaper, WorkspaceLifecycleState.Ready, AppRoute.Studio),
            new("03-compact-cosmic-night-loading", 980, 820, ThemePreference.CosmicNight, WorkspaceLifecycleState.Loading, AppRoute.Studio),
            new("04-narrow-cosmic-paper-empty", 700, 820, ThemePreference.CosmicPaper, WorkspaceLifecycleState.Empty, AppRoute.Studio),
            new("05-standard-high-contrast-error", 1280, 900, ThemePreference.HighContrast, WorkspaceLifecycleState.Error, AppRoute.Studio, FocusEditor: true),
            new("06-full-focus-reading", 1500, 950, ThemePreference.CosmicNight, WorkspaceLifecycleState.Ready, AppRoute.Studio, FocusReading: true),
            new("07-compact-follow-system-light-pointer-focus", 980, 820, ThemePreference.FollowSystem, WorkspaceLifecycleState.Offline, AppRoute.Studio, FocusEditor: true, PointerOverEditor: true, SystemUsesDark: false),
            new("08-narrow-navigation-drawer", 700, 820, ThemePreference.CosmicNight, WorkspaceLifecycleState.Offline, AppRoute.Studio, OpenNavigationDrawer: true),
            new("09-standard-route-empty-state", 1280, 900, ThemePreference.CosmicPaper, WorkspaceLifecycleState.Ready, AppRoute.Library),
            new("10-standard-reduced-motion", 1280, 900, ThemePreference.CosmicNight, WorkspaceLifecycleState.Ready, AppRoute.Settings, ReducedMotion: true),
            new("11-full-text-scale-125", 1600, 1000, ThemePreference.CosmicPaper, WorkspaceLifecycleState.Ready, AppRoute.Studio, FocusEditor: true, TypographyScale: 1.25),
            new("12-full-text-scale-150", 1600, 1000, ThemePreference.CosmicPaper, WorkspaceLifecycleState.Ready, AppRoute.Studio, FocusEditor: true, TypographyScale: 1.5),
            new("13-full-text-scale-175", 1600, 1000, ThemePreference.CosmicPaper, WorkspaceLifecycleState.Ready, AppRoute.Studio, FocusEditor: true, TypographyScale: 1.75),
            new("14-full-text-scale-200", 1600, 1000, ThemePreference.CosmicPaper, WorkspaceLifecycleState.Ready, AppRoute.Studio, FocusEditor: true, TypographyScale: 2.0),
            new("15-narrow-text-scale-200", 700, 900, ThemePreference.CosmicPaper, WorkspaceLifecycleState.Ready, AppRoute.Studio, FocusEditor: true, TypographyScale: 2.0),
            new("16-minimum-window-cosmic-night", 520, 700, ThemePreference.CosmicNight, WorkspaceLifecycleState.Offline, AppRoute.Studio),
            new("17-minimum-window-text-scale-200", 520, 820, ThemePreference.CosmicPaper, WorkspaceLifecycleState.Ready, AppRoute.Studio, TypographyScale: 2.0),
        ];
    }
}
