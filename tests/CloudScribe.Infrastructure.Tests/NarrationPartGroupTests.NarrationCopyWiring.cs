using CloudScribe.Application.Documents;
using CloudScribe.Infrastructure.DependencyInjection;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Fact]
    public async Task NarrationCopyArticleProductionStoreRegistrationGuardsResolvedService()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var n = await NarrationArticleAsync(f); var e = n.Evidence;
        var services = new ServiceCollection();
        services.AddCloudScribeInfrastructure(new ConfigurationBuilder().Build());
        services.AddSingleton<IDbContextFactory<CloudScribeDbContext>>(f);
        services.AddSingleton<IChirpArticleAdmissionStore>(e.Admission);
        services.AddSingleton(f.Paths);
        services.AddSingleton<TimeProvider>(e.Clock);
        services.AddSingleton<ChirpArticleService>(); // Same article registration as CompositionRoot.
        using var provider = services.BuildServiceProvider();
        Assert.IsType<EfNarrationCopyStore>(provider.GetRequiredService<INarrationCopyStore>());
        var service = provider.GetRequiredService<ChirpArticleService>();
        var review = await service.PrepareAsync(ChirpIntent(e), TestContext.Current.CancellationToken);
        await EditNarrationSourceAsync(f, n.Source);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApproveAsync(ChirpConfirmation(review), TestContext.Current.CancellationToken));
        Assert.Empty(await FinancialRowsAsync(f, "chirp_article_attempts"));
    }
}
