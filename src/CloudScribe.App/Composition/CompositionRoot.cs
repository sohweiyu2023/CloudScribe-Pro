using CloudScribe.App.ViewModels;
using CloudScribe.Infrastructure.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CloudScribe.App.Composition;

public static class CompositionRoot
{
    public static IHost BuildHost()
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = [],
            ApplicationName = "CloudScribe Pro",
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.Configuration
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables("CLOUDSCRIBE_");

        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Information);

        builder.Services.AddCloudScribeInfrastructure(builder.Configuration);
        builder.Services.AddSingleton<ShellViewModel>();
        builder.Services.AddSingleton<MainWindow>();

        return builder.Build();
    }
}
