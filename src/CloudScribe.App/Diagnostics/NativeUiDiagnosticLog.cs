using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using CloudScribe.Infrastructure.Diagnostics;

namespace CloudScribe.App.Diagnostics;

/// <summary>Content-free native lifecycle metadata using the existing bounded, local bootstrap sink.</summary>
internal static class NativeUiDiagnosticLog
{
    private static readonly string Session = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
    private static readonly string Identity = $"session={Session}; runtime={Environment.Version}; module={typeof(NativeUiDiagnosticLog).Module.ModuleVersionId}";

    internal static void Action(NativeUiOperation operation, NativeUiPhase phase, Exception? error = null) =>
        BootstrapDiagnosticLog.Write("native.action", $"{Identity}; {FormatAction(operation, phase, error)}");

    // Accept enums and exception identity only. Never accept a filename, status message, document or exception message.
    internal static string FormatAction(NativeUiOperation operation, NativeUiPhase phase, Exception? error) =>
        $"operation={(Enum.IsDefined(operation) ? operation.ToString() : "Unknown")}; phase={(Enum.IsDefined(phase) ? phase.ToString() : "Unknown")}; exceptionCategory={(error is null ? "none" : SafeExceptionSummarizer.Classify(error).ToString())}; hresult={error?.HResult ?? 0}";

    internal static void ApplicationInitialized() => RecordResources("application.initialized", null);

    internal static void Attach(Window window)
    {
        DispatcherTimer? settled = null;
        window.Opened += (_, _) =>
        {
            RecordResources("window.opened", window);
            Dispatcher.UIThread.Post(() => RecordResources("window.first-render", window), DispatcherPriority.Render);
            settled = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            settled.Tick += (_, _) => { settled.Stop(); RecordResources("window.settled", window); };
            settled.Start();
        };
        window.Activated += (_, _) => RecordResources("window.activated", window);
        window.PropertyChanged += (_, args) =>
        {
            if (args.Property == Window.WindowStateProperty)
                RecordResources("window.state-changed", window);
        };
        window.Closed += (_, _) => { settled?.Stop(); RecordResources("window.closed", window); };
    }

    private static void RecordResources(string eventName, Window? window)
    {
        try
        {
            Avalonia.Application? app = Avalonia.Application.Current;
            object? resource = app?.Resources["Brush.ShellGradient"];
            GradientBrush? gradient = resource as GradientBrush;
            string stops = gradient is null ? "missing" : string.Join(',', gradient.GradientStops.Select(stop => stop.Color.ToString()));
            BootstrapDiagnosticLog.Write(eventName,
                $"{Identity}; applicationType={app?.GetType().FullName ?? "none"}; styles={app?.Styles.Count ?? 0}; shellStops={stops}; opacity={window?.Opacity.ToString(CultureInfo.InvariantCulture) ?? "none"}; transparency={window?.ActualTransparencyLevel.ToString() ?? "none"}; state={window?.WindowState.ToString() ?? "none"}");
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            BootstrapDiagnosticLog.Write("native.metadata-unavailable", $"{Identity}; exceptionCategory={SafeExceptionSummarizer.Classify(error)}; hresult={error.HResult}");
        }
    }
}
