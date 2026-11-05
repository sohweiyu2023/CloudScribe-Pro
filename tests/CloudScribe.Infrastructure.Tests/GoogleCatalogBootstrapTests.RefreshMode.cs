namespace CloudScribe.Infrastructure.Tests;

public sealed partial class GoogleCatalogBootstrapTests
{
    [Fact]
    public void OrdinaryConnectionDoesNotRequireReviewedRefresh()
    {
        using var fixture = new CompletionFixture();
        Assert.False(fixture.Service.RequiresReviewedRefresh);
        Assert.Equal(0, fixture.Handler.Calls);
        Assert.Equal(0, fixture.Runner.Refreshes);
    }

    [Fact]
    public async Task InspectingRestrictedModeDoesNotConsumeAClaimOrCallTheProvider()
    {
        using var fixture = new CompletionFixture();
        await fixture.InitializeAsync().ConfigureAwait(true);
        int calls = fixture.Handler.Calls;
        Assert.True(fixture.Service.RequiresReviewedRefresh);
        Assert.Equal(calls, fixture.Handler.Calls);
        Assert.Equal(0, fixture.Runner.Refreshes);
        Assert.Null(fixture.Runner.Pending);
    }
}
