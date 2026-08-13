using CloudScribe.Application.Activation;
using CloudScribe.Application.Diagnostics;
using CloudScribe.Application.Documents;
using CloudScribe.Application.Observability;
using CloudScribe.Application.Startup;
using CloudScribe.Infrastructure.Activation;
using CloudScribe.Infrastructure.Configuration;
using CloudScribe.Infrastructure.Diagnostics;
using CloudScribe.Infrastructure.Files;
using CloudScribe.Infrastructure.Persistence;
using CloudScribe.Infrastructure.Providers;
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
        services.AddSingleton<IActivityTimelineStore, EfActivityTimelineStore>();
        services.AddSingleton<IBillableOperationLedger, EfBillableOperationLedger>();
        services.AddSingleton<IApplicationInitializer, DatabaseInitializer>();

        services.AddSingleton<IProviderFactoryRegistry, ProviderFactoryRegistry>();
        return services;
    }
}
