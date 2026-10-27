namespace CloudScribe.Application.Startup;

public enum StartupPhase
{
    Unknown,
    Bootstrap,
    HostComposition,
    CoreServiceResolution,
    DiagnosticsInitialization,
    PrimaryInstanceAcquisition,
    HostStart,
    InitializerResolution,
    ApplicationInitialization,
    BackendBudget,
    DesktopThreadScheduling,
    FrameworkInitialization,
    MainWindowResolution,
    Readiness,
}
