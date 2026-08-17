using CloudScribe.App.ViewModels;
using CloudScribe.Application.Documents;
using CloudScribe.Application.Pricing;
using CloudScribe.Application.Providers;
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
        builder.Services.AddSingleton(serviceProvider =>
        {
            ShellViewModel viewModel = ActivatorUtilities.CreateInstance<ShellViewModel>(serviceProvider);
            viewModel.ConfigureStage3DocumentWorkflow(
                serviceProvider.GetRequiredService<IDocumentLibrary>(),
                serviceProvider.GetRequiredService<DocumentAutosaveCoordinator>());
            viewModel.ConfigureStage3ImportWorkflow(
                serviceProvider.GetRequiredService<ILocalDocumentImporter>(),
                serviceProvider.GetRequiredService<DocumentPreprocessor>());
            viewModel.ConfigureStage4PricingHistory(
                serviceProvider.GetRequiredService<IPricingCatalogHistoryStore>(),
                serviceProvider.GetRequiredService<IPricingContractOverrideStore>(),
                serviceProvider.GetRequiredService<IProviderAccountStore>(),
                serviceProvider.GetRequiredService<IProviderCapabilitySnapshotStore>());
            viewModel.ScheduleDocumentWorkspaceStart();
            viewModel.SchedulePricingHistoryStart();
            return viewModel;
        });
        builder.Services.AddSingleton(serviceProvider =>
        {
            MainWindow window = ActivatorUtilities.CreateInstance<MainWindow>(serviceProvider);
            DocumentWindowBehavior.Attach(window);
            return window;
        });

        return builder.Build();
    }
}
