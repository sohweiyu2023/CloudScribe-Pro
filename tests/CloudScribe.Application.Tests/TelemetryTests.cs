using CloudScribe.Application.Telemetry;

namespace CloudScribe.Application.Tests;

public sealed class TelemetryTests
{
    [Fact]
    public void UsesStableActivitySourceName()
    {
        Assert.Equal("CloudScribe.Pro", CloudScribeTelemetry.ActivitySource.Name);
    }
}
