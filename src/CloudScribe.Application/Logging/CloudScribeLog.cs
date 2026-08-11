using CloudScribe.Application.Activation;
using Microsoft.Extensions.Logging;

namespace CloudScribe.Application.Logging;

public static partial class CloudScribeLog
{
    [LoggerMessage(EventId = 1000, Level = LogLevel.Information, Message = "CloudScribe Pro is starting in {Mode} mode.")]
    public static partial void ApplicationStarting(ILogger logger, string mode);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Information, Message = "CloudScribe Pro offline shell is ready.")]
    public static partial void ApplicationReady(ILogger logger);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Information, Message = "CloudScribe Pro is stopping.")]
    public static partial void ApplicationStopping(ILogger logger);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Error, Message = "CloudScribe Pro startup exceeded the bounded {TimeoutSeconds}-second deadline.")]
    public static partial void ApplicationStartupTimedOut(ILogger logger, int timeoutSeconds);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Critical, Message = "CloudScribe Pro could not complete startup.")]
    public static partial void ApplicationStartupFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1005, Level = LogLevel.Error, Message = "CloudScribe Pro shutdown encountered a bounded cleanup failure.")]
    public static partial void ApplicationShutdownFailed(ILogger logger, Exception exception);


    [LoggerMessage(EventId = 1006, Level = LogLevel.Information, Message = "Local diagnostic logging selected {LocationMode} directory {Directory}; fallback reason {FallbackReason}.")]
    public static partial void DiagnosticsDirectorySelected(ILogger logger, string directory, string locationMode, string fallbackReason);

    [LoggerMessage(EventId = 1007, Level = LogLevel.Information, Message = "This process is the primary CloudScribe instance with {ArgumentCount} startup arguments.")]
    public static partial void PrimaryInstanceAcquired(ILogger logger, int argumentCount);

    [LoggerMessage(EventId = 1008, Level = LogLevel.Information, Message = "Startup activation was forwarded to the existing CloudScribe instance with {ArgumentCount} arguments.")]
    public static partial void SecondaryActivationForwarded(ILogger logger, int argumentCount);

    [LoggerMessage(EventId = 1009, Level = LogLevel.Information, Message = "Generic Host startup is beginning.")]
    public static partial void HostStarting(ILogger logger);

    [LoggerMessage(EventId = 1010, Level = LogLevel.Information, Message = "Generic Host startup completed.")]
    public static partial void HostStarted(ILogger logger);

    [LoggerMessage(EventId = 1011, Level = LogLevel.Information, Message = "Application initializer {InitializerName} is starting.")]
    public static partial void InitializerStarting(ILogger logger, string initializerName);

    [LoggerMessage(EventId = 1012, Level = LogLevel.Information, Message = "Application initializer {InitializerName} completed.")]
    public static partial void InitializerCompleted(ILogger logger, string initializerName);

    [LoggerMessage(EventId = 1013, Level = LogLevel.Information, Message = "Avalonia desktop lifetime is starting.")]
    public static partial void DesktopLifetimeStarting(ILogger logger);

    [LoggerMessage(EventId = 1014, Level = LogLevel.Information, Message = "Avalonia desktop lifetime exited with code {ExitCode}.")]
    public static partial void DesktopLifetimeExited(ILogger logger, int exitCode);

    [LoggerMessage(EventId = 1015, Level = LogLevel.Information, Message = "Generic Host shutdown is beginning.")]
    public static partial void HostStopping(ILogger logger);

    [LoggerMessage(EventId = 1016, Level = LogLevel.Information, Message = "Generic Host shutdown completed.")]
    public static partial void HostStopped(ILogger logger);

    [LoggerMessage(EventId = 1017, Level = LogLevel.Information, Message = "Single-instance coordinator disposal completed.")]
    public static partial void SingleInstanceCoordinatorDisposed(ILogger logger);

    [LoggerMessage(EventId = 1100, Level = LogLevel.Information, Message = "Local observability database initialized.")]
    public static partial void DatabaseInitialized(ILogger logger);

    [LoggerMessage(EventId = 1200, Level = LogLevel.Information, Message = "Application activation routed from {Source} with {ArgumentCount} arguments.")]
    public static partial void ActivationReceived(ILogger logger, ActivationSource source, int argumentCount);

    [LoggerMessage(EventId = 1201, Level = LogLevel.Warning, Message = "A secondary activation subscriber failed; the listener remains available.")]
    public static partial void ActivationDispatchFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1300, Level = LogLevel.Warning, Message = "A bounded diagnostic record could not be written; user work is unaffected.")]
    public static partial void DiagnosticsWriteFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1400, Level = LogLevel.Information, Message = "A redacted support bundle was created.")]
    public static partial void SupportBundleCreated(ILogger logger);

    [LoggerMessage(EventId = 1401, Level = LogLevel.Warning, Message = "The redacted support-bundle preview could not be prepared; user work is unaffected.")]
    public static partial void SupportBundlePreviewFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1402, Level = LogLevel.Information, Message = "A redacted support-bundle preview found {FileCount} eligible files totalling {TotalSizeBytes} bytes.")]
    public static partial void SupportBundlePreviewCompleted(ILogger logger, int fileCount, long totalSizeBytes);

    [LoggerMessage(EventId = 1500, Level = LogLevel.Information, Message = "Shell route changed to {Route}.")]
    public static partial void ShellRouteChanged(ILogger logger, string route);

    [LoggerMessage(EventId = 1501, Level = LogLevel.Information, Message = "Workspace lifecycle changed to {LifecycleState}.")]
    public static partial void WorkspaceLifecycleChanged(ILogger logger, string lifecycleState);

    [LoggerMessage(EventId = 1502, Level = LogLevel.Information, Message = "Theme preference changed to {Theme}.")]
    public static partial void ThemeChanged(ILogger logger, string theme);

    [LoggerMessage(EventId = 1503, Level = LogLevel.Information, Message = "Focus Reading changed to {Enabled}.")]
    public static partial void FocusReadingChanged(ILogger logger, bool enabled);

    [LoggerMessage(EventId = 1504, Level = LogLevel.Information, Message = "Adaptive layout band changed to {LayoutMode}.")]
    public static partial void AdaptiveLayoutChanged(ILogger logger, string layoutMode);
}
