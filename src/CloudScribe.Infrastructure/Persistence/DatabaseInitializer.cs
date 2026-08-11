using CloudScribe.Application.Logging;
using CloudScribe.Application.Startup;
using CloudScribe.Infrastructure.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CloudScribe.Infrastructure.Persistence;

public sealed class DatabaseInitializer(
    AppPaths appPaths,
    IDbContextFactory<ObservabilityDbContext> contextFactory,
    ILogger<DatabaseInitializer> logger) : IApplicationInitializer
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        appPaths.EnsureDatabaseDirectory();
        using ObservabilityDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await context.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        CloudScribeLog.DatabaseInitialized(logger);
    }
}
