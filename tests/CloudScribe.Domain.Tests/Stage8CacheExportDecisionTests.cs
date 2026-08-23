using CloudScribe.Domain.Safety;

namespace CloudScribe.Domain.Tests;

public sealed class Stage8CacheExportDecisionTests
{
    [Fact]
    public void Export_requires_explicit_request_and_current_policy()
    {
        Assert.False(CacheExportDecision.CanExport(false, true, false, false));
        Assert.False(CacheExportDecision.CanExport(true, false, false, false));
        Assert.True(CacheExportDecision.CanExport(true, true, false, false));
    }

    [Fact]
    public void Protected_or_unresolved_entries_never_export()
    {
        Assert.False(CacheExportDecision.CanExport(true, true, true, false));
        Assert.False(CacheExportDecision.CanExport(true, true, false, true));
    }
}
