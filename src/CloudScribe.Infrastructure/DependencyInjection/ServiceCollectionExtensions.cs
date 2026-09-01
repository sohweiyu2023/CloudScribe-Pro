using CloudScribe.Application.Activation;
using CloudScribe.Application.Diagnostics;
using CloudScribe.Application.Documents;
using CloudScribe.Application.Generation;
using CloudScribe.Application.Observability;
using CloudScribe.Application.Pricing;
using CloudScribe.Application.Providers;
using CloudScribe.Application.Security;
using CloudScribe.Application.Startup;
using CloudScribe.Infrastructure.Activation;
using CloudScribe.Infrastructure.Configuration;
using CloudScribe.Infrastructure.Diagnostics;
using CloudScribe.Infrastructure.Files;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Infrastructure.Persistence;
using CloudScribe.Infrastructure.Pricing;
using CloudScribe.Infrastructure.Providers;
using CloudScribe.Infrastructure.Safety;
using CloudScribe.Infrastructure.Security;
using CloudScribe.Providers.Abstractions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudScribe.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCloudScribeInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        AddCoreServices(services, configuration);
        AddPersistenceServices(services);
        AddProviderAndTrustServices(services, configuration);
        AddGenerationSupportServices(services);
        AddSafetyServices(services);
        return services;
    }

    private static void AddCoreServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddOptions<CloudScribeOptions>()
            .Bind(configuration.GetSection(CloudScribeOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<CloudScribeOptions>, CloudScribeOptionsValidator>();
        services.AddSingleton<AppPaths>();
        services.AddSingleton<BoundedJsonFileLoggerProvider>();
        services.AddSingleton<ILoggerProvider>(serviceProvider =>
            serviceProvider.GetRequiredService<BoundedJsonFileLoggerProvider>());
        services.AddSingleton<IDiagnosticLogStatus>(serviceProvider =>
            serviceProvider.GetRequiredService<BoundedJsonFileLoggerProvider>());
        services.AddSingleton<ISupportBundleService, SupportBundleService>();
        services.AddSingleton<IActivationRouter, ActivationRouter>();
        services.AddSingleton<ISingleInstanceCoordinator, SingleInstanceCoordinator>();
    }

    private static void AddPersistenceServices(IServiceCollection services)
    {
        services.AddPooledDbContextFactory<CloudScribeDbContext>((serviceProvider, builder) =>
        {
            AppPaths paths = serviceProvider.GetRequiredService<AppPaths>();
            paths.EnsureDatabaseDirectory();
            SqliteConnectionStringBuilder connection = new()
            {
                DataSource = paths.DatabasePath,
                Cache = SqliteCacheMode.Shared,
                Pooling = true,
                ForeignKeys = true,
                DefaultTimeout = 5,
            };
            builder.UseSqlite(connection.ConnectionString);
        });
        services.AddSingleton<LegacyDatabaseMigrationBridge>();
        services.AddSingleton<DocumentContentStore>();
        services.AddSingleton<IDocumentLibrary, EfDocumentLibrary>();
        services.AddTransient<DocumentAutosaveCoordinator>();
        services.AddSingleton<ILocalDocumentImporter, BoundedLocalDocumentImporter>();
        services.AddSingleton<DocumentPreprocessor>();
        services.AddSingleton<IActivityTimelineStore, EfActivityTimelineStore>();
        services.AddSingleton<IBillableOperationLedger, EfBillableOperationLedger>();
        services.AddSingleton<IApplicationInitializer, DatabaseInitializer>();
    }

    private static void AddProviderAndTrustServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IProviderFactoryRegistry, ProviderFactoryRegistry>();
        services.AddSingleton<IProviderAccountStore, EfProviderAccountStore>();
        services.AddSingleton<IProviderCapabilitySnapshotStore, EfProviderCapabilitySnapshotStore>();
        services.AddSingleton<GoogleGenerationProductionEvidenceResolver>();
        services.AddSingleton<StrictJsonObjectReader>();
        services.AddSingleton<ExactPricingControlMaterialInspector>();
        services.AddSingleton<V222ControlSet>();
        services.AddOptions<PricingCatalogTrustOptions>()
            .Bind(configuration.GetSection(PricingCatalogTrustOptions.SectionName));
        services.AddSingleton<IPricingCatalogContractValidator, AdmittedV222PricingCatalogContractValidator>();
        services.AddSingleton<IPricingCatalogSignatureVerifier, Ed25519PricingCatalogSignatureVerifier>();
        services.AddSingleton<IPricingCatalogAdmissionService, PricingCatalogAdmissionService>();
        services.AddSingleton<IPricingCatalogHistoryStore, EfPricingCatalogHistoryStore>();
        services.AddSingleton<IPricingContractOverrideStore, EfPricingContractOverrideStore>();
        services.AddSingleton<ICredentialVault, WindowsCredentialVault>();
        services.AddSingleton<ITransientCredentialResolver, VaultBackedTransientCredentialResolver>();
        services.AddSingleton<IGenerationPrivateCacheKeyProvider, VaultBackedGenerationPrivateCacheKeyProvider>();
    }

    private static void AddGenerationSupportServices(IServiceCollection services)
    {
        services.AddSingleton<GenerationSupportBundleService>();
        services.AddSingleton<GenerationSupportBundleMetadataFileStore>();
        services.AddSingleton(serviceProvider =>
            new GenerationSupportBundleExportCoordinator(
                serviceProvider.GetRequiredService<GenerationSupportBundleService>(),
                serviceProvider.GetRequiredService<GenerationSupportBundleMetadataFileStore>().PersistAsync));
    }

    private static void AddSafetyServices(IServiceCollection services)
    {
        services.AddSingleton<AtomicVerifiedRestoreExecutor>();
        services.AddSingleton<RestoreRecoveryExecutionCompositionFactory>();
    }
}
