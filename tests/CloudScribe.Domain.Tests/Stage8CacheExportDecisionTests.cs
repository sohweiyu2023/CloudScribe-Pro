using CloudScribe.Domain.Safety;

namespace CloudScribe.Domain.Tests;

public sealed class Stage8CacheExportDecisionTests
{
    [Fact]
    public void Export_requires_explicit_request_and_current_policy()
    {
        Assert.False(CacheExportDecisionPolicy.Evaluate(false, true, false, false).IncludeCache);
        Assert.False(CacheExportDecisionPolicy.Evaluate(true, false, false, false).IncludeCache);
        Assert.True(CacheExportDecisionPolicy.Evaluate(true, true, false, false).IncludeCache);
    }

    [Fact]
    public void Protected_or_unresolved_entries_never_export()
    {
        Assert.False(CacheExportDecisionPolicy.Evaluate(true, true, true, false).IncludeCache);
        Assert.False(CacheExportDecisionPolicy.Evaluate(true, true, false, true).IncludeCache);
    }
}
