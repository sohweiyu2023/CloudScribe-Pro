using CloudScribe.Infrastructure.DependencyInjection;
using CloudScribe.Infrastructure.Safety;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CloudScribe.Infrastructure.Tests;

public sealed class Stage8ProductionSafetyRegistrationTests
{
    [Fact]
    public void AddCloudScribeInfrastructure_RegistersRestoreRecoveryProductionServicesAsSingletons()
    {
        var services = new ServiceCollection();
        IConfiguration configuration = new ConfigurationBuilder().Build();

        services.AddCloudScribeInfrastructure(configuration);

        AssertSingleton<AtomicVerifiedRestoreExecutor>(services);
        AssertSingleton<RestoreRecoveryExecutionCompositionFactory>(services);
    }

    private static void AssertSingleton<TService>(IServiceCollection services)
    {
        ServiceDescriptor descriptor = Assert.Single(
            services.Where(static descriptor => descriptor.ServiceType == typeof(TService)));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        Assert.Equal(typeof(TService), descriptor.ImplementationType);
    }
}
