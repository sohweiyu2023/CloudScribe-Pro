using CloudScribe.App.ViewModels;
using CloudScribe.Application.Documents;
using CloudScribe.Application.Generation;
using CloudScribe.Application.Pricing;
using CloudScribe.Application.Providers;
using CloudScribe.Infrastructure.Configuration;
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
        RegisterCredentialRepair(builder.Services);
        RegisterProductionServices(builder.Services);
        RegisterShell(builder.Services);

        return builder.Build();
    }

    private static void RegisterProductionServices(IServiceCollection services)
    {
        services.AddSingleton<HttpClient>();
        RegisterPrivateProviderDiagnostics(services);
        services.AddSingleton<GoogleGenerationProductionTransportFactory>();
        services.AddSingleton<GoogleGenerationProductionRuntimeEvidenceResolver>();
        services.AddSingleton<GoogleGenerationProductionExecutionContextResolver>();
        services.AddSingleton<GoogleGenerationProductionRequestIntentStateOwner>();
        services.AddSingleton<GoogleGenerationProductionAuthorizationSnapshotStateOwner>();
        services.AddSingleton<GoogleGenerationProductionIntentEvidenceResolver>();
        services.AddSingleton<IGoogleGenerationProductionIntentEvidenceResolver>(serviceProvider =>
            serviceProvider.GetRequiredService<GoogleGenerationProductionIntentEvidenceResolver>());
        services.AddSingleton<GoogleGenerationProductionIntentAssemblyCoordinator>();
        services.AddSingleton<GoogleTtsStudioPreflightEvidenceResolver>();
        services.AddSingleton<GoogleTtsStudioRequestIntentFactory>();
        services.AddSingleton<GoogleTtsStudioTrustContextFactory>();
        services.AddSingleton<GoogleTtsStudioQueueStateResolver>();
        services.AddSingleton<GoogleTtsStudioCaptureCoordinator>();
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
        services.AddSingleton<BuiltInPricingCatalogBootstrapService>();
        services.AddSingleton<StudioPreferencesStore>();
        services.AddSingleton<LocalRequestStatusReader>();
        services.AddSingleton<GoogleTtsUsabilityHotfixBinder>();
        services.AddSingleton(serviceProvider => GoogleGenerationAcceptedMp3OutputService.WithArticleOutputRoot(
            Path.Combine(serviceProvider.GetRequiredService<AppPaths>().RootDirectory, "accepted-chirp-article-audio")));
        services.AddSingleton<INativeMediaTool, BoundedNativeMediaTool>();
        services.AddSingleton(serviceProvider => new InstalledRecoveryAdmissionStore(AppContext.BaseDirectory,
            serviceProvider.GetRequiredService<AppPaths>(), serviceProvider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IReviewedHistoricalAudioAnchorStore>(serviceProvider => serviceProvider.GetRequiredService<InstalledRecoveryAdmissionStore>());
        services.AddSingleton<IGoogleRecoveryPredecessorEvidenceResolver>(serviceProvider => serviceProvider.GetRequiredService<InstalledRecoveryAdmissionStore>());
        services.AddSingleton<IGoogleRecoveryBudgetEvidenceResolver, InstalledRecoveryBudgetEvidenceResolver>();
        services.AddSingleton<GoogleExplicitRecoveryService>();
        services.AddSingleton<IGoogleRecoveryCurrentCompilation>(serviceProvider => new GoogleRecoveryCurrentCompilation(
            serviceProvider.GetRequiredService<GoogleTtsStudioPreflightEvidenceResolver>(), serviceProvider.GetRequiredService<GoogleTtsStudioRequestIntentFactory>(),
            serviceProvider.GetRequiredService<GoogleTtsStudioTrustContextFactory>(), serviceProvider.GetRequiredService<GoogleGenerationProductionCompileAndPrepareService>(),
            serviceProvider.GetRequiredService<CloudScribe.Infrastructure.Pricing.V222ControlSet>(), serviceProvider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<GoogleRecoveryPreparationService>();
        RegisterArticleNarration(services);
    }

    private static void RegisterCredentialRepair(IServiceCollection services)
    {
        services.AddSingleton(provider => new GoogleCredentialRepairVerifier(new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
        })
        { Timeout = TimeSpan.FromSeconds(20) }, provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<ILoggerFactory>(),
            provider.GetRequiredService<CompletionWorkflowGate>()));
        services.AddSingleton<GoogleCredentialRepairJournalStore>();
        services.AddSingleton<GoogleCredentialRepairService>();
    }

    private static void RegisterArticleNarration(IServiceCollection services)
    {
        services.AddSingleton<IChirpArticleAdmissionStore>(serviceProvider => new InstalledChirpArticleAdmissionStore(AppContext.BaseDirectory,
            serviceProvider.GetRequiredService<AppPaths>(), serviceProvider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<ChirpArticleService>();
        services.AddSingleton<IChirpReservationResumeAdmissionStore>(provider => new InstalledChirpReservationResumeAdmissionStore(AppContext.BaseDirectory,
            provider.GetRequiredService<AppPaths>(), provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<ILogger<InstalledChirpReservationResumeAdmissionStore>>()));
        services.AddSingleton<ChirpArticlePreparationService>();
        services.AddSingleton<ArticleAudioExportService>();
    }

    private static void RegisterPrivateProviderDiagnostics(IServiceCollection services)
    {
        services.AddSingleton(serviceProvider =>
        {
            var paths = serviceProvider.GetRequiredService<AppPaths>();
            // A sibling of the exact configured profile, never a real-user fallback for isolated QA profiles.
            string root = Path.Combine(Path.GetDirectoryName(paths.RootDirectory)!, ".cloudscribe-private-diagnostics",
                InstalledRecoveryAdmissionStore.ProfileHash(paths.RootDirectory));
            return new GooglePrivateErrorDiagnostics(root, serviceProvider.GetRequiredService<TimeProvider>(),
                serviceProvider.GetRequiredService<CompletionWorkflowGate>());
        });
    }

    private static void RegisterShell(IServiceCollection services)
    {
        services.AddSingleton(serviceProvider =>
        {
            ShellViewModel viewModel = ActivatorUtilities.CreateInstance<ShellViewModel>(serviceProvider);
            ConfigureWorkflows(viewModel, serviceProvider);
            ConfigureFinalProductionStages(viewModel, serviceProvider);
            viewModel.ApplyFinalReleasePresentation();
            viewModel.ConfigureSavedVoiceSetups(serviceProvider.GetRequiredService<StudioPreferencesStore>());
            viewModel.ScheduleDocumentWorkspaceStart();
            viewModel.SchedulePricingHistoryStart();
            return viewModel;
        });
        services.AddSingleton(serviceProvider =>
        {
            MainWindow window = ActivatorUtilities.CreateInstance<MainWindow>(serviceProvider);
            DocumentWindowBehavior.Attach(window);
            serviceProvider.GetRequiredService<GoogleTtsUsabilityHotfixBinder>().Attach(
                window,
                serviceProvider.GetRequiredService<ShellViewModel>());
            return window;
        });
    }

    private static void ConfigureWorkflows(ShellViewModel viewModel, IServiceProvider serviceProvider)
    {
        viewModel.ConfigureStage3DocumentWorkflow(
            serviceProvider.GetRequiredService<IDocumentLibrary>(),
            serviceProvider.GetRequiredService<DocumentAutosaveCoordinator>(),
            serviceProvider.GetRequiredService<INarrationPartGroupStore>());
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
        ConfigureGooglePreparationAndSpend(viewModel, serviceProvider);
        ConfigureGoogleOutput(viewModel, serviceProvider);
        var recovery = serviceProvider.GetRequiredService<GoogleRecoveryPreparationService>();
        viewModel.ConfigureGoogleRecovery(recovery.PrepareAsync, recovery.ApproveAsync);
        var chirp = serviceProvider.GetRequiredService<ChirpArticlePreparationService>();
        viewModel.ConfigureChirpArticle(chirp.PrepareAsync, chirp.ApproveAsync, async (request, outcome, token) =>
        {
            string file = await serviceProvider.GetRequiredService<ChirpArticleService>().PersistAcceptedAsync(request, outcome, token).ConfigureAwait(false);
            using var localCompletion = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await serviceProvider.GetRequiredService<GoogleGenerationAcceptedMp3OutputService>().RegisterAcceptedAsync(outcome, file, localCompletion.Token).ConfigureAwait(false);
            return file;
        });
        serviceProvider.GetRequiredService<Stage7VoiceLabCatalogShellBinder>().Bind(viewModel);
        serviceProvider.GetRequiredService<Stage7VoiceLabAuditionShellBinder>().Bind(viewModel);
        Stage8RestoreRecoveryShellBinder.ConfigurePersistedRecovery(
            viewModel,
            serviceProvider.GetRequiredService<RestoreRecoveryExecutionCompositionFactory>(),
            serviceProvider.GetRequiredService<RestoreRecoveryProductionConfigurationResolver>(),
            serviceProvider.GetRequiredService<AtomicVerifiedRestoreExecutor>());
    }

    private static void ConfigureGooglePreparationAndSpend(
        ShellViewModel viewModel,
        IServiceProvider serviceProvider)
    {
        viewModel.ConfigureGoogleRequestInvalidation(() =>
        {
            serviceProvider.GetRequiredService<GoogleGenerationProductionPendingApprovalStateOwner>().Invalidate();
            serviceProvider.GetRequiredService<GoogleGenerationProductionSubmissionStateOwner>().Invalidate();
        });
        GoogleGenerationProductionIntentAssemblyCoordinator intentAssemblyCoordinator =
            serviceProvider.GetRequiredService<GoogleGenerationProductionIntentAssemblyCoordinator>();
        GoogleGenerationProductionPreparationCoordinator preparationCoordinator =
            serviceProvider.GetRequiredService<GoogleGenerationProductionPreparationCoordinator>();
        viewModel.ConfigureStage6GoogleGenerationPreparation(async cancellationToken =>
        {
            ShellViewModel.GoogleTtsStudioRequestSelection selection = viewModel.CaptureGoogleTtsStudioRequestSelection();
            await serviceProvider.GetRequiredService<GoogleTtsStudioCaptureCoordinator>()
                .CaptureAsync(selection, cancellationToken).ConfigureAwait(true);
            await intentAssemblyCoordinator.AssembleCurrentAsync(cancellationToken).ConfigureAwait(false);
            GoogleGenerationProductionPendingApprovalStateOwner.PendingState pending = await preparationCoordinator
                .PrepareCurrentAsync(cancellationToken)
                .ConfigureAwait(false);
            pending.Validate();
            return new ShellViewModel.GoogleGenerationSpendReview(
                pending.Currency,
                pending.Scale,
                pending.CurrentEstimateMinorUnits,
                pending.Envelope.PricingProvenanceId,
                pending.Envelope.VoiceName,
                pending.Envelope.CompiledPayloadSha256).Validate();
        });
        GoogleGenerationProductionSpendApprovalService approvalService =
            serviceProvider.GetRequiredService<GoogleGenerationProductionSpendApprovalService>();
        viewModel.ConfigureStage6GoogleGenerationSpendApproval((maximum, confirmed, cancellationToken) =>
            approvalService.ApproveExplicitAsync(
                new GoogleGenerationProductionSpendApprovalService.ApprovalConfirmation(maximum, confirmed),
                cancellationToken));
    }

    private static void ConfigureGoogleOutput(ShellViewModel viewModel, IServiceProvider serviceProvider)
    {
        GoogleGenerationAcceptedMp3OutputService output =
            serviceProvider.GetRequiredService<GoogleGenerationAcceptedMp3OutputService>();
        viewModel.ConfigureStage6GoogleGenerationOutput(
            async (outcome, cancellationToken) =>
            {
                GoogleGenerationAcceptedMp3Output persisted = await output
                    .PersistAcceptedAsync(outcome, cancellationToken)
                    .ConfigureAwait(false);
                return persisted.Path;
            },
            output.PlayAsync);
        viewModel.ConfigureArticleAudioRecording(serviceProvider.GetRequiredService<ArticleAudioExportService>().RecordAcceptedAsync);
    }
}
