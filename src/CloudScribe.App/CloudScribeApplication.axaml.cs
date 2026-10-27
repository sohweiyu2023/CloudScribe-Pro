using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using CloudScribe.Application.Logging;
using CloudScribe.Application.Startup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CloudScribe.App;

public sealed partial class CloudScribeApplication : Avalonia.Application
{
    private readonly IHost? _host;
    private readonly StartupPhaseTimings? _timings;

    public CloudScribeApplication()
    {
    }

    internal CloudScribeApplication(IHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    internal CloudScribeApplication(IHost host, StartupPhaseTimings timings) : this(host)
    {
        _timings = timings;
    }

    public override void Initialize()
    {
        _timings?.Start(StartupPhase.FrameworkInitialization);
        AvaloniaXamlLoader.Load(this);
        Diagnostics.NativeUiDiagnosticLog.ApplicationInitialized();
        _timings?.Complete(StartupPhase.FrameworkInitialization);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        IHost host = _host ?? throw new InvalidOperationException("The CloudScribe host was not assigned before application startup.");
        ILogger? startupLogger = null;
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _timings?.Start(StartupPhase.MainWindowResolution);
            MainWindow window = host.Services.GetRequiredService<MainWindow>();
            _timings?.Complete(StartupPhase.MainWindowResolution);
            desktop.MainWindow = window;
            desktop.Exit += (_, _) => window.DisposeDataContext();
            startupLogger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("CloudScribe.Startup");
        }

        base.OnFrameworkInitializationCompleted();
        if (startupLogger is not null)
        {
            CloudScribeLog.ApplicationReady(startupLogger);
            _timings?.Complete(StartupPhase.Readiness);
        }
    }
}
