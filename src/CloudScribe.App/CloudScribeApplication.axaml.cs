using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using CloudScribe.Application.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CloudScribe.App;

public sealed partial class CloudScribeApplication : Avalonia.Application
{
    private readonly IHost? _host;

    public CloudScribeApplication()
    {
    }

    internal CloudScribeApplication(IHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        IHost host = _host ?? throw new InvalidOperationException("The CloudScribe host was not assigned before application startup.");
        ILogger? startupLogger = null;
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            MainWindow window = host.Services.GetRequiredService<MainWindow>();
            desktop.MainWindow = window;
            desktop.Exit += (_, _) => window.DisposeDataContext();
            startupLogger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("CloudScribe.Startup");
        }

        base.OnFrameworkInitializationCompleted();
        if (startupLogger is not null)
        {
            CloudScribeLog.ApplicationReady(startupLogger);
        }
    }
}
