using Microsoft.Extensions.Logging;

namespace CloudScribe.Application.Logging;

public static class CloudScribeEventIds
{
    public static readonly EventId ApplicationStarting = new(1000, nameof(ApplicationStarting));
    public static readonly EventId ApplicationReady = new(1001, nameof(ApplicationReady));
    public static readonly EventId ApplicationStopping = new(1002, nameof(ApplicationStopping));
    public static readonly EventId ApplicationStartupTimedOut = new(1003, nameof(ApplicationStartupTimedOut));
    public static readonly EventId ApplicationStartupFailed = new(1004, nameof(ApplicationStartupFailed));
    public static readonly EventId ApplicationShutdownFailed = new(1005, nameof(ApplicationShutdownFailed));
    public static readonly EventId DiagnosticsDirectorySelected = new(1006, nameof(DiagnosticsDirectorySelected));
    public static readonly EventId PrimaryInstanceAcquired = new(1007, nameof(PrimaryInstanceAcquired));
    public static readonly EventId SecondaryActivationForwarded = new(1008, nameof(SecondaryActivationForwarded));
    public static readonly EventId HostStarting = new(1009, nameof(HostStarting));
    public static readonly EventId HostStarted = new(1010, nameof(HostStarted));
    public static readonly EventId InitializerStarting = new(1011, nameof(InitializerStarting));
    public static readonly EventId InitializerCompleted = new(1012, nameof(InitializerCompleted));
    public static readonly EventId DesktopLifetimeStarting = new(1013, nameof(DesktopLifetimeStarting));
    public static readonly EventId DesktopLifetimeExited = new(1014, nameof(DesktopLifetimeExited));
    public static readonly EventId HostStopping = new(1015, nameof(HostStopping));
    public static readonly EventId HostStopped = new(1016, nameof(HostStopped));
    public static readonly EventId SingleInstanceCoordinatorDisposed = new(1017, nameof(SingleInstanceCoordinatorDisposed));
    public static readonly EventId DatabaseInitialized = new(1100, nameof(DatabaseInitialized));
    public static readonly EventId ActivationReceived = new(1200, nameof(ActivationReceived));
    public static readonly EventId ActivationDispatchFailed = new(1201, nameof(ActivationDispatchFailed));
    public static readonly EventId DiagnosticsWriteFailed = new(1300, nameof(DiagnosticsWriteFailed));
    public static readonly EventId SupportBundleCreated = new(1400, nameof(SupportBundleCreated));
    public static readonly EventId SupportBundlePreviewFailed = new(1401, nameof(SupportBundlePreviewFailed));
    public static readonly EventId SupportBundlePreviewCompleted = new(1402, nameof(SupportBundlePreviewCompleted));
    public static readonly EventId ShellRouteChanged = new(1500, nameof(ShellRouteChanged));
    public static readonly EventId WorkspaceLifecycleChanged = new(1501, nameof(WorkspaceLifecycleChanged));
    public static readonly EventId ThemeChanged = new(1502, nameof(ThemeChanged));
    public static readonly EventId FocusReadingChanged = new(1503, nameof(FocusReadingChanged));
    public static readonly EventId AdaptiveLayoutChanged = new(1504, nameof(AdaptiveLayoutChanged));
}
