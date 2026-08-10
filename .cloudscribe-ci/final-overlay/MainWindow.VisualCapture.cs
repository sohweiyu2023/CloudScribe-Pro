using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using CloudScribe.App.ViewModels;
using CloudScribe.Domain.Configuration;

namespace CloudScribe.App;

public partial class MainWindow
{
    private const double CaptureBitmapDpi = 96.0;
    private const string CaptureSurface = "Avalonia.RenderTargetBitmap";

    private async void OnStage2VisualCaptureOpened(object? sender, EventArgs e)
    {
        Opened -= OnStage2VisualCaptureOpened;
        try
        {
            if (DataContext is not ShellViewModel viewModel)
            {
                throw new InvalidOperationException("The Stage 2 capture shell is unavailable.");
            }

            string outputDirectory = Stage2VisualCapture.GetRequiredOutputDirectory();
            Directory.CreateDirectory(outputDirectory);
            await CaptureStage2VisualMatrixAsync(viewModel, outputDirectory).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            try
            {
                string? outputDirectory = Stage2VisualCapture.GetConfiguredOutputDirectory();
                if (!string.IsNullOrWhiteSpace(outputDirectory))
                {
                    Directory.CreateDirectory(outputDirectory);
                    await File.WriteAllTextAsync(
                        Path.Combine(outputDirectory, "capture-error.txt"),
                        ex.ToString()).ConfigureAwait(true);
                }
            }
            catch (Exception reportingException)
            {
                Trace.TraceError(
                    "Stage 2 visual capture error reporting failed after the capture process faulted: {0}",
                    reportingException);
            }
            finally
            {
                Environment.ExitCode = 1;
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
                captureCase.FocusEditor,
                captureCase.ReducedMotion,
                captureCase.TypographyScale,
                ComputeSha256(path)));
        }

        string sourceManifestSha256 = RequireSourceManifestSha256();
        string repositoryVersion = typeof(MainWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? throw new InvalidOperationException("Application informational version is unavailable during visual capture.");
        string runtimePlatform = OperatingSystem.IsWindows()
            ? "Windows"
            : OperatingSystem.IsLinux()
                ? "Linux"
                : OperatingSystem.IsMacOS()
                    ? "macOS"
                    : "Unknown";
        string manifest = JsonSerializer.Serialize(new
        {
            schema = "cloudscribe-stage2-visual-evidence-1.1",
            generated_at_utc = TimeProvider.System.GetUtcNow(),
            repository_version = repositoryVersion,
            source_manifest_sha256 = sourceManifestSha256,
            runtime_platform = runtimePlatform,
            runtime_framework = RuntimeInformation.FrameworkDescription,
            runtime_evidence = true,
            concept_art = false,
            capture_surface = CaptureSurface,
            capture_bitmap_dpi = CaptureBitmapDpi,
            capture_process_id = Environment.ProcessId,
            capture_executable = Environment.ProcessPath,
            captures = results,
        }, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "visual-evidence-manifest.json"),
            manifest).ConfigureAwait(true);
    }

    private async Task ConfigureCaptureCaseAsync(ShellViewModel viewModel, VisualCaptureCase captureCase)
    {
        Width = captureCase.Width;
        Height = captureCase.Height;
        RequestedThemeVariant = captureCase.Theme;
        viewModel.SetTypographyScale(captureCase.TypographyScale);
        viewModel.SetReducedMotion(captureCase.ReducedMotion);
        viewModel.SetNavigationDrawer(captureCase.OpenNavigationDrawer);
        viewModel.SetLifecycleState(captureCase.LifecycleState);
        viewModel.NavigateTo(captureCase.Route);
        if (captureCase.FocusReading)
        {
            viewModel.FocusReadingSurface();
        }
        else if (captureCase.FocusEditor)
        {
            viewModel.FocusEditorSurface();
        }
        else
        {
            viewModel.ClearStage2Focus();
        }

        InvalidateMeasure();
        InvalidateArrange();
        await Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Render);
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

    private string RequireSourceManifestSha256()
    {
        string root = AppContext.BaseDirectory;
        for (int depth = 0; depth < 8; depth++)
        {
            string candidate = Path.Combine(root, "SHA256SUMS.txt");
            if (File.Exists(candidate))
            {
                using FileStream stream = File.OpenRead(candidate);
                return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            }

            DirectoryInfo? parent = Directory.GetParent(root);
            if (parent is null)
            {
                break;
            }

            root = parent.FullName;
        }

        throw new InvalidOperationException("SHA256SUMS.txt is unavailable for Stage 2 visual evidence capture.");
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
        bool ReducedMotion,
        double TypographyScale,
        string Sha256);
}
