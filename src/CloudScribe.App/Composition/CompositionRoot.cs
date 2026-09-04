using CloudScribe.App.ViewModels;
using CloudScribe.Application.Documents;
using CloudScribe.Application.Generation;
using CloudScribe.Application.Pricing;
using CloudScribe.Application.Providers;
using CloudScribe.Infrastructure.DependencyInjection;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Infrastructure.Safety;
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
        RegisterProductionServices(builder.Services);
        RegisterShell(builder.Services);

        return builder.Build();
    }

    private static void RegisterProductionServices(IServiceCollection services)
    {
        services.AddSingleton<HttpClient>();
        services.AddSingleton<GoogleGenerationProductionTransportFactory>();
        services.AddSingleton<GoogleGenerationProductionRuntimeEvidenceResolver>();
        services.AddSingleton<GoogleGenerationProductionExecutionContextResolver>();
        services.AddSingleton<GoogleGenerationProductionCurrentRequestStateOwner>();
        services.AddSingleton<GoogleGenerationProductionPendingApprovalStateOwner>();
        services.AddSingleton<GoogleGenerationProductionPendingApprovalPublisher>();
        services.AddSingleton<GoogleGenerationProductionCompileAndPrepareService>();
        services.AddSingleton<GoogleGenerationProductionPreparationCoordinator>();
        services.AddSingleton<GoogleGenerationProductionSubmissionStateOwner>();
        services.AddSingleton<GoogleGenerationProductionSpendApprovalService>();
        services.AddSingleton(serviceProvider =>
            new GoogleGenerationProductionRuntimeRequestSource(
                serviceProvider.GetRequiredService<IGoogleGenerationSpendAuthorizationStore>(),
                serviceProvider.GetRequiredService<GoogleGenerationProductionSubmissionStateOwner>().ResolveCurrentAsync));
        services.AddSingleton<Stage6GoogleGenerationShellBinder>();
        services.AddSingleton<Stage7VoiceLabCatalogShellBinder>();
        services.AddSingleton<Stage7VoiceLabAuditionShellBinder>();
    }

    private static void RegisterShell(IServiceCollection services)
    {
        services.AddSingleton(serviceProvider =>
        {
            ShellViewModel viewModel = ActivatorUtilities.CreateInstance<ShellViewModel>(serviceProvider);
            ConfigureWorkflows(viewModel, serviceProvider);
            ConfigureFinalProductionStages(viewModel, serviceProvider);
            viewModel.ApplyFinalReleasePresentation();
            viewModel.ScheduleDocumentWorkspaceStart();
            viewModel.SchedulePricingHistoryStart();
            return viewModel;
        });
        services.AddSingleton(serviceProvider =>
        {
            MainWindow window = ActivatorUtilities.CreateInstance<MainWindow>(serviceProvider);
            DocumentWindowBehavior.Attach(window);
            return window;
        });
    }

    private static void ConfigureWorkflows(ShellViewModel viewModel, IServiceProvider serviceProvider)
    {
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
        viewModel.ConfigureStage5GenerationDiagnostics(
            serviceProvider.GetRequiredService<GenerationSupportBundleExportCoordinator>(),
            currentPolicyAllowsDiagnostics: true);
    }

    private static void ConfigureFinalProductionStages(ShellViewModel viewModel, IServiceProvider serviceProvider)
    {
        serviceProvider.GetRequiredService<Stage6GoogleGenerationShellBinder>().Bind(
            viewModel,
            serviceProvider.GetRequiredService<GoogleGenerationProductionRuntimeRequestSource>().ResolveAsync);
        GoogleGenerationProductionPreparationCoordinator preparationCoordinator =
            serviceProvider.GetRequiredService<GoogleGenerationProductionPreparationCoordinator>();
        viewModel.ConfigureStage6GoogleGenerationPreparation(cancellationToken =>
            preparationCoordinator.PrepareCurrentAsync(cancellationToken));
        GoogleGenerationProductionSpendApprovalService approvalService =
            serviceProvider.GetRequiredService<GoogleGenerationProductionSpendApprovalService>();
        viewModel.ConfigureStage6GoogleGenerationSpendApproval((maximum, confirmed, cancellationToken) =>
            approvalService.ApproveExplicitAsync(
                new GoogleGenerationProductionSpendApprovalService.ApprovalConfirmation(maximum, confirmed),
                cancellationToken));
        serviceProvider.GetRequiredService<Stage7VoiceLabCatalogShellBinder>().Bind(viewModel);
        serviceProvider.GetRequiredService<Stage7VoiceLabAuditionShellBinder>().Bind(viewModel);
        Stage8RestoreRecoveryShellBinder.ConfigurePersistedRecovery(
            viewModel,
            serviceProvider.GetRequiredService<RestoreRecoveryExecutionCompositionFactory>(),
            serviceProvider.GetRequiredService<RestoreRecoveryProductionConfigurationResolver>(),
            serviceProvider.GetRequiredService<AtomicVerifiedRestoreExecutor>());
    }
}
