using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class GoogleCatalogBootstrapTests
{
    [Fact]
    public async Task PreparationRoutesActualBootstrapThroughSeparateLeaseAndNeverEnablesPaidGate()
    {
        using var fixture = new CompletionFixture();
        await fixture.InitializeAsync().ConfigureAwait(true);
        fixture.Configuration = fixture.Configuration with { PreparationSha256 = new string('b', 64) };
        Guid refresh = Guid.NewGuid();
        using (var owner = fixture.Service.BeginUiRefresh(refresh))
        {
            await fixture.Service.RefreshConfiguredAsync(refresh, TestContext.Current.CancellationToken).ConfigureAwait(true);
            var account = (await fixture.Stores.ListAsync(TestContext.Current.CancellationToken).ConfigureAwait(true))[0];
            await fixture.Catalog.LoadAsync(account.Reference, CompletionCatalogEndpoint, TestContext.Current.CancellationToken).ConfigureAwait(true);
            Assert.Throws<CompletionGateException>(() => fixture.Gate.BeforeSynthesis("synthetic-credential",
                CompletionSynthesisEndpoint, "{\"input\":\"not permitted\"}"u8.ToArray(), Guid.NewGuid()));
            owner.Succeed();
        }
        Assert.Equal(1, fixture.Runner.Refreshes);
        Assert.Contains("claim-preparation", fixture.Runner.Commands, StringComparer.Ordinal);
        Assert.Contains("complete-preparation", fixture.Runner.Commands, StringComparer.Ordinal);
        Assert.DoesNotContain("claim-refresh", fixture.Runner.Commands, StringComparer.Ordinal);
        Assert.Throws<InvalidOperationException>(fixture.Gate.RequireReady);
        Assert.Throws<InvalidOperationException>(() => fixture.Gate.BeforeSynthesis("synthetic-credential",
            CompletionSynthesisEndpoint, "{}"u8.ToArray(), Guid.NewGuid()));
    }
}
